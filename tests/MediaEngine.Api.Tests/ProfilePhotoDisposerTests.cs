using MediaEngine.Api.Services.View;

namespace MediaEngine.Api.Tests;

/// <summary>The name that kept photos carry in the Shared Library when a person is removed.</summary>
public sealed class ProfilePhotoDisposerTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 10, 23, 30, 0, TimeSpan.Zero);

    [Fact]
    public void KeptPhotos_AreNamedAfterThePersonAndTheDate() =>
        Assert.Equal("From Alex 2026-10-10", ProfilePhotoDisposer.SharedLabel("Alex", Day));

    [Fact]
    public void KeptPhotos_DropCharactersAFolderNameCannotHold() =>
        Assert.Equal("From Alex 2026-10-10", ProfilePhotoDisposer.SharedLabel("  Al/ex. ", Day));

    [Fact]
    public void KeptPhotos_HaveANameEvenWhenTheOnlyCharactersAreInvalid() =>
        Assert.Equal("From a removed profile 2026-10-10", ProfilePhotoDisposer.SharedLabel("///", Day));
}
