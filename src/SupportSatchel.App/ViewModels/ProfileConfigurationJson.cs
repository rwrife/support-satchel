using System.Text.Json;
using System.Text.Json.Serialization;
using SupportSatchel.Core.Domain;

namespace SupportSatchel.App.ViewModels;

/// <summary>
/// Reads and writes the advanced profile configuration edited by the desktop UI.
/// Identity, display name, description, and timestamps remain controlled by the
/// basic editor fields and are intentionally absent from this document.
/// </summary>
public static class ProfileConfigurationJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Writes sources, redaction rules, and packaging options as editable JSON.</summary>
    public static string Serialize(
        IReadOnlyList<CaptureSource> sources,
        RedactionSettings redaction,
        ExportOptions export)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(redaction);
        ArgumentNullException.ThrowIfNull(export);
        return JsonSerializer.Serialize(new ConfigurationDocument(sources, redaction, export), Options);
    }

    /// <summary>Writes the configurable portion of a saved profile.</summary>
    public static string Serialize(BundleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return Serialize(profile.Sources, profile.Redaction, profile.Export);
    }

    internal static ProfileConfiguration Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new DesktopInputException("Advanced configuration JSON is required.");
        }

        try
        {
            var document = JsonSerializer.Deserialize<ConfigurationDocument>(json, Options)
                ?? throw new DesktopInputException("Advanced configuration JSON must contain an object.");
            if (document.Sources is null || document.Redaction is null || document.Export is null)
            {
                throw new DesktopInputException(
                    "Advanced configuration JSON requires sources, redaction, and export objects.");
            }

            return new ProfileConfiguration(document.Sources, document.Redaction, document.Export);
        }
        catch (JsonException)
        {
            throw new DesktopInputException("Advanced configuration JSON is invalid. Check its syntax and required fields.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record ConfigurationDocument(
        IReadOnlyList<CaptureSource> Sources,
        RedactionSettings Redaction,
        ExportOptions Export);
}

internal sealed record ProfileConfiguration(
    IReadOnlyList<CaptureSource> Sources,
    RedactionSettings Redaction,
    ExportOptions Export);
