using MediaEngine.Api.Services.Faces;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>Spec view-people-search WP7: faces are found in a person's own photos, look-alikes are grouped, and nothing outside that person's library is touched.</summary>
public sealed class FaceScanTests
{
    [Fact]
    public async Task LookAlikesAreGrouped_PhotosAreLookedAtOnce_AndTheScanResumes()
    {
        using var fixture = new ViewLibraryServiceTests.ViewFixture(contentKind: ViewContentKinds.Photos);
        fixture.WritePersonal("one.png", OcrFixtures.Png(600, 400));
        fixture.WritePersonal("two.png", OcrFixtures.Png(640, 420));
        fixture.WritePersonal("three.png", OcrFixtures.Png(660, 440));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        var engine = new ScriptedEngine();
        var faces = new ViewFaceRepository(fixture.Database);
        var scanner = new FaceScanService(faces, new ViewProfileRepository(fixture.Database), fixture.Spaces, engine, NullLogger<FaceScanService>.Instance);

        // Photo order is by item id, so script by call order: Sam, Sam again (a little different), and Alex.
        engine.Script = [[Sam(0.01f)], [Sam(0.03f)], [Alex()]];
        Assert.Equal(2, await scanner.ScanBatchAsync(fixture.OwnerProfileId, 2, CancellationToken.None));

        var afterTwo = await faces.GetProgressAsync(fixture.OwnerProfileId, fixture.PersonalLibraryId);
        Assert.Equal(2, afterTwo.PhotosLooked);
        Assert.Equal(2, afterTwo.Faces);
        Assert.Equal(1, afterTwo.UnnamedGroups);

        // The next call carries on with the one photo left (as after a restart) and never looks at a photo twice.
        Assert.Equal(1, await scanner.ScanBatchAsync(fixture.OwnerProfileId, 5, CancellationToken.None));
        Assert.Equal(0, await scanner.ScanBatchAsync(fixture.OwnerProfileId, 5, CancellationToken.None));
        Assert.Equal(3, engine.Calls);
        var done = await faces.GetProgressAsync(fixture.OwnerProfileId, fixture.PersonalLibraryId);
        Assert.Equal(3, done.Faces);
        Assert.Equal(2, done.UnnamedGroups);
        Assert.Equal(3, done.PhotosLooked);
    }

    [Fact]
    public async Task TwoFacesInOnePhotoAreNeverTheSamePerson()
    {
        using var fixture = new ViewLibraryServiceTests.ViewFixture(contentKind: ViewContentKinds.Photos);
        fixture.WritePersonal("pair.png", OcrFixtures.Png(600, 400));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        var engine = new ScriptedEngine { Script = [[Sam(0f), Sam(0.01f)]] };
        var faces = new ViewFaceRepository(fixture.Database);
        var scanner = new FaceScanService(faces, new ViewProfileRepository(fixture.Database), fixture.Spaces, engine, NullLogger<FaceScanService>.Instance);

        await scanner.ScanBatchAsync(fixture.OwnerProfileId, 5, CancellationToken.None);

        Assert.Equal(2, (await faces.GetProgressAsync(fixture.OwnerProfileId, fixture.PersonalLibraryId)).UnnamedGroups);
    }

    [Fact]
    public async Task SwitchedOff_OrNotReady_NothingIsLookedAt_AndDeleteAllRemovesEveryRow()
    {
        using var fixture = new ViewLibraryServiceTests.ViewFixture(contentKind: ViewContentKinds.Photos);
        fixture.WritePersonal("one.png", OcrFixtures.Png(600, 400));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        var profiles = new ViewProfileRepository(fixture.Database);
        var faces = new ViewFaceRepository(fixture.Database);
        var engine = new ScriptedEngine { Script = [[Sam(0f)]] };
        var scanner = new FaceScanService(faces, profiles, fixture.Spaces, engine, NullLogger<FaceScanService>.Instance);

        Assert.True((await profiles.GetPreferencesAsync(fixture.OwnerProfileId)).RecognizePeople);
        Assert.True(await profiles.SaveRecognizePeopleAsync(fixture.OwnerProfileId, false));
        Assert.Equal(0, await scanner.ScanBatchAsync(fixture.OwnerProfileId, 5, CancellationToken.None));
        Assert.Equal(0, engine.Calls);

        engine.Available = false;
        Assert.True(await profiles.SaveRecognizePeopleAsync(fixture.OwnerProfileId, true));
        Assert.Equal(0, await scanner.ScanBatchAsync(fixture.OwnerProfileId, 5, CancellationToken.None));

        engine.Available = true;
        Assert.Equal(1, await scanner.ScanBatchAsync(fixture.OwnerProfileId, 5, CancellationToken.None));
        Assert.Equal(1, await faces.DeleteAllAsync(fixture.OwnerProfileId));
        var empty = await faces.GetProgressAsync(fixture.OwnerProfileId, fixture.PersonalLibraryId);
        Assert.Equal((0, 0, 0), (empty.PhotosLooked, empty.Faces, empty.UnnamedGroups));
    }

    [Fact]
    public async Task OnlyThePersonsOwnLibraryIsOffered_NotAnotherPersonsAndNotTheVault()
    {
        using var fixture = new ViewLibraryServiceTests.ViewFixture(contentKind: ViewContentKinds.Photos);
        fixture.WritePersonal("one.png", OcrFixtures.Png(600, 400));
        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        var faces = new ViewFaceRepository(fixture.Database);

        var own = await faces.GetUnscannedPhotosAsync(fixture.OwnerProfileId, fixture.PersonalLibraryId, null, 10, FaceImageFormats.Readable);
        Assert.Single(own);
        Assert.Empty(await faces.GetUnscannedPhotosAsync(Guid.NewGuid(), fixture.PersonalLibraryId, null, 10, FaceImageFormats.Readable));
        Assert.Empty(await faces.GetUnscannedPhotosAsync(fixture.OwnerProfileId, Guid.NewGuid(), null, 10, FaceImageFormats.Readable));
    }

    private static ViewDetectedFace Sam(float wobble) => Face(1f, wobble);

    private static ViewDetectedFace Alex() => Face(-1f, 0f);

    private static ViewDetectedFace Face(float sign, float wobble)
    {
        var look = new float[128];
        look[0] = sign;
        look[1] = wobble;
        return new ViewDetectedFace(0.2, 0.2, 0.3, 0.3, 0.9, look);
    }

    private sealed class ScriptedEngine : IFaceEngine
    {
        public IReadOnlyList<IReadOnlyList<ViewDetectedFace>> Script { get; set; } = [];

        public bool Available { get; set; } = true;

        public int Calls { get; private set; }

        public string ModelId => "test";

        public bool IsAvailable => Available;

        public Task<IReadOnlyList<ViewDetectedFace>> AnalyseAsync(string imagePath, CancellationToken ct) =>
            Task.FromResult(Script[Calls++]);
    }
}
