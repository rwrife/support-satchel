using SupportSatchel.Core.Domain;

namespace SupportSatchel.Core.Tests;

/// <summary>Shared factories producing valid, deterministic domain objects.</summary>
internal static class TestProfiles
{
    internal static readonly DateTimeOffset BaseTime =
        new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    internal static BundleProfile Valid(
        Guid? id = null,
        string name = "Test Profile",
        IReadOnlyList<CaptureSource>? sources = null,
        RedactionSettings? redaction = null,
        ExportOptions? export = null,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? updatedAt = null) => new()
        {
            Id = id ?? Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Name = name,
            Description = "Fixture profile",
            CreatedAtUtc = createdAt ?? BaseTime,
            UpdatedAtUtc = updatedAt ?? BaseTime.AddMinutes(1),
            Sources = sources ??
            [
                new CaptureSource
                {
                    Id = "src-app-logs",
                    Kind = SourceKind.Folder,
                    Path = "/var/log/myapp",
                    IncludePatterns = ["*.log"],
                    ExcludePatterns = ["*.bak"],
                    Required = true,
                },
                new CaptureSource
                {
                    Id = "src-config",
                    Kind = SourceKind.File,
                    Path = "/etc/myapp/config.json",
                },
            ],
            Redaction = redaction ?? RedactionSettings.CreateDefault(),
            Export = export ?? new ExportOptions(),
        };

    internal static RunRecord ValidRun(Guid profileId, Guid? runId = null) => new()
    {
        Id = runId ?? Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
        ProfileId = profileId,
        ProfileName = "Test Profile",
        StartedAtUtc = BaseTime,
        Status = RunStatus.Running,
        WorkspacePath = "/tmp/satchel-runs/run-1",
    };
}
