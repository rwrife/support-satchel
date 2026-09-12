using System.Text;
using System.Text.RegularExpressions;
using SupportSatchel.Core.Collecting;
using SupportSatchel.Core.Domain;

namespace SupportSatchel.Core.Redacting;

/// <summary>
/// Applies profile redaction rules to staged copies of collected evidence.
/// The engine is the middle stage of collect → redact → package:
/// <list type="bullet">
/// <item><description>It only ever writes inside
/// <c>{workspace}/{Collector.StagingDirectory}</c>; source files the
/// collector copied from are never in reach, and provenance sidecars are
/// never modified.</description></item>
/// <item><description>
/// <see cref="PreviewWorkspace"/> is read-only and yields the original vs
/// redacted text per artifact for the review step (issue #6);
/// <see cref="ApplyWorkspace"/> performs the masking and writes a JSON
/// report but never deletes evidence.</description></item>
/// <item><description>Binary (non-UTF-8) artifacts are left untouched —
/// redaction is a text-level guarantee, documented in
/// <c>docs/redaction.md</c>.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Determinism and safety contract (tested):
/// <list type="bullet">
/// <item>Enabled rules run strictly in list order; each rule sees the text
/// produced by its predecessors, so match indexes in previews are stable
/// and reproducible.</item>
/// <item>Rule regexes are compiled with a timeout. A rule that exceeds the
/// timeout aborts the artifact fail-closed: the staged copy keeps its
/// un-redacted content and the run records an error, so packaging (issue
/// #5) can refuse to export rather than silently ship secrets.</item>
/// <item>All report timestamps come from <see cref="RedactionOptions.Clock"/>
/// and artifact lists are ordinal-sorted, so two runs over identical
/// workspaces produce byte-identical reports.</item>
/// </list>
/// </remarks>
public sealed class RedactionEngine
{
    /// <summary>Workspace-relative name of the redaction report.</summary>
    public const string ReportFileName = "redaction-report.json";

    /// <summary>Snippets longer than this are truncated for display.</summary>
    public const int MaxSnippetChars = 48;

    /// <summary>
    /// Byte-order-mark prefix of UTF-8; stripped before redaction and
    /// re-emitted when the artifact is rewritten.
    /// </summary>
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Strict UTF-8 decoder: unlike <see cref="Encoding.UTF8"/> it throws on
    /// invalid byte sequences, which is how the engine distinguishes text
    /// artifacts from binary ones.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Redacts one text in memory. Useful for previews and tests; matches
    /// reference positions in the text as each rule saw it.
    /// </summary>
    public TextRedactionPreview PreviewText(
        string text,
        RedactionSettings settings,
        RedactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(settings);
        return RunRules(text, settings, ResolveTimeout(options)).Preview;
    }

    /// <summary>
    /// Read-only per-artifact preview of a collector workspace: for every
    /// staged artifact this reports the original staged text and the exact
    /// text an export would contain, plus every rule hit. Nothing on disk
    /// is modified.
    /// </summary>
    public WorkspaceRedactionPreview PreviewWorkspace(
        CollectionResult collection,
        RedactionSettings settings,
        RedactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(settings);

        var timeout = ResolveTimeout(options);

        var previews = new List<ArtifactRedactionPreview>();
        var errors = new List<string>();

        foreach (var artifact in collection.Artifacts)
        {
            var stagedFull = StagedFullPath(collection.WorkspacePath, artifact.StagedPath);
            var (kind, original, error) = TryReadStagedText(stagedFull);
            if (error is not null)
            {
                errors.Add($"{artifact.StagedPath}: {error}");
            }

            if (kind != ArtifactRedactionKind.Text)
            {
                previews.Add(new ArtifactRedactionPreview
                {
                    StagedPath = artifact.StagedPath,
                    Kind = kind,
                    Matches = [],
                });
                continue;
            }

            var run = RunRules(original!, settings, timeout);
            previews.Add(new ArtifactRedactionPreview
            {
                StagedPath = artifact.StagedPath,
                Kind = ArtifactRedactionKind.Text,
                OriginalText = run.Preview.OriginalText,
                RedactedText = run.AbortedRuleId is null ? run.Preview.RedactedText : null,
                Matches = run.Preview.Matches,
            });

            if (run.AbortedRuleId is not null)
            {
                errors.Add(
                    $"{artifact.StagedPath}: rule '{run.AbortedRuleId}' aborted (invalid pattern or timeout); preview incomplete.");
            }
        }

        errors.Sort(StringComparer.Ordinal);
        return new WorkspaceRedactionPreview { Artifacts = previews, Errors = errors };
    }

