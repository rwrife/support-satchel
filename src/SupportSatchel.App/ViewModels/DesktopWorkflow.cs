using SupportSatchel.Core.Domain;
using SupportSatchel.Core.Collecting;
using SupportSatchel.Core.Packaging;
using SupportSatchel.Core.Redacting;
using SupportSatchel.Core.Storage;

namespace SupportSatchel.App.ViewModels;

/// <summary>
/// Asynchronous desktop boundary over the local production profile store and
/// capture/redaction/export pipeline. Each operation owns its store connection,
/// making calls safe to dispatch away from the UI thread.
/// </summary>
public sealed class DesktopWorkflow : IDesktopWorkflow
{
    private readonly string _databasePath;
    private readonly string _workspaceRoot;
    private readonly Func<DateTimeOffset> _clock;

    /// <summary>Creates a local-only workflow using the supplied database and run directory.</summary>
    public DesktopWorkflow(
        string databasePath,
        string workspaceRoot,
        Func<DateTimeOffset>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _databasePath = Path.GetFullPath(databasePath);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>Lists saved profiles in store order without blocking the caller.</summary>
    public Task<IReadOnlyList<BundleProfile>> ListProfilesAsync() => RunAsync(store => store.ListProfiles());

    /// <summary>Validates and persists a new profile.</summary>
    public Task<BundleProfile> CreateProfileAsync(string name, string description, string configurationJson) =>
        RunAsync(store =>
        {
            var now = _clock();
            var profile = BuildProfile(Guid.NewGuid(), name, description, configurationJson, now, now);
            store.SaveProfile(profile);
            return profile;
        });

    /// <summary>Validates and persists edits while retaining profile identity and creation time.</summary>
    public Task<BundleProfile> UpdateProfileAsync(
        Guid id,
        string name,
        string description,
        string configurationJson) =>
        RunAsync(store =>
        {
            var current = store.GetProfile(id);
            var updated = BuildProfile(id, name, description, configurationJson, current.CreatedAtUtc, _clock());
            store.UpdateProfile(updated);
            return updated;
        });

    /// <summary>Copies a saved profile under a new identity and user-supplied unique name.</summary>
    public Task<BundleProfile> DuplicateProfileAsync(Guid id, string newName) =>
        RunAsync(store =>
        {
            var source = store.GetProfile(id);
            var now = _clock();
            var duplicate = source with
            {
                Id = Guid.NewGuid(),
                Name = NormalizeName(newName),
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
            };
            store.SaveProfile(duplicate);
            return duplicate;
        });

    /// <summary>
    /// Deletes only when the confirmation exactly matches the current persisted
    /// profile name, preventing stale selection and accidental deletion.
    /// </summary>
    public Task DeleteProfileAsync(Guid id, string confirmationName) =>
        RunAsync<object?>(store =>
        {
            var profile = store.GetProfile(id);
            if (!string.Equals(profile.Name, confirmationName, StringComparison.Ordinal))
            {
                throw new DesktopInputException("Deletion confirmation must exactly match the selected profile name.");
            }

            store.DeleteProfile(id);
            return null;
        });

    /// <summary>
    /// Captures the selected profile into a unique local workspace and creates
    /// a read-only redaction preview. It does not apply redactions or create an export.
    /// </summary>
    public Task<DesktopRun> CaptureAsync(Guid profileId) => RunAsync(store =>
    {
        var profile = store.GetProfile(profileId);
        var runId = Guid.NewGuid();
        var started = _clock().ToUniversalTime();
        var collectorRoot = Path.Combine(_workspaceRoot, "run-" + runId.ToString("N"));
        var workspace = Path.Combine(collectorRoot, "run-" + profile.Id.ToString("N"));
        var record = new RunRecord
        {
            Id = runId,
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            StartedAtUtc = started,
            Status = RunStatus.Running,
            WorkspacePath = workspace,
        };
        store.SaveRun(record);

        try
        {
            var collection = new Collector().Run(profile, collectorRoot);
            if (!collection.IsSuccessful)
            {
                store.UpdateRun(record with
                {
                    Status = RunStatus.Failed,
                    FinishedAtUtc = _clock().ToUniversalTime(),
                    Notes = $"Collection failed with {collection.Errors.Count} error(s).",
                });
                throw new DesktopInputException(
                    $"Capture failed for {collection.Errors.Count} required source(s). No review or export is available.");
            }

            var preview = new RedactionEngine().PreviewWorkspace(collection, profile.Redaction);
            if (preview.Errors.Count != 0)
            {
                store.UpdateRun(record with
                {
                    Status = RunStatus.Failed,
                    FinishedAtUtc = _clock().ToUniversalTime(),
                    Notes = $"Redaction preview failed with {preview.Errors.Count} error(s).",
                });
                throw new DesktopInputException(
                    $"Redaction preview failed for {preview.Errors.Count} artifact(s). Export remains blocked.");
            }

            store.UpdateRun(record with
            {
                Status = RunStatus.Completed,
                FinishedAtUtc = _clock().ToUniversalTime(),
                Notes = $"Ready for review: {preview.Artifacts.Count} artifact(s).",
            });
            return new DesktopRun(
                runId,
                profile,
                collection,
                preview.Artifacts.Select(static artifact => new ReviewArtifact(artifact)).ToArray());
        }
        catch (DesktopInputException)
        {
            throw;
        }
        catch
        {
            try
            {
                store.UpdateRun(record with
                {
                    Status = RunStatus.Failed,
                    FinishedAtUtc = _clock().ToUniversalTime(),
                    Notes = "Capture or redaction preview failed unexpectedly.",
                });
            }
            catch
            {
                // Preserve the sanitized operation-boundary failure.
            }

            throw new DesktopInputException("Capture failed unexpectedly. No source paths or file contents were disclosed.");
        }
    });

    /// <summary>
    /// Applies redactions to included staged copies and invokes the production
    /// exporter only after an explicit review acknowledgment.
    /// </summary>
    public async Task<DesktopExportResult> ExportAsync(
        DesktopRun run,
        string outputDirectory,
        bool reviewAcknowledged)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!reviewAcknowledged)
        {
            throw new DesktopInputException("Review acknowledgment is required before export.");
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new DesktopInputException("Choose an export directory.");
        }

