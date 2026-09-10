using SupportSatchel.Core.Domain;
using SupportSatchel.Core.Storage;
using Xunit;

namespace SupportSatchel.Core.Tests;

/// <summary>Acceptance: serialization/deserialization round-trips losslessly.</summary>
public class ProfileJsonTests
{
    [Fact]
    public void SerializeDeserializeRoundTripsAllFields()
    {
        var profile = TestProfiles.Valid();
        var restored = ProfileJson.Deserialize(ProfileJson.Serialize(profile));
        Assert.Equal(profile, restored);
    }

    [Fact]
    public void SerializationIsDeterministic()
    {
        var profile = TestProfiles.Valid();
        Assert.Equal(ProfileJson.Serialize(profile), ProfileJson.Serialize(profile));
    }

    [Fact]
    public void CustomRedactionAndSourcesRoundTrip()
    {
        var profile = TestProfiles.Valid(
            sources:
            [
                new CaptureSource
                {
                    Id = "s1",
                    Kind = SourceKind.Folder,
                    Path = "/tmp/data",
                    IncludePatterns = ["**/*.json", "*.txt"],
                    ExcludePatterns = ["*.tmp"],
                    Required = false,
                },
            ],
            redaction: new RedactionSettings
            {
                Rules =
                [
                    new RedactionRule
                    {
                        Id = "r1",
                        Name = "Custom",
                        Pattern = @"secret\d+",
                        Replacement = "<x>",
                        Enabled = false,
                    },
                ],
            },
            export: new ExportOptions
            {
                BundleNameTemplate = "{profile}-{version}-{utc}.zip",
                Deterministic = false,
                IncludeManifest = false,
            });

        var restored = ProfileJson.Deserialize(ProfileJson.Serialize(profile));
        Assert.Equal(profile, restored);
        Assert.Equal(SourceKind.Folder, Assert.Single(restored.Sources).Kind);
        Assert.False(Assert.Single(restored.Redaction.Rules).Enabled);
        Assert.False(restored.Export.Deterministic);
    }

    [Fact]
    public void NewerDocumentSchemaIsRejected()
    {
        var json = ProfileJson.Serialize(TestProfiles.Valid() with
        {
            DocumentSchema = BundleProfile.CurrentDocumentSchema + 1,
        });

        var ex = Assert.Throws<ProfileSerializationException>(() => ProfileJson.Deserialize(json));
        Assert.Contains("newer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedJsonThrowsTypedException()
    {
        Assert.Throws<ProfileSerializationException>(
            () => ProfileJson.Deserialize("{ not json"));
    }

    [Fact]
    public void SerializedDocumentUsesCamelCase()
    {
        var json = ProfileJson.Serialize(TestProfiles.Valid());
        Assert.Contains("\"documentSchema\"", json, StringComparison.Ordinal);
        Assert.Contains("\"createdAtUtc\"", json, StringComparison.Ordinal);
    }
}
