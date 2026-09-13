using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportSatchel.Core.Packaging;

/// <summary>
/// Deterministic JSON codec for <see cref="BundleManifest"/> documents,
/// matching the house style of <c>ProfileJson</c> and
/// <c>RedactionReportJson</c>: camelCase, indented, no polymorphism, so
/// manifests are byte-stable for a given manifest object and verifiable by
/// recipients with a flat model.
/// </summary>
public static class BundleManifestJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    /// <summary>Serializes a bundle manifest to canonical JSON.</summary>
    public static string Serialize(BundleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return JsonSerializer.Serialize(manifest, Options);
    }

    /// <summary>Deserializes a manifest written by <see cref="Serialize"/>.</summary>
    public static BundleManifest? Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<BundleManifest>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
