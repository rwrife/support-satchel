using System.Text;

namespace SupportSatchel.Core.Collecting;

/// <summary>
/// A built-in read-only diagnostic probe. Probes must never write to the
/// system, require privilege, or read secret material; they only report
/// baseline OS and runtime facts as plain text. Redaction (issue #4)
/// reviews probe output like any other staged artifact.
/// </summary>
public interface IDiagnosticProbe
{
    /// <summary>Stable identifier used in file names and provenance.</summary>
    string Id { get; }

    /// <summary>Probe implementation version; bump when output format changes.</summary>
    string Version { get; }

    /// <summary>Renders the probe output (deterministic line order, \n endings).</summary>
    /// <param name="now">Run time (UTC) supplied by the collector clock.</param>
    string Collect(DateTimeOffset now);
}

/// <summary>Reports OS name, version, and architecture.</summary>
public sealed class OperatingSystemProbe : IDiagnosticProbe
{
    /// <inheritdoc />
    public string Id => "os-info";

    /// <inheritdoc />
    public string Version => "1";

    /// <inheritdoc />
    public string Collect(DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("capturedUtc=").Append(now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")).Append('\n');
        sb.Append("osDescription=").Append(System.Runtime.InteropServices.RuntimeInformation.OSDescription).Append('\n');
        sb.Append("osArchitecture=").Append(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture).Append('\n');
        sb.Append("processArchitecture=").Append(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture).Append('\n');
        sb.Append("isWindows=").Append(OperatingSystem.IsWindows()).Append('\n');
        sb.Append("isMacOS=").Append(OperatingSystem.IsMacOS()).Append('\n');
        sb.Append("isLinux=").Append(OperatingSystem.IsLinux());
        return sb.ToString();
    }
}

/// <summary>Reports CLR version and base directory (read-only facts).</summary>
public sealed class RuntimeProbe : IDiagnosticProbe
{
    /// <inheritdoc />
    public string Id => "runtime-info";

    /// <inheritdoc />
    public string Version => "1";

    /// <inheritdoc />
    public string Collect(DateTimeOffset now)
    {
        var sb = new StringBuilder();
        sb.Append("capturedUtc=").Append(now.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")).Append('\n');
        sb.Append("framework=").Append(System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription).Append('\n');
        sb.Append("machineName=").Append(Environment.MachineName).Append('\n');
        sb.Append("processorCount=").Append(Environment.ProcessorCount).Append('\n');
        sb.Append("userName=").Append(Environment.UserName);
        return sb.ToString();
    }
}

/// <summary>Default built-in probe set used when a run does not customize it.</summary>
public static class BuiltInProbes
{
    /// <summary>Probes run for every collection. Order is deterministic.</summary>
    public static IReadOnlyList<IDiagnosticProbe> All { get; } =
        [new OperatingSystemProbe(), new RuntimeProbe()];
}
