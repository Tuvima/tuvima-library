using MediaEngine.Domain.Services;
namespace MediaEngine.Domain.Tests;

public class IsbnValidationTests
{
    [Theory]
    [InlineData("3282476326", null)]
    [InlineData("9781542016421", "9781542016421")]
    [InlineData("urn:isbn:978-1-5420-1642-1", "9781542016421")]
    [InlineData("0-8044-2957-X", "080442957X")]
    [InlineData("9781542016422", null)]
    [InlineData("1234567890123", null)]
    public void RequiresValidChecksum(string input, string? expected) =>
        Assert.Equal(expected, IsbnValidation.NormalizeValid(input));
}
