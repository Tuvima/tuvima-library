using MediaEngine.Api.Services.ReadServices;

namespace MediaEngine.Api.Tests;

public sealed class PersonSearchDistinguishingLineTests
{
    [Theory]
    [InlineData("Actor", "+1981-01-28T00:00:00Z", "fallback", "Actor, born 1981")]
    [InlineData("Composer", null, "fallback", "Composer")]
    [InlineData(null, "1957-01-01", "fallback", "Born 1957")]
    [InlineData(null, null, "fallback", "fallback")]
    [InlineData("  ", "bad", "fallback", "fallback")]
    public void DistinguishingLine_NamesJobAndBirthYearForSameNamedPeople(string? job, string? born, string? fallback, string? expected)
    {
        Assert.Equal(expected, UniversalSearchReadService.DistinguishingLine(job, born, fallback));
    }
}
