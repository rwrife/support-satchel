using SupportSatchel.Core.Collecting;
using Xunit;

namespace SupportSatchel.Core.Tests;

/// <summary>Unit tests for the gitignore-flavoured glob matcher.</summary>
public class GlobMatcherTests
{
    [Theory]
    [InlineData("app.log", "*.log", true)]
    [InlineData("sub/dir/app.log", "*.log", true)]
    [InlineData("app.txt", "*.log", false)]
    [InlineData("logs/app.log", "logs/*.log", true)]
    [InlineData("other/app.log", "logs/*.log", false)]
    [InlineData("logs/sub/app.log", "logs/*.log", false)]
    [InlineData("logs/sub/app.log", "logs/**/*.log", true)]
    [InlineData("a/b/c.txt", "**/c.txt", true)]
    [InlineData("c.txt", "**/c.txt", true)]
    [InlineData("config.json", "config.js?n", true)]
    [InlineData("config.json", "config.json", true)]
    [InlineData("sub/config.json", "config.json", true)]
    public void IsMatchBehavesAsDocumented(string path, string pattern, bool expected)
    {
        Assert.Equal(expected, GlobMatcher.IsMatch(path, pattern));
    }

    [Fact]
    public void IsSelectedRequiresIncludeWhenPresentAndExcludesAlwaysWin()
    {
        Assert.True(GlobMatcher.IsSelected("a/app.log", [], []));
        Assert.True(GlobMatcher.IsSelected("a/app.log", ["*.log"], []));
        Assert.False(GlobMatcher.IsSelected("a/app.log", ["*.log"], ["*.log"]));
        Assert.False(GlobMatcher.IsSelected("a/app.log", ["*.txt"], []));

        // Exclude wins even when include matches the same file.
        Assert.False(GlobMatcher.IsSelected("a/app.log", ["app.log"], ["app.log"]));
    }

    [Fact]
    public void BackslashesInPathsAreNormalized()
    {
        Assert.True(GlobMatcher.IsMatch(@"logs\app.log", "logs/*.log"));
    }
}