    /// <summary>
    /// Applies <paramref name="settings"/> to the staged copies of a
    /// collector workspace, rewriting text artifacts in place (inside the
    /// workspace only) and writing <see cref="ReportFileName"/> at the
    /// workspace root. Re-running on an already-redacted workspace is safe
    /// (idempotent on text that no longer matches).
    /// </summary>
    public RedactionRunResult ApplyWorkspace(
        CollectionResult collection,
        RedactionSettings settings,
        RedactionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentNullException.ThrowIfNull(settings);

        options ??= new RedactionOptions();
        var now = options.Clock();
        var timeout = options.PerRuleTimeout;

        var outcomes = new List<RedactedArtifact>();
        var errors = new List<string>();

        foreach (var artifact in collection.Artifacts)
        {
            var stagedFull = StagedFullPath(collection.WorkspacePath, artifact.StagedPath);
            var (kind, original, error) = TryReadStagedText(stagedFull);
            if (error is not null)
            {
                errors.Add($"{artifact.StagedPath}: {error}");
            }

            if (kind != ArtifactRedactionKind.Text)
            {
                outcomes.Add(new RedactedArtifact
                {
                    StagedPath = artifact.StagedPath,
                    Kind = kind,
                    Redacted = false,
                    Matches = [],
                });
                continue;
            }

            var run = RunRules(original!, settings, timeout);
            var hasBom = HasUtf8Bom(stagedFull);

            if (run.AbortedRuleId is not null)
            {
                // Fail closed: leave the un-redacted copy, but never let an
                // aborted artifact be exported.
                errors.Add(
                    $"{artifact.StagedPath}: rule '{run.AbortedRuleId}' aborted (invalid pattern or timeout); artifact left un-redacted.");
                outcomes.Add(new RedactedArtifact
                {
                    StagedPath = artifact.StagedPath,
                    Kind = ArtifactRedactionKind.Text,
                    Redacted = false,
                    Matches = run.Preview.Matches,
                });
                continue;
            }

            var changed = run.Preview.Changed;
            if (changed)
            {
                WriteStagedText(stagedFull, run.Preview.RedactedText, hasBom);
            }

            outcomes.Add(new RedactedArtifact
            {
                StagedPath = artifact.StagedPath,
                Kind = ArtifactRedactionKind.Text,
                Redacted = changed,
                Matches = run.Preview.Matches,
            });
        }

        errors.Sort(StringComparer.Ordinal);

        var report = new RedactionReport
        {
            GeneratedAtUtc = now,
            Artifacts = outcomes,
            Errors = errors,
            IsSuccessful = errors.Count == 0,
        };

        var reportPath = Path.Combine(collection.WorkspacePath, ReportFileName);
        File.WriteAllText(
            reportPath,
            RedactionReportJson.Serialize(report),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new RedactionRunResult
        {
            WorkspacePath = collection.WorkspacePath,
            ReportPath = reportPath,
            Artifacts = outcomes,
            Errors = errors,
        };
    }

    private static string StagedFullPath(string workspacePath, string stagedPath) =>
        Path.Combine(
            workspacePath,
            Collector.StagingDirectory,
            stagedPath.Replace('/', Path.DirectorySeparatorChar));

    private static (ArtifactRedactionKind Kind, string? Text, string? Error) TryReadStagedText(
        string stagedFull)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(stagedFull))
            {
                return (ArtifactRedactionKind.Missing, null, "staged copy is missing on disk");
            }

