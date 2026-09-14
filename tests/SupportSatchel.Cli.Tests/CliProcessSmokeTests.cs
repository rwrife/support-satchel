using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using SupportSatchel.Cli;
using SupportSatchel.Core.Domain;
using SupportSatchel.Core.Storage;
using Xunit;

namespace SupportSatchel.Cli.Tests;

public sealed class CliProcessSmokeTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), "support-satchel-process-smoke", Guid.NewGuid().ToString("N"));

    public CliProcessSmokeTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of temporary fixtures.
        }
    }

    [Fact]
    [Trait("Category", "ProcessSmoke")]
    public async Task ChildProcessRunsSanitizedProfileAndExportsReviewedBundle()
    {
        var source = Path.Combine(root, "sanitized-sample.log");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sanitized-sample.log"), source);
        var database = Path.Combine(root, "profiles.db");
        var profile = new BundleProfile
        {
            Id = Guid.NewGuid(),
            Name = "CI sanitized sample",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Sources =
            [
                new CaptureSource
                {
                    Id = "sanitized-fixture",
                    Kind = SourceKind.File,
                    Path = source,
                    Required = true,
                },
            ],
        };
        using (var store = new ProfileStore(database))
        {
            store.SaveProfile(profile);
        }

        var listed = await Invoke("--json", "profiles", "list", "--store", database);
        Assert.Equal(0, listed.ExitCode);
        Assert.Contains("CI sanitized sample", listed.Stdout, StringComparison.Ordinal);

        var run = await Invoke(
            "--json", "run", "--profile", profile.Id.ToString("D"), "--store", database,
            "--workspace-root", Path.Combine(root, "runs"));
        Assert.Equal(0, run.ExitCode);
        using var runDocument = JsonDocument.Parse(run.Stdout);
        Assert.Equal("readyForReview", runDocument.RootElement.GetProperty("status").GetString());
        var runId = runDocument.RootElement.GetProperty("runId").GetGuid();

        var exported = await Invoke(
            "--json", "export", "--run", runId.ToString("D"), "--store", database,
            "--output", Path.Combine(root, "export"), "--reviewed");
        Assert.Equal(0, exported.ExitCode);
        using var exportDocument = JsonDocument.Parse(exported.Stdout);
        var bundle = exportDocument.RootElement.GetProperty("bundlePath").GetString()!;
        using var archive = ZipFile.OpenRead(bundle);
        var fixtureEntry = Assert.Single(archive.Entries,
            entry => entry.FullName == "staging/sanitized-fixture/sanitized-sample.log");
        using var reader = new StreamReader(fixtureEntry.Open());
        var fixtureText = reader.ReadToEnd();
        Assert.DoesNotContain("fixture.user@example.test", fixtureText, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", fixtureText, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", fixtureText, StringComparison.Ordinal);
        Assert.DoesNotContain(archive.Entries,
            entry => entry.FullName.StartsWith("provenance", StringComparison.Ordinal));
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> Invoke(params string[] arguments)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var cliAssembly = typeof(CliApplication).Assembly.Location;
        var start = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(cliAssembly);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between HasExited and Kill.
                }
            }
            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutTask, stderrTask);
            throw new Xunit.Sdk.XunitException("CLI child process timed out.");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
