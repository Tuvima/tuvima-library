using MediaEngine.Storage;

namespace MediaEngine.Storage.Tests;

public sealed class DescriptionTextTests
{
    [Theory]
    [InlineData("<p>First &amp; second</p><p>Next</p>", "First & second\n\nNext")]
    [InlineData("&lt;p&gt;Hello&lt;/p&gt;", "Hello")]
    [InlineData("<script>alert(1)</script><p onclick='bad()'>Safe</p><style>body{}</style>", "Safe")]
    [InlineData("2 < 3 and 4 > 1", "2 < 3 and 4 > 1")]
    public void NormalizesProviderTextWithoutExecutableContent(string source, string expected)
    {
        var actual = DescriptionText.Normalize(source);
        Assert.Equal(expected, actual);
        Assert.Equal(actual, DescriptionText.Normalize(actual));
    }
}
