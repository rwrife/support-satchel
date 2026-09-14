using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SupportSatchel.Core;
using SupportSatchel.Core.Collecting;
using SupportSatchel.Core.Domain;
using SupportSatchel.Core.Packaging;
using SupportSatchel.Core.Redacting;
using SupportSatchel.Core.Storage;

namespace SupportSatchel.Cli;

/// <summary>Command-line entry point for the local capture, review, and export workflow.</summary>
public static class CliApplication
{
    private const int ExitOk = 0;
    private const int ExitUsage = 2;
    private const int ExitNotFound = 3;
    private const int ExitPipelineFailed = 4;
    private const int ExitUnexpected = 5;

    private static readonly string ProductVersion = typeof(CliApplication).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0]
        ?? typeof(CliApplication).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Runs one CLI invocation using injectable streams for tests.</summary>
    public static int Run(IReadOnlyList<string> arguments, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        var args = arguments.ToList();
        var json = args.RemoveAll(static argument => argument == "--json") > 0;

        if (args.Count == 0 || args[0] is "--help" or "-h" or "help")
        {
            if (args.Count > 1)
            {
                return WriteError(json, stdout, stderr, ExitUsage, "invalid_arguments",
                    "Invalid arguments for help.");
            }

            if (json)
            {
                WriteJson(stdout, new { command = "help", usage = UsageLines });
            }
            else
            {
                WriteUsage(stdout);
            }
            return ExitOk;
        }

        if (args[0] is "--version" or "-v")
        {
            if (args.Count > 1)
            {
                return WriteError(json, stdout, stderr, ExitUsage, "invalid_arguments",
                    "Invalid arguments for version.");
            }

            if (json)
            {
                WriteJson(stdout, new { product = CoreInfo.Product, version = ProductVersion });
            }
            else
            {
                stdout.WriteLine($"Support Satchel CLI ({CoreInfo.Product}) {ProductVersion}");
            }
            return ExitOk;
        }

        try
        {
            return args[0] switch
            {
                "profiles" when args.Count > 1 && args[1] == "list" => ListProfiles(args[2..], json, stdout),
                "run" => RunProfile(args[1..], json, stdout, stderr),
                "export" => ExportRun(args[1..], json, stdout, stderr),
                _ => WriteError(json, stdout, stderr, ExitUsage, "invalid_arguments",
                    "Unknown command or invalid command shape. Run with --help for usage."),
            };
        }
        catch (ProfileNotFoundException)
        {
            return WriteError(json, stdout, stderr, ExitNotFound, "profile_not_found",
                "The requested profile was not found in the local store.");
        }
        catch (RunNotFoundException)
        {
            return WriteError(json, stdout, stderr, ExitNotFound, "run_not_found",
                "The requested run was not found in the local store.");
        }
        catch (CliUsageException ex)
        {
            return WriteError(json, stdout, stderr, ExitUsage, "invalid_arguments", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or StoreException
                                   or BundleExportException or InvalidOperationException)
        {
            // Do not echo exception messages: storage and IO failures can contain source paths or secrets.
            return WriteError(json, stdout, stderr, ExitUnexpected, "operation_failed",
                "The operation failed. No bundle was exported.");
        }
        catch (Exception)
        {
            // The process boundary is intentionally sanitized even for an unexpected provider failure.
            return WriteError(json, stdout, stderr, ExitUnexpected, "operation_failed",
                "The operation failed. No bundle was exported.");
        }
    }

    private static int ListProfiles(IReadOnlyList<string> args, bool json, TextWriter stdout)
    {
        var options = ParseOptions(args, "--store");
        using var store = new ProfileStore(GetStorePath(options));
        var profiles = store.ListProfiles()
            .Select(profile => new
            {
                profile.Id,
                profile.Name,
                profile.Description,
                sourceCount = profile.Sources.Count,
                profile.UpdatedAtUtc,
            })
            .ToArray();

        if (json)
        {
            WriteJson(stdout, new { profiles });
        }
        else if (profiles.Length == 0)
        {
            stdout.WriteLine("No profiles found.");
        }
        else
        {
            foreach (var profile in profiles)
            {
                stdout.WriteLine($"{profile.Id:D}\t{profile.Name}\t{profile.sourceCount} source(s)");
            }
        }

        return ExitOk;
    }

    private static int RunProfile(
        IReadOnlyList<string> args,
        bool json,
        TextWriter stdout,
        TextWriter stderr)
    {
        var options = ParseOptions(args, "--profile", "--store", "--workspace-root");
        var profileSelector = Require(options, "--profile");
        var workspaceRoot = options.GetValueOrDefault("--workspace-root") ?? DefaultDataPath("runs");

        using var store = new ProfileStore(GetStorePath(options));
        var profile = Guid.TryParse(profileSelector, out var profileId)
            ? store.GetProfile(profileId)
            : store.GetProfileByName(profileSelector);

        var runId = Guid.NewGuid();
        var started = DateTimeOffset.UtcNow;
        var collectorRoot = Path.Combine(Path.GetFullPath(workspaceRoot), "run-" + runId.ToString("N"));
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
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    Notes = $"Collection failed with {collection.Errors.Count} error(s).",
                });
                return WriteError(json, stdout, stderr, ExitPipelineFailed, "collection_failed",
                    $"Capture failed for {collection.Errors.Count} required source(s); redaction and export were not attempted.");
            }

            var redaction = new RedactionEngine().ApplyWorkspace(collection, profile.Redaction);
            if (!redaction.IsSuccessful)
            {
                store.UpdateRun(record with
                {
                    Status = RunStatus.Failed,
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    Notes = $"Redaction failed with {redaction.Errors.Count} error(s).",
                });
                return WriteError(json, stdout, stderr, ExitPipelineFailed, "redaction_failed",
                    $"Redaction failed for {redaction.Errors.Count} artifact(s); export is blocked.");
            }

            store.UpdateRun(record with
            {
                Status = RunStatus.Completed,
                FinishedAtUtc = DateTimeOffset.UtcNow,
                Notes = $"Ready for review: {redaction.Artifacts.Count} artifact(s).",
            });

            if (json)
            {
                WriteJson(stdout, new
                {
                    runId,
                    profileId = profile.Id,
                    status = "readyForReview",
                    reviewRequired = true,
                    workspacePath = collection.WorkspacePath,
                    reportPath = redaction.ReportPath,
                    artifactCount = redaction.Artifacts.Count,
                    redactedArtifactCount = redaction.Artifacts.Count(artifact => artifact.Redacted),
                    skippedSourceCount = collection.Skipped.Count,
                });
            }
            else
            {
                stdout.WriteLine($"Run {runId:D} is ready for redaction review.");
                stdout.WriteLine($"Workspace: {collection.WorkspacePath}");
                stdout.WriteLine("Review the staged artifacts and redaction-report.json, then invoke export with this run id.");
            }

            return ExitOk;
        }
        catch
        {
            try
            {
                store.UpdateRun(record with
                {
                    Status = RunStatus.Failed,
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    Notes = "Capture or redaction failed unexpectedly.",
                });
            }
            catch
            {
                // Preserve the original sanitized process-boundary failure.
            }

            throw;
        }
    }

    private static int ExportRun(
        IReadOnlyList<string> args,
        bool json,
        TextWriter stdout,
        TextWriter stderr)
    {
        var reviewed = args.Count(argument => argument == "--reviewed");
        if (reviewed != 1)
        {
            throw new CliUsageException("Export requires exactly one --reviewed acknowledgment.");
        }

        var options = ParseOptions(args.Where(argument => argument != "--reviewed").ToArray(), "--run", "--store", "--output");
        if (!Guid.TryParse(Require(options, "--run"), out var runId))
        {
            throw new CliUsageException("--run must be a GUID.");
        }

        var output = Require(options, "--output");
        using var store = new ProfileStore(GetStorePath(options));
        var run = store.GetRun(runId);
        if (run.Status != RunStatus.Completed)
        {
            return WriteError(json, stdout, stderr, ExitPipelineFailed, "run_not_exportable",
                "Only a completed capture and redaction run can be exported.");
        }

        var profile = store.GetProfile(run.ProfileId);
        var collection = LoadReviewedCollection(run.WorkspacePath);
        var exported = new BundleExporter().Export(profile, collection, Path.GetFullPath(output));

        if (json)
        {
            WriteJson(stdout, new
            {
                runId,
                status = "exported",
                exported.BundlePath,
                exported.ManifestPath,
                exported.BundleSha256,
                artifactCount = exported.Manifest.Artifacts.Count,
            });
        }
        else
        {
            stdout.WriteLine($"Exported run {runId:D}.");
            stdout.WriteLine($"Bundle: {exported.BundlePath}");
            stdout.WriteLine($"SHA-256: {exported.BundleSha256}");
        }

        return ExitOk;
    }

    private static CollectionResult LoadReviewedCollection(string workspace)
    {
        RejectReparsePoints(workspace, workspace);
        var reportPath = Path.Combine(workspace, RedactionEngine.ReportFileName);
        RejectReparsePoints(workspace, reportPath);
        if (!File.Exists(reportPath))
        {
            throw new BundleExportException("Redaction report is missing.");
        }

        var report = RedactionReportJson.Deserialize(File.ReadAllText(reportPath));
        if (report is null || !report.IsSuccessful)
        {
            throw new BundleExportException("Redaction report is invalid or unsuccessful.");
        }

        var stagingRoot = Path.GetFullPath(Path.Combine(workspace, Collector.StagingDirectory));
        RejectReparsePoints(workspace, stagingRoot);
        var stagingPrefix = stagingRoot.EndsWith(Path.DirectorySeparatorChar)
            ? stagingRoot
            : stagingRoot + Path.DirectorySeparatorChar;
        var artifacts = report.Artifacts.Select(artifact =>
        {
            var relative = artifact.StagedPath.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(stagingRoot, relative));
            if (!fullPath.StartsWith(stagingPrefix, StringComparison.Ordinal) || !File.Exists(fullPath))
            {
                throw new BundleExportException("Redaction report contains an invalid artifact path.");
            }
            RejectReparsePoints(workspace, fullPath);

            return new StagedArtifact
            {
                StagedPath = artifact.StagedPath,
                SizeBytes = new FileInfo(fullPath).Length,
                SourceModifiedUtc = File.GetLastWriteTimeUtc(fullPath),
                Provenance = new FileProvenance("redacted-workspace", string.Empty, artifact.StagedPath),
            };
        }).ToArray();

        return new CollectionResult
        {
            WorkspacePath = workspace,
            Artifacts = artifacts,
            Skipped = [],
            Errors = [],
        };
    }

    private static void RejectReparsePoints(string workspace, string path)
    {
        var workspacePath = Path.GetFullPath(workspace);
        var fullPath = Path.GetFullPath(path);
        var relativePath = Path.GetRelativePath(workspacePath, fullPath);
        if (relativePath == ".." || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new BundleExportException("Export path falls outside the reviewed workspace.");
        }

        var current = workspacePath;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new BundleExportException("Export refuses symbolic links or reparse points in the reviewed workspace.");
        }

        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new BundleExportException("Export refuses symbolic links or reparse points in the reviewed workspace.");
            }
        }
    }

    private static Dictionary<string, string?> ParseOptions(
        IReadOnlyList<string> args,
        params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index += 2)
        {
            var option = args[index];
            if (!allowedSet.Contains(option) || index + 1 >= args.Count || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new CliUsageException("Invalid or incomplete command option. Run with --help for usage.");
            }

            if (string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new CliUsageException("Option values cannot be empty or whitespace.");
            }

            if (!result.TryAdd(option, args[index + 1]))
            {
                throw new CliUsageException($"Option {option} may only be supplied once.");
            }
        }

        return result;
    }

    private static string Require(IReadOnlyDictionary<string, string?> options, string name) =>
        options.GetValueOrDefault(name) ?? throw new CliUsageException($"Missing required option {name}.");

    private static string GetStorePath(IReadOnlyDictionary<string, string?> options)
    {
        var path = Path.GetFullPath(options.GetValueOrDefault("--store") ?? DefaultDataPath("profiles.db"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static string DefaultDataPath(string leaf) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SupportSatchel",
        leaf);

    private static int WriteError(
        bool json,
        TextWriter stdout,
        TextWriter stderr,
        int exitCode,
        string code,
        string message)
    {
        if (json)
        {
            WriteJson(stdout, new { error = code, message });
        }
        else
        {
            stderr.WriteLine($"Error: {message}");
        }

        return exitCode;
    }

    private static void WriteJson(TextWriter writer, object value) =>
        writer.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Support Satchel CLI");
        foreach (var line in UsageLines)
        {
            writer.WriteLine("  " + line);
        }
        writer.WriteLine();
        writer.WriteLine("Run captures and redacts local copies. Export remains a separate action after review.");
    }

    private static readonly string[] UsageLines =
    [
        "support-satchel [--json] profiles list [--store PATH]",
        "support-satchel [--json] run --profile ID_OR_NAME [--store PATH] [--workspace-root PATH]",
        "support-satchel [--json] export --run ID [--store PATH] --output DIRECTORY --reviewed",
    ];

    private sealed class CliUsageException(string message) : Exception(message);
}