            bytes = File.ReadAllBytes(stagedFull);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (ArtifactRedactionKind.Binary, null, $"staged copy unreadable ({ex.GetType().Name})");
        }

        // Text artifacts are UTF-8 (with optional BOM). Anything that does
        // not decode strictly is treated as binary and left untouched;
        // see docs/redaction.md for the documented limits.
        try
        {
            var hasBom = bytes.Length >= 3
                && bytes[0] == Utf8Bom[0] && bytes[1] == Utf8Bom[1] && bytes[2] == Utf8Bom[2];
            var text = StrictUtf8.GetString(hasBom ? bytes.AsSpan(3) : bytes);
            return (ArtifactRedactionKind.Text, text, null);
        }
        catch (DecoderFallbackException)
        {
            return (ArtifactRedactionKind.Binary, null, null);
        }
    }

    private static bool HasUtf8Bom(string stagedFull)
    {
        using var probe = File.OpenRead(stagedFull);
        var head = new byte[3];
        var read = 0;
        while (read < 3)
        {
            var n = probe.Read(head, read, 3 - read);
            if (n == 0)
            {
                break;
            }

            read += n;
        }

        return read == 3
            && head[0] == Utf8Bom[0] && head[1] == Utf8Bom[1] && head[2] == Utf8Bom[2];
    }

    private static void WriteStagedText(string path, string text, bool emitBom)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            text,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: emitBom));
    }

    private RuleRun RunRules(string text, RedactionSettings settings, TimeSpan timeout)
    {
        var current = text;
        var matches = new List<RedactionMatch>();
        string? abortedRuleId = null;

        foreach (var rule in settings.Rules)
        {
            if (!rule.Enabled)
            {
                continue;
            }

            Regex regex;
            try
            {
                regex = new Regex(
                    rule.Pattern,
                    RegexOptions.None,
                    timeout);
            }
            catch (ArgumentException)
            {
                // ProfileValidator normally rejects these; if one slips
                // through, fail closed on this artifact.
                abortedRuleId = rule.Id;
                break;
            }

            string replacementText;
            var ruleMatches = new List<RedactionMatch>();
            try
            {
                replacementText = regex.Replace(
                    current,
                    match =>
                    {
                        ruleMatches.Add(new RedactionMatch(
                            rule.Id,
                            match.Index,
                            match.Length,
                            TruncateSnippet(match.Value)));
                        return ExpandReplacement(rule.Replacement, match);
                    });
            }
            catch (RegexMatchTimeoutException)
            {
                abortedRuleId = rule.Id;
                break;
            }

            current = replacementText;
            matches.AddRange(ruleMatches);
        }

        return new RuleRun(
            new TextRedactionPreview
            {
                OriginalText = text,
                RedactedText = current,
                Matches = matches,
            },
            abortedRuleId);
    }

    // Explicit expansion so an empty or group-less replacement behaves the
    // same on every platform, and '$'-only literals in replacements are not
    // silently swallowed.
    private static string ExpandReplacement(string replacement, Match match)
    {
        if (string.IsNullOrEmpty(replacement) || !replacement.Contains('$', StringComparison.Ordinal))
        {
            return replacement ?? string.Empty;
        }

        try
        {
            return match.Result(replacement);
        }
        catch (ArgumentException)
        {
            // Malformed substitution: treat the replacement literally
            // rather than aborting the run.
            return replacement;
        }
    }

    private static string TruncateSnippet(string value) =>
        value.Length <= MaxSnippetChars
            ? value
            : value[..MaxSnippetChars] + "…";

    private static TimeSpan ResolveTimeout(RedactionOptions? options)
    {
        var timeout = options?.PerRuleTimeout ?? new RedactionOptions().PerRuleTimeout;
        if (timeout <= TimeSpan.Zero && timeout != System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentException(
                "PerRuleTimeout must be positive or Timeout.InfiniteTimeSpan.",
                nameof(options));
        }

        return timeout;
    }

    private readonly record struct RuleRun(TextRedactionPreview Preview, string? AbortedRuleId);
}
