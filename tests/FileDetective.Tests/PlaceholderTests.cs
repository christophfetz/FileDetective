using FileDetective.Core;

namespace FileDetective.Tests;

public class PlaceholderTests
{
    [Fact]
    public void Describe_ReturnsProductName()
    {
        Assert.Equal("FileDetective", Placeholder.Describe());
    }
}