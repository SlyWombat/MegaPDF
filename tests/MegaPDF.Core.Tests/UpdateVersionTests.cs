using MegaPDF.Core.Services;
using Xunit;

namespace MegaPDF.Core.Tests;

public class UpdateVersionTests
{
    [Theory]
    [InlineData("v1.4.0", "1.3.0.0", true)]
    [InlineData("v1.3.1", "1.3.0.0", true)]
    [InlineData("v2.0", "1.9.9.9", true)]
    [InlineData("v1.3.0", "1.3.0.0", false)]  // same
    [InlineData("v1.2.9", "1.3.0.0", false)]  // older
    [InlineData("1.4.0", "1.3.0.0", true)]    // no v prefix
    [InlineData("nightly", "1.3.0.0", false)] // garbage tag
    [InlineData("", "1.3.0.0", false)]
    [InlineData(null, "1.3.0.0", false)]
    public void IsNewer_ComparesTagAgainstCurrent(string? tag, string current, bool expected) =>
        Assert.Equal(expected, UpdateVersion.IsNewer(tag, Version.Parse(current)));
}
