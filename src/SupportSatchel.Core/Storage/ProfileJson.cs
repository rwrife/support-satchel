using System.Text.Json;
using System.Text.Json.Serialization;

namespace SupportSatchel.Core.Storage;

/// <summary>
/// Thrown when a stored profile document cannot be read by this version
/// of the application (unknown future schema, or malformed JSON).
/// </summary>
public sealed class ProfileSerializationException : InvalidOperationException
{
    /// <summary>Initializes the exception with a message.</summary>
    public ProfileSerializationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with a message and cause.</summary>
    public ProfileSerializationException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Canonical JSON codec for <see cref="Domain.BundleProfile"/> documents.
/// Property naming is camelCase and enum handling is numeric-stable via
/// explicit converters, so stored documents stay byte-stable across
/// platforms and round-trip losslessly.
/// </summary>
public static class ProfileJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Serializes a profile to canonical (indented, deterministic) JSON.</summary>
    public static string Serialize(Domain.BundleProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return JsonSerializer.Serialize(profile, Options);
    }

    /// <summary>
    /// Deserializes a profile document, rejecting schemas newer than
    /// <see cref="Domain.BundleProfile.CurrentDocumentSchema"/>.
    /// </summary>
    public static Domain.BundleProfile Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        Domain.BundleProfile profile;
        try
        {
            profile = JsonSerializer.Deserialize<Domain.BundleProfile>(json, Options)
                ?? throw new ProfileSerializationException("Profile document is empty JSON.");
        }
        catch (JsonException ex)
        {
            throw new ProfileSerializationException("Profile document is not valid JSON.", ex);
        }

        if (profile.DocumentSchema > Domain.BundleProfile.CurrentDocumentSchema)
        {
            throw new ProfileSerializationException(
                $"Profile document schema {profile.DocumentSchema} is newer than this build understands "
                + $"({Domain.BundleProfile.CurrentDocumentSchema}).");
        }

        return profile;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
