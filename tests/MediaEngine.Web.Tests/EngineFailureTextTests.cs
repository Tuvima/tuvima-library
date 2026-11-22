using MediaEngine.Web.Services.Integration;

namespace MediaEngine.Web.Tests;

/// <summary>What the Engine client keeps in LastError is turned into one plain sentence for the person: no status code, no "Invalid request." lead-in, no trace number.</summary>
public sealed class EngineFailureTextTests
{
    [Theory]
    [InlineData("HTTP 400: Invalid request. Every Gallery item must belong to the Gallery owner's Personal Space. Trace: 0HNQ7IA7J1RVB:0000000F", "Every Gallery item must belong to the Gallery owner's Personal Space.")]
    [InlineData("POST /collections/{id}/items returned 403: The active profile cannot edit this collection.", "The active profile cannot edit this collection.")]
    [InlineData("  The playlist is full.  ", "The playlist is full.")]
    public void Plain_KeepsOnlyTheEnginesExplanation(string lastError, string expected) =>
        Assert.Equal(expected, EngineFailureText.Plain(lastError));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HTTP 500: Trace: 0HNQ7:1")]
    public void Plain_IsNull_WhenThereIsNothingAPersonCouldUse(string? lastError) =>
        Assert.Null(EngineFailureText.Plain(lastError));
}
