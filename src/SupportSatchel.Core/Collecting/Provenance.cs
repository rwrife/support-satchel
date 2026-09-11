namespace SupportSatchel.Core.Collecting;

/// <summary>
/// Base type for per-artifact provenance recorded when the collector stages
/// a file. Provenance answers "where did this staged copy come from?".
/// </summary>
public abstract record Provenance;

/// <summary>Provenance for a copy of an existing file on disk.</summary>
/// <param name="SourceId">Profile source that produced the artifact.</param>
/// <param name="SourceRoot">Absolute path of the collected file.</param>
/// <param name="RelativeSourcePath">
/// Path relative to the folder source root; equal to the file name for
/// single-file sources.
/// </param>
public sealed record FileProvenance(
    string SourceId,
    string SourceRoot,
    string RelativeSourcePath) : Provenance;

/// <summary>Provenance for the output of a built-in read-only probe.</summary>
/// <param name="SourceId">Logical source bucket (built-in probes).</param>
/// <param name="ProbeId">Stable probe identifier.</param>
/// <param name="ProbeVersion">Probe implementation version string.</param>
public sealed record ProbeProvenance(
    string SourceId,
    string ProbeId,
    string ProbeVersion) : Provenance;
