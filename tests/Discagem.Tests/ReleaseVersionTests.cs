using Discagem.Shared;

namespace Discagem.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("0.2.0", 0, 2, 0, "")]
    [InlineData("v1.10.3", 1, 10, 3, "")]
    [InlineData("0.3.0-beta.1", 0, 3, 0, "beta.1")]
    [InlineData("0.2.0+4f1c2d3", 0, 2, 0, "")]
    [InlineData("0.0.0-ci.42+abc", 0, 0, 0, "ci.42")]
    public void Parses(string text, int major, int minor, int patch, string preRelease)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch, preRelease), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("-1.2.3")]
    public void RejectsInvalidVersions(string? text) => Assert.False(ReleaseVersion.TryParse(text, out _));

    [Theory]
    [InlineData("0.2.0", "0.3.0")]
    [InlineData("0.9.0", "0.10.0")]
    [InlineData("1.0.0-beta.2", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-rc.1", "1.0.1-alpha")]
    public void OrdersAsSemanticVersioning(string lower, string higher)
    {
        ReleaseVersion.TryParse(lower, out var a);
        ReleaseVersion.TryParse(higher, out var b);
        Assert.True(a < b, $"{a} < {b}");
        Assert.True(b > a, $"{b} > {a}");
    }

    [Fact]
    public void FormatsWithoutBuildMetadata()
    {
        ReleaseVersion.TryParse("v0.3.0-beta.1+abc", out var version);
        Assert.Equal("0.3.0-beta.1", version.ToString());
    }
}