        // This is deliberately evaluated before Task.Run. The acknowledgment
        // applies to this immutable set, not to UI objects that can change while
        // the background export is waiting or running.
        var includedPaths = run.Artifacts
            .Where(static artifact => artifact.IsIncluded)
            .Select(static artifact => artifact.StagedPath)
            .ToHashSet(StringComparer.Ordinal);
        if (includedPaths.Count == 0)
        {
            throw new DesktopInputException("Include at least one reviewed artifact before exporting.");
        }

        if (!run.TryBeginExport())
        {
            throw new DesktopInputException("This run is already exporting or has already been exported.");
        }

        // Once this token is consumed it is never reset: ApplyWorkspace mutates
        // staging, so even a later packaging failure cannot be safely retried.
        return await RunAsync(store =>
        {
            var currentProfile = store.GetProfile(run.ProfileId);
            if (!currentProfile.Equals(run.Profile))
            {
                throw new DesktopInputException("The profile changed after capture. Capture again before exporting.");
            }

            var collection = run.Collection with
            {
                Artifacts = run.Collection.Artifacts
                    .Where(artifact => includedPaths.Contains(artifact.StagedPath))
                    .ToArray(),
            };
            ValidateReviewedPaths(collection);
            var redaction = new RedactionEngine().ApplyWorkspace(collection, run.Profile.Redaction);
            if (!redaction.IsSuccessful)
            {
                throw new DesktopInputException("Redaction failed. Capture again before exporting.");
            }

            var exported = new BundleExporter().Export(
                run.Profile,
                collection,
                Path.GetFullPath(outputDirectory));
            return DesktopExportResult.From(exported);
        });
    }

    private Task<T> RunAsync<T>(Func<ProfileStore, T> operation) => Task.Run(() =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        Directory.CreateDirectory(_workspaceRoot);
        using var store = new ProfileStore(_databasePath);
        return operation(store);
    });

    private static void ValidateReviewedPaths(CollectionResult collection)
    {
        try
        {
            var workspace = Path.GetFullPath(collection.WorkspacePath);
            var stagingRoot = Path.GetFullPath(Path.Combine(workspace, Collector.StagingDirectory));
            var stagingPrefix = stagingRoot.EndsWith(Path.DirectorySeparatorChar)
                ? stagingRoot
                : stagingRoot + Path.DirectorySeparatorChar;
            RejectReparsePoints(workspace, workspace);
            RejectReparsePoints(workspace, stagingRoot);

            foreach (var artifact in collection.Artifacts)
            {
                var relative = artifact.StagedPath.Replace('/', Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(Path.Combine(stagingRoot, relative));
                if (!fullPath.StartsWith(stagingPrefix, StringComparison.Ordinal) || !File.Exists(fullPath))
                {
                    throw new DesktopInputException(
                        "Export refused because the reviewed artifact inventory changed after capture.");
                }

                RejectReparsePoints(workspace, fullPath);
            }
        }
        catch (DesktopInputException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new DesktopInputException(
                "Export refused because a reviewed workspace path could not be verified safely.");
        }
    }

    private static void RejectReparsePoints(string workspace, string path)
    {
        var relativePath = Path.GetRelativePath(workspace, path);
        if (relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new DesktopInputException("Export refused because a reviewed artifact path left the run workspace.");
        }

        var current = workspace;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new DesktopInputException("Export refused a symbolic link or reparse point in the reviewed workspace.");
        }

        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new DesktopInputException("Export refused a symbolic link or reparse point in the reviewed workspace.");
            }
        }
    }

    private static BundleProfile BuildProfile(
        Guid id,
        string name,
        string description,
        string configurationJson,
        DateTimeOffset created,
        DateTimeOffset updated)
    {
        var configuration = ProfileConfigurationJson.Parse(configurationJson);
        var profile = new BundleProfile
        {
            Id = id,
            Name = NormalizeName(name),
            Description = description?.Trim() ?? string.Empty,
            CreatedAtUtc = created.ToUniversalTime(),
            UpdatedAtUtc = updated.ToUniversalTime(),
            Sources = configuration.Sources,
            Redaction = configuration.Redaction,
            Export = configuration.Export,
        };

        var validation = ProfileValidator.Validate(profile);
        if (!validation.IsValid)
        {
            var first = validation.Issues[0];
            throw new DesktopInputException(
                $"Configuration is invalid at {first.Path}. Correct that field and try again.");
        }

        return profile;
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DesktopInputException("Profile name is required.");
        }

        return name.Trim();
    }
}
