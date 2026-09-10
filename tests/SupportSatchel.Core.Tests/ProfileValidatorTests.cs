using SupportSatchel.Core.Domain;
using Xunit;

namespace SupportSatchel.Core.Tests;

/// <summary>Acceptance: unit tests validate profile validation rules.</summary>
public class ProfileValidatorTests
{
    [Fact]
    public void ValidProfilePassesValidation()
    {
        var result = ProfileValidator.Validate(TestProfiles.Valid());
        Assert.True(result.IsValid, string.Join("; ", result.Issues.Select(i => $"{i.Path}: {i.Message}")));
    }

    [Fact]
    public void EmptyNameIsRejected()
    {
        var result = ProfileValidator.Validate(TestProfiles.Valid(name: "   "));
        Assert.Contains(result.Issues, i => i.Path == "name");
    }

    [Fact]
    public void EmptyGuidIsRejected()
    {
        var result = ProfileValidator.Validate(TestProfiles.Valid(id: Guid.Empty));
        Assert.Contains(result.Issues, i => i.Path == "id");
    }

    [Fact]
    public void NonUtcTimestampIsRejected()
    {
        var withOffset = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours(2));
        var result = ProfileValidator.Validate(TestProfiles.Valid(createdAt: withOffset));
        Assert.Contains(result.Issues, i => i.Path == "createdAtUtc");
    }

    [Fact]
    public void UpdatedBeforeCreatedIsRejected()
    {
        var result = ProfileValidator.Validate(TestProfiles.Valid(
            createdAt: TestProfiles.BaseTime,
            updatedAt: TestProfiles.BaseTime.AddMinutes(-5)));
        Assert.Contains(result.Issues, i => i.Path == "updatedAtUtc");
    }

    [Fact]
    public void ProfileWithoutSourcesIsRejected()
    {
        var result = ProfileValidator.Validate(TestProfiles.Valid(sources: []));
        Assert.Contains(result.Issues, i => i.Path == "sources");
    }

    [Fact]
    public void DuplicateSourceIdsAreRejected()
    {
        var sources = new[]
        {
            new CaptureSource { Id = "dup", Path = "/tmp/a" },
            new CaptureSource { Id = "dup", Path = "/tmp/b" },
        };
        var result = ProfileValidator.Validate(TestProfiles.Valid(sources: sources));
        Assert.Contains(result.Issues, i => i.Message.Contains("Duplicate source id", StringComparison.Ordinal));
    }

    [Fact]
    public void WildcardInSourcePathIsRejected()
    {
        var sources = new[]
        {
            new CaptureSource { Id = "glob", Path = "/tmp/*.log" },
        };
        var result = ProfileValidator.Validate(TestProfiles.Valid(sources: sources));
        Assert.Contains(result.Issues, i => i.Path == "sources[0].path");
    }

    [Fact]
    public void FolderPathWithoutExtensionMayNotCarryWildcard()
    {
        var sources = new[]
        {
            new CaptureSource { Id = "globdir", Path = "/tmp/logs*", Kind = SourceKind.Folder },
        };
        var result = ProfileValidator.Validate(TestProfiles.Valid(sources: sources));
        Assert.Contains(result.Issues, i => i.Path == "sources[0].path");
    }

    [Fact]
    public void PatternsOnFileSourceAreRejected()
    {
        var sources = new[]
        {
            new CaptureSource
            {
                Id = "file-glob",
                Path = "/tmp/app.log",
                IncludePatterns = ["*"],
            },
        };
        var result = ProfileValidator.Validate(TestProfiles.Valid(sources: sources));
        Assert.Contains(result.Issues, i => i.Path == "sources[0].includePatterns");
    }

    [Fact]
    public void InvalidRedactionRegexIsRejected()
    {
        var redaction = new RedactionSettings
        {
            Rules =
            [
                new RedactionRule { Id = "bad", Name = "Broken", Pattern = "([unclosed" },
            ],
        };
        var result = ProfileValidator.Validate(TestProfiles.Valid(redaction: redaction));
        Assert.Contains(result.Issues, i => i.Path == "redaction.rules[0].pattern");
    }

    [Fact]
    public void DuplicateRedactionRuleIdsAreRejected()
    {
        var redaction = new RedactionSettings
        {
            Rules =
            [
                new RedactionRule { Id = "same", Name = "A", Pattern = "a" },
                new RedactionRule { Id = "same", Name = "B", Pattern = "b" },
            ],
        };
        var result = ProfileValidator.Validate(TestProfiles.Valid(redaction: redaction));
        Assert.Contains(result.Issues, i => i.Message.Contains("Duplicate redaction rule id", StringComparison.Ordinal));
    }

    [Fact]
    public void BundleTemplateMissingTokensIsRejected()
    {
        var result = ProfileValidator.Validate(TestProfiles.Valid(
            export: new ExportOptions { BundleNameTemplate = "bundle.zip" }));
        Assert.Contains(result.Issues, i => i.Path == "export.bundleNameTemplate");
    }

    [Fact]
    public void BundleTemplateWithInvalidFileNameCharsIsRejected()
    {
        var result = ProfileValidator.Validate(TestProfiles.Valid(
            export: new ExportOptions { BundleNameTemplate = "{profile}/{utc}.zip" }));
        Assert.Contains(result.Issues, i => i.Path == "export.bundleNameTemplate");
    }

    [Fact]
    public void FutureDocumentSchemaIsRejected()
    {
        var result = ProfileValidator.Validate(TestProfiles.Valid() with
        {
            DocumentSchema = BundleProfile.CurrentDocumentSchema + 1,
        });
        Assert.Contains(result.Issues, i => i.Path == "documentSchema");
    }

    [Fact]
    public void AllIssuesAreReportedAtOnce()
    {
        var result = ProfileValidator.Validate(TestProfiles.Valid(name: string.Empty, id: Guid.Empty));
        Assert.True(result.Issues.Count >= 2);
    }
}
