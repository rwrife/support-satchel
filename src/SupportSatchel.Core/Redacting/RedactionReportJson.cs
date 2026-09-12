using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportSatchel.Core.Redacting;

/// <summary>
/// Deterministic JSON codec for <see cref="RedactionReport"/> documents,
/// matching the house style of <c>ProvenanceJson</c>: camelCase, indented,
/// no polymorphism, so downstream consumers (packaging, review UI) can read
/// reports with a flat model.
/// </summary>
public static class RedactionReportJson
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

    /// <summary>Serializes a redaction report to canonical JSON.</summary>
    public static string Serialize(RedactionReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, Options);
    }

    /// <summary>Deserializes a redaction report written by <see cref="Serialize"/>.</summary>
    public static RedactionReport? Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            return JsonSerializer.Deserialize<RedactionReport>(json, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
