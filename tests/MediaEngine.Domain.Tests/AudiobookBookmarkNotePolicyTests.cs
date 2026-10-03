using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class AudiobookBookmarkNotePolicyTests
{
    [Fact]
    public void TryNormalize_TrimsOuterWhitespaceAndMapsBlankToNull()
    {
        Assert.True(AudiobookBookmarkNotePolicy.TryNormalize("  useful note\n", out var normalized));
        Assert.Equal("useful note", normalized);

        Assert.True(AudiobookBookmarkNotePolicy.TryNormalize(" \t\r\n ", out normalized));
        Assert.Null(normalized);
    }

    [Fact]
    public void TryNormalize_PreservesInternalNewlinesAndAcceptsExactlyTwoHundredUtf16Units()
    {
        var note = new string('a', 99) + "\n" + new string('b', 100);
        Assert.Equal(AudiobookBookmarkNotePolicy.MaximumLength, note.Length);

        Assert.True(AudiobookBookmarkNotePolicy.TryNormalize(note, out var normalized));
        Assert.Equal(note, normalized);

        var supplementaryCharacters = string.Concat(Enumerable.Repeat("\U0001F3B5", 100));
        Assert.Equal(AudiobookBookmarkNotePolicy.MaximumLength, supplementaryCharacters.Length);
        Assert.True(AudiobookBookmarkNotePolicy.TryNormalize(supplementaryCharacters, out normalized));
        Assert.Equal(supplementaryCharacters, normalized);
    }

    [Fact]
    public void TryNormalize_RejectsMoreThanTwoHundredUtf16Units()
    {
        var note = new string('x', AudiobookBookmarkNotePolicy.MaximumLength + 1);

        Assert.False(AudiobookBookmarkNotePolicy.TryNormalize(note, out var normalized));
        Assert.Null(normalized);

        var paddedAtLimit = " " + new string('x', 198) + " ";
        Assert.True(AudiobookBookmarkNotePolicy.TryNormalize(paddedAtLimit, out normalized));
        Assert.Equal(new string('x', 198), normalized);

        var paddedOverLimit = " " + new string('x', 199) + " ";
        Assert.False(AudiobookBookmarkNotePolicy.TryNormalize(paddedOverLimit, out normalized));
        Assert.Null(normalized);
    }
}
