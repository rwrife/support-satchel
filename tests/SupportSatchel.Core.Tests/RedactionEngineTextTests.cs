using SupportSatchel.Core.Domain;
using SupportSatchel.Core.Redacting;
using Xunit;

namespace SupportSatchel.Core.Tests;

/// <summary>
/// Rule-level tests for the redaction engine: default-rule coverage of
/// common secret/PII shapes, sequential rule semantics, and the documented
/// false-positive/false-negative limits (see docs/redaction.md).
/// </summary>
public class RedactionEngineTextTests
{
    private readonly RedactionEngine engine = new();

    private static RedactionSettings Rules(params RedactionRule[] rules) =>
        new() { Rules = rules };

    [Fact]
    public void DefaultRulesRedactEmailsBearerTokensKeyValuesAndHostHints()
    {
        const string Text = """
            contact: jane.doe@example.com
            auth: Authorization: Bearer abcdef0123456789ABCDEF-_
            cfg: api_key = "sk-live-0123456789abcdef"
            env: hostname: build-wks-42
            """;

        var preview = engine.PreviewText(Text, RedactionSettings.CreateDefault());

        Assert.True(preview.Changed);
        Assert.DoesNotContain("jane.doe@example.com", preview.RedactedText, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdef0123456789ABCDEF-_", preview.RedactedText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-live-0123456789abcdef", preview.RedactedText, StringComparison.Ordinal);
        Assert.DoesNotContain("build-wks-42", preview.RedactedText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", preview.RedactedText, StringComparison.Ordinal);

        var ruleIds = preview.Matches.Select(m => m.RuleId).Distinct().ToList();
        Assert.Contains("builtin.email", ruleIds);
        Assert.Contains("builtin.bearer-token", ruleIds);
        Assert.Contains("builtin.api-key", ruleIds);
        Assert.Contains("builtin.host-user", ruleIds);
    }

    [Fact]
    public void DefaultRulesLeaveBenignTextUntouched()
    {
        const string Text = """
            info: application started, version 3.2.1
            warn: cache miss for key session-index
            info: processed 128 records in 42ms
            """;

        var preview = engine.PreviewText(Text, RedactionSettings.CreateDefault());

        Assert.False(preview.Changed);
        Assert.Empty(preview.Matches);
        Assert.Equal(Text, preview.RedactedText);
    }

    [Fact]
    public void EmailRuleHandlesPlusTagsAndSubdomains()
    {
        const string Text = "mail first.last+tag@sub.domain.co.uk now";

        var preview = engine.PreviewText(Text, RedactionSettings.CreateDefault());

        Assert.Equal("mail [REDACTED] now", preview.RedactedText);
        var match = Assert.Single(preview.Matches);
        Assert.Equal("first.last+tag@sub.domain.co.uk",
            Text.Substring(match.Index, match.Length));
    }

    [Fact]
    public void EmailRuleDoesNotMatchLabellessHost_DocumentedLimit()
    {
        // Documented false negative: the email pattern requires a dotted
        // TLD, so local-part-only addresses such as unix socket names or
        // "user@localhost" pass through un-redacted.
        const string Text = "socket user@localhost accepted";

        var preview = engine.PreviewText(Text, RedactionSettings.CreateDefault());

        Assert.False(preview.Changed);
        Assert.Contains("user@localhost", preview.RedactedText, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortKeyValueSecretsAreNotMasked_DocumentedLimit()
    {
        // Documented false negative: values shorter than 12 characters look
        // like ordinary config words, so the key=value rule ignores them.
        const string Text = "token: abc123";

        var preview = engine.PreviewText(Text, RedactionSettings.CreateDefault());

        Assert.False(preview.Changed);
    }

    [Fact]
    public void BearerRuleIsCaseInsensitive()
    {
        var preview = engine.PreviewText(
            "header: BEARER abcdef0123456789abcdef",
            RedactionSettings.CreateDefault());

        Assert.DoesNotContain("abcdef0123456789abcdef", preview.RedactedText, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledRulesAreSkipped()
    {
        var settings = Rules(new RedactionRule
        {
            Id = "custom.email",
            Name = "Emails",
            Pattern = @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
            Enabled = false,
        });

        var preview = engine.PreviewText("mail a@b.com", settings);

        Assert.False(preview.Changed);
        Assert.Empty(preview.Matches);
    }

    [Fact]
    public void RulesRunSequentiallyAndLaterRulesSeeEarlierOutput()
    {
        // Rule 1 turns foo->bar, rule 2 turns bar->foo. Sequential semantics
        // mean "foo bar" becomes "bar bar" then "foo foo" with no repeated
        // scanning: fixed-point behaviour (which would loop forever here)
        // is explicitly NOT how the engine works.
        var settings = Rules(
            new RedactionRule { Id = "r1", Name = "R1", Pattern = "foo", Replacement = "bar" },
            new RedactionRule { Id = "r2", Name = "R2", Pattern = "bar", Replacement = "foo" });

        var preview = engine.PreviewText("foo bar", settings);

        Assert.Equal("foo foo", preview.RedactedText);
        // r1 hits once ("foo"), r2 then hits twice on "bar bar".
        Assert.Equal(3, preview.Matches.Count);
        Assert.Equal(["r1", "r2", "r2"], preview.Matches.Select(m => m.RuleId));
    }

    [Fact]
    public void ReplacementMayReferenceCaptureGroups()
    {
        var settings = Rules(new RedactionRule
        {
            Id = "custom.keep-key",
            Name = "Keep key name",
            Pattern = @"(?i)\b(?<key>token)\b\s*[:=]\s*[A-Za-z0-9+/=_\-\.]{12,}",
            Replacement = "${key}=[REDACTED]",
        });

        var preview = engine.PreviewText("token: abcdef0123456789", settings);

        Assert.Equal("token=[REDACTED]", preview.RedactedText);
    }

    [Fact]
    public void MatchSplicesReconstructAgainstTextAtApplicationTime()
    {
        // Each rule's match indexes refer to the text as that rule saw it,
        // i.e. after all earlier rules already ran.
        var settings = Rules(
            new RedactionRule { Id = "r1", Name = "R1", Pattern = "aaa", Replacement = "ZZ" },
            new RedactionRule { Id = "r2", Name = "R2", Pattern = "bbb", Replacement = "[REDACTED]" });

        var preview = engine.PreviewText("aaa bbb aaa", settings);

        // r1 ran on "aaa bbb aaa" -> both hits at 0 and 8.
        var r1Matches = preview.Matches.Where(m => m.RuleId == "r1").ToList();
        Assert.Equal([0, 8], r1Matches.Select(m => m.Index));

        // r2 ran on the *redacted* text "ZZ bbb ZZ" -> single hit at index 3.
        var r2Match = Assert.Single(preview.Matches, m => m.RuleId == "r2");
        Assert.Equal(3, r2Match.Index);
        Assert.Equal(3, r2Match.Length);
        Assert.Equal("ZZ [REDACTED] ZZ", preview.RedactedText);
    }

    [Fact]
    public void SnippetsAreTruncatedForDisplay()
    {
        var longValue = new string('k', 100);
        var preview = engine.PreviewText(
            $"api_key={longValue}",
            RedactionSettings.CreateDefault());

        var match = Assert.Single(preview.Matches);
        Assert.Equal(RedactionEngine.MaxSnippetChars + 1, match.Snippet.Length);
        Assert.EndsWith("…", match.Snippet, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidPatternFailsClosedInsteadOfThrowing()
    {
        var settings = Rules(new RedactionRule
        {
            Id = "broken",
            Name = "Broken regex",
            Pattern = "([unclosed",
        });

        var preview = engine.PreviewText("anything", settings);

        // Fail closed: text is returned unchanged (no partial redaction) so
        // callers can detect the abort via an empty match list plus their
        // own validation; ApplyWorkspace surfaces the abort as an error.
        Assert.False(preview.Changed);
        Assert.Empty(preview.Matches);
    }

    [Fact]
    public void CatastrophicPatternAbortsUnderTimeout()
    {
        // Classic backtracking bomb; with a tiny timeout the engine must
        // abort rather than hang.
        var settings = Rules(new RedactionRule
        {
            Id = "bomb",
            Name = "Backtracking bomb",
            Pattern = "^(a+)+$",
        });
        var options = new RedactionOptions { PerRuleTimeout = TimeSpan.FromMilliseconds(150) };

        var preview = engine.PreviewText(
            new string('a', 40) + "b",
            settings,
            options);

        Assert.False(preview.Changed);
    }

    [Fact]
    public void NonPositiveTimeoutIsRejected()
    {
        var options = new RedactionOptions { PerRuleTimeout = TimeSpan.Zero };

        Assert.Throws<ArgumentException>(() =>
            engine.PreviewText("text", RedactionSettings.CreateDefault(), options));
    }
}
