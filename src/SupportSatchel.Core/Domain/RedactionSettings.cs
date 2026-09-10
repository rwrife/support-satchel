namespace SupportSatchel.Core.Domain;

/// <summary>
/// Redaction configuration attached to a <see cref="BundleProfile"/>.
/// Rules are applied to staged copies only; original sources are never
/// modified. Execution and previewing are implemented in issue #4.
/// </summary>
public sealed record RedactionSettings
{
    /// <summary>Ordered rules; earlier rules are applied first by the engine.</summary>
    public IReadOnlyList<RedactionRule> Rules { get; init; } = [];

    /// <summary>
    /// Creates settings pre-populated with conservative built-in rules that
    /// cover common secrets and PII shapes (tokens, API keys, e-mail
    /// addresses, host/user hints). These are a safe starting point, not a
    /// guarantee: documented limits are covered by tests in issue #4.
    /// </summary>
    public static RedactionSettings CreateDefault() => new()
    {
        Rules =
        [
            new RedactionRule
            {
                Id = "builtin.email",
                Name = "Email addresses",
                Pattern = @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}",
            },
            new RedactionRule
            {
                Id = "builtin.bearer-token",
                Name = "Bearer tokens",
                Pattern = @"(?i)\b(bearer\s+[A-Za-z0-9._\-]{16,})",
            },
            new RedactionRule
            {
                Id = "builtin.api-key",
                Name = "API keys and secrets in key=value form",
                Pattern = @"(?i)\b(api[_-]?key|secret|token|password)\b\s*[:=]\s*""?[A-Za-z0-9+/=_\-\.]{12,}""?",
            },
            new RedactionRule
            {
                Id = "builtin.host-user",
                Name = "Host and user name hints",
                Pattern = @"(?i)\b(hostname|user[-_]?name|computer[-_]?name)\s*[:=]\s*[^\s""']+",
            },
        ],
    };

    /// <inheritdoc />
    public bool Equals(RedactionSettings? other)
    {
        if (other is null)
        {
            return false;
        }

        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return PatternList.EqualRules(Rules, other.Rules);
    }

    /// <inheritdoc />
    public override int GetHashCode() => PatternList.RulesHash(Rules);
}
