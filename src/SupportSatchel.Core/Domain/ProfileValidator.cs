using System.Text.RegularExpressions;

namespace SupportSatchel.Core.Domain;

/// <summary>One validation problem found in a <see cref="BundleProfile"/>.</summary>
/// <param name="Path">Machine-readable location, e.g. <c>sources[0].path</c>.</param>
/// <param name="Message">Human-readable problem description.</param>
public readonly record struct ValidationIssue(string Path, string Message);

/// <summary>Outcome of <see cref="ProfileValidator.Validate"/>.</summary>
public sealed record ValidationResult
{
    /// <summary>True when no issues were found.</summary>
    public bool IsValid => Issues.Count == 0;

    /// <summary>All problems found, in deterministic order.</summary>
    public required IReadOnlyList<ValidationIssue> Issues { get; init; }
}

/// <summary>
/// Rules that make a profile safe to persist and run. Validation is pure
/// and never touches the file system: existence of source paths is a
/// collector concern (issue #3), not a profile-definition concern.
/// </summary>
public static class ProfileValidator
{
    /// <summary>Maximum accepted profile document schema version.</summary>
    public const int MaxSupportedDocumentSchema = BundleProfile.CurrentDocumentSchema;

    /// <summary>Validates a profile and returns all issues at once.</summary>
    public static ValidationResult Validate(BundleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var issues = new List<ValidationIssue>();

        if (profile.DocumentSchema < 1)
        {
            issues.Add(new ValidationIssue("documentSchema", "Document schema must be >= 1."));
        }
        else if (profile.DocumentSchema > MaxSupportedDocumentSchema)
        {
            issues.Add(new ValidationIssue(
                "documentSchema",
                $"Document schema {profile.DocumentSchema} is newer than the supported version {MaxSupportedDocumentSchema}."));
        }

        if (profile.Id == Guid.Empty)
        {
            issues.Add(new ValidationIssue("id", "Profile id must be a non-empty GUID."));
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            issues.Add(new ValidationIssue("name", "Profile name is required."));
        }
        else if (profile.Name.Trim() != profile.Name || profile.Name.Length > 200)
        {
            issues.Add(new ValidationIssue("name", "Profile name must be trimmed and at most 200 characters."));
        }

        if (profile.CreatedAtUtc.Offset != TimeSpan.Zero)
        {
            issues.Add(new ValidationIssue("createdAtUtc", "Created time must use UTC offset."));
        }

        if (profile.UpdatedAtUtc.Offset != TimeSpan.Zero)
        {
            issues.Add(new ValidationIssue("updatedAtUtc", "Updated time must use UTC offset."));
        }

        if (profile.CreatedAtUtc <= DateTimeOffset.MinValue || profile.UpdatedAtUtc <= DateTimeOffset.MinValue)
        {
            issues.Add(new ValidationIssue("timestamps", "Timestamps must be greater than the minimum date."));
        }

        if (profile.UpdatedAtUtc < profile.CreatedAtUtc)
        {
            issues.Add(new ValidationIssue("updatedAtUtc", "Updated time cannot precede created time."));
        }

        var seenSourceIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < profile.Sources.Count; i++)
        {
            var source = profile.Sources[i];
            var prefix = $"sources[{i}]";

            if (string.IsNullOrWhiteSpace(source.Id))
            {
                issues.Add(new ValidationIssue($"{prefix}.id", "Source id is required."));
            }
            else if (!seenSourceIds.Add(source.Id))
            {
                issues.Add(new ValidationIssue($"{prefix}.id", $"Duplicate source id '{source.Id}'."));
            }

            if (string.IsNullOrWhiteSpace(source.Path))
            {
                issues.Add(new ValidationIssue($"{prefix}.path", "Source path is required."));
            }
            else if (source.Path.Contains('*', StringComparison.Ordinal)
                || source.Path.Contains('?', StringComparison.Ordinal))
            {
                issues.Add(new ValidationIssue(
                    $"{prefix}.path",
                    "Source path must be a concrete file or folder; put wildcards in include patterns."));
            }

            if (source.Kind == SourceKind.File
                && (source.IncludePatterns.Count > 0 || source.ExcludePatterns.Count > 0))
            {
                issues.Add(new ValidationIssue(
                    $"{prefix}.includePatterns",
                    "File sources cannot use include/exclude patterns; use a folder source."));
            }

            CheckPatterns(source.IncludePatterns, $"{prefix}.includePatterns", issues);
            CheckPatterns(source.ExcludePatterns, $"{prefix}.excludePatterns", issues);
        }

        if (profile.Sources.Count == 0)
        {
            issues.Add(new ValidationIssue("sources", "A profile needs at least one capture source."));
        }

        var seenRuleIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < profile.Redaction.Rules.Count; i++)
        {
            var rule = profile.Redaction.Rules[i];
            var prefix = $"redaction.rules[{i}]";

            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                issues.Add(new ValidationIssue($"{prefix}.id", "Redaction rule id is required."));
            }
            else if (!seenRuleIds.Add(rule.Id))
            {
                issues.Add(new ValidationIssue($"{prefix}.id", $"Duplicate redaction rule id '{rule.Id}'."));
            }

            if (string.IsNullOrWhiteSpace(rule.Name))
            {
                issues.Add(new ValidationIssue($"{prefix}.name", "Redaction rule name is required."));
            }

            if (string.IsNullOrEmpty(rule.Pattern))
            {
                issues.Add(new ValidationIssue($"{prefix}.pattern", "Redaction pattern is required."));
            }
            else if (!IsValidRegex(rule.Pattern))
            {
                issues.Add(new ValidationIssue($"{prefix}.pattern", "Redaction pattern is not a valid regular expression."));
            }
        }

        if (string.IsNullOrWhiteSpace(profile.Export.BundleNameTemplate))
        {
            issues.Add(new ValidationIssue("export.bundleNameTemplate", "Bundle name template is required."));
        }
        else
        {
            var template = profile.Export.BundleNameTemplate;
            if (!template.Contains("{profile}", StringComparison.Ordinal)
                || !template.Contains("{utc}", StringComparison.Ordinal))
            {
                issues.Add(new ValidationIssue(
                    "export.bundleNameTemplate",
                    "Bundle name template must contain {profile} and {utc} tokens."));
            }

            var withoutTokens = template
                .Replace("{profile}", "p", StringComparison.Ordinal)
                .Replace("{version}", "v", StringComparison.Ordinal)
                .Replace("{utc}", "u", StringComparison.Ordinal);
            if (withoutTokens.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                issues.Add(new ValidationIssue(
                    "export.bundleNameTemplate",
                    "Bundle name template contains characters invalid in file names."));
            }
        }

        return new ValidationResult { Issues = issues };
    }

    private static void CheckPatterns(
        IReadOnlyList<string> patterns,
        string prefix,
        List<ValidationIssue> issues)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < patterns.Count; i++)
        {
            var pattern = patterns[i];
            var path = $"{prefix}[{i}]";

            if (string.IsNullOrWhiteSpace(pattern))
            {
                issues.Add(new ValidationIssue(path, "Glob pattern cannot be empty."));
                continue;
            }

            if (!seen.Add(pattern))
            {
                issues.Add(new ValidationIssue(path, $"Duplicate glob pattern '{pattern}'."));
            }
        }
    }

    private static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = Regex.Match(string.Empty, pattern);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
