using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace SupportSatchel.Core.Collecting;

/// <summary>
/// Gitignore-flavoured glob matcher used by folder collectors to apply
/// include/exclude patterns from a <see cref="Domain.CaptureSource"/>.
/// Paths and patterns are normalized to forward slashes before matching.
/// </summary>
/// <remarks>
/// Pattern semantics:
/// <list type="bullet">
/// <item>A pattern containing '/' must match the whole path relative to the
/// source root (e.g. <c>logs/*.log</c>).</item>
/// <item>A pattern without '/' matches the file name at any depth
/// (e.g. <c>*.log</c> matches <c>sub/dir/app.log</c>).</item>
/// <item><c>*</c> and <c>?</c> never cross path separators; <c>**</c> does.
/// <c>**/</c> matches zero or more leading segments.</item>
/// <item>Matching is case-insensitive on Windows and case-sensitive on
/// Unix-like systems, mirroring the host file system.</item>
/// </list>
/// </remarks>
public static class GlobMatcher
{
    private static readonly ConcurrentDictionary<string, Regex> Cache =
        new(StringComparer.Ordinal);

    private static RegexOptions CaseSensitivity =>
        OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None;

    /// <summary>Normalizes path separators to '/' for matching.</summary>
    public static string NormalizePath(string path) => path.Replace('\\', '/');

    /// <summary>True when <paramref name="relativePath"/> matches the glob.</summary>
    public static bool IsMatch(string relativePath, string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        return Cache.GetOrAdd(pattern, BuildRegex).IsMatch(NormalizePath(relativePath));
    }

    /// <summary>True when any pattern matches; empty lists never match.</summary>
    public static bool MatchesAny(string relativePath, IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        foreach (var pattern in patterns)
        {
            if (IsMatch(relativePath, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Selection predicate: when no include patterns are configured every
    /// file is a candidate; exclude patterns always win.
    /// </summary>
    public static bool IsSelected(
        string relativePath,
        IReadOnlyList<string> includePatterns,
        IReadOnlyList<string> excludePatterns)
    {
        ArgumentNullException.ThrowIfNull(includePatterns);
        ArgumentNullException.ThrowIfNull(excludePatterns);

        if (includePatterns.Count > 0 && !MatchesAny(relativePath, includePatterns))
        {
            return false;
        }

        return !MatchesAny(relativePath, excludePatterns);
    }

    private static Regex BuildRegex(string pattern)
    {
        var normalized = NormalizePath(pattern).TrimStart('/');
        var text = normalized.Contains('/', StringComparison.Ordinal)
            ? normalized
            : string.Concat("**/", normalized);

        var sb = new StringBuilder("^");
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            switch (c)
            {
                case '*':
                    if (i + 1 < text.Length && text[i + 1] == '*')
                    {
                        if (i + 2 < text.Length && text[i + 2] == '/')
                        {
                            sb.Append("(?:[^/]*/)*");
                            i += 3;
                        }
                        else
                        {
                            sb.Append(".*");
                            i += 2;
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*");
                        i += 1;
                    }

                    break;
                case '?':
                    sb.Append("[^/]");
                    i += 1;
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    i += 1;
                    break;
            }
        }

        sb.Append('$');
        return new Regex(
            sb.ToString(),
            RegexOptions.Compiled | RegexOptions.CultureInvariant | CaseSensitivity);
    }
}
