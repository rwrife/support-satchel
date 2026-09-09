using Xunit;

namespace SupportSatchel.Core.Tests;

/// <summary>
/// Baseline smoke test proving the solution skeleton builds, restores,
/// and executes tests. Domain-specific suites arrive with issues #2+.
/// </summary>
public class SkeletonTests
{
    [Fact]
    public void CoreExposesProductIdentity()
    {
        Assert.Equal("SupportSatchel.Core", CoreInfo.Product);
    }

    [Fact]
    public void TargetFrameworkIsNet8()
    {
        // Guards the repo-wide TargetFramework policy in Directory.Build.props.
        var framework = System.Runtime.InteropServices.RuntimeInformation
            .FrameworkDescription;
        Assert.Contains(".NET 8", framework, StringComparison.Ordinal);
    }
}
