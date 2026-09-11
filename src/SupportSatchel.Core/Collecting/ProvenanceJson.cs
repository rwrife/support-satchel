using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportSatchel.Core.Collecting;

/// <summary>
/// Deterministic JSON codec for provenance sidecars written next to staged
/// artifacts. Sidecar format is a flat document so downstream consumers
/// (redaction review UI, manifest builder) do not need polymorphic
/// deserialization.
/// </summary>
public static class ProvenanceJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Sidecar wire format; bump when the layout changes incompatibly.</summary>
    public const int SidecarSchema = 1;

    private sealed record Sidecar
    {
        public int Schema { get; init; } = SidecarSchema;

        public required string StagedPath { get; init; }

        public required string SourceModifiedUtc { get; init; }

        public required string Kind { get; init; }

        public required string SourceId { get; init; }

        public string? SourceRoot { get; init; }

        public string? RelativeSourcePath { get; init; }

        public string? ProbeId { get; init; }

        public string? ProbeVersion { get; init; }
    }

    /// <summary>Serializes provenance metadata for one staged artifact.</summary>
    public static string Serialize(string stagedPath, DateTimeOffset sourceModifiedUtc, Provenance provenance)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        ArgumentNullException.ThrowIfNull(provenance);

        var sidecar = provenance switch
        {
            FileProvenance file => new Sidecar
            {
                StagedPath = stagedPath,
                SourceModifiedUtc = sourceModifiedUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Kind = "file",
                SourceId = file.SourceId,
                SourceRoot = file.SourceRoot,
                RelativeSourcePath = file.RelativeSourcePath,
            },
            ProbeProvenance probe => new Sidecar
            {
                StagedPath = stagedPath,
                SourceModifiedUtc = sourceModifiedUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                Kind = "probe",
                SourceId = probe.SourceId,
                ProbeId = probe.ProbeId,
                ProbeVersion = probe.ProbeVersion,
            },
            _ => throw new ArgumentException($"Unknown provenance kind '{provenance.GetType().Name}'.", nameof(provenance)),
        };

        return JsonSerializer.Serialize(sidecar, Options);
    }
}
