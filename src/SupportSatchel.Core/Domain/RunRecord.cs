namespace SupportSatchel.Core.Domain;

/// <summary>Lifecycle state of a capture run.</summary>
public enum RunStatus
{
    /// <summary>Run started and still in progress.</summary>
    Running = 0,

    /// <summary>Run finished successfully.</summary>
    Completed = 1,

    /// <summary>Run stopped with an error.</summary>
    Failed = 2,
}

/// <summary>
/// Metadata for one execution of a profile. Stored in the local database
/// alongside the profile that produced it so the UI can show run history.
/// </summary>
public sealed record RunRecord
{
    /// <summary>Globally unique run identity.</summary>
    public required Guid Id { get; init; }

    /// <summary>Profile that produced this run.</summary>
    public required Guid ProfileId { get; init; }

    /// <summary>Profile name captured at run time (survives profile rename).</summary>
    public required string ProfileName { get; init; }

    /// <summary>Run start time (UTC).</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Run finish time; null while running.</summary>
    public DateTimeOffset? FinishedAtUtc { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public RunStatus Status { get; init; } = RunStatus.Running;

    /// <summary>Staging workspace directory for this run (issue #3).</summary>
    public required string WorkspacePath { get; init; }

    /// <summary>Free-form operator notes; empty when none.</summary>
    public string Notes { get; init; } = string.Empty;
}
