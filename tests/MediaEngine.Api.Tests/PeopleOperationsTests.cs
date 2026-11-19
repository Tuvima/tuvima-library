using Dapper;
using MediaEngine.Api.Services.Faces;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>Spec view-people-search WP8: naming, suggestions, "Not Sam", merge, hide, cover, remove a face and remove a person.</summary>
public sealed class PeopleOperationsTests
{
    [Fact]
    public async Task NamingOffersLookAlikes_ConfirmJoinsThem_AndNotThemKeepsThemApart()
    {
        using var fixture = await ScannedAsync(Look(1f, 0f), Look(0.3f, 0.954f), Look(-1f, 0f));
        var faces = new ViewFaceRepository(fixture.Database);
        var owner = fixture.OwnerProfileId;
        var groups = await faces.GetGroupsAsync(owner, false, false, 10);
        Assert.Equal(3, groups.Count);

        var sam = await GroupHolding(faces, owner, fixture, 1f);
        var named = await faces.NamePersonAsync(owner, sam.PersonId, "  Sam ");
        Assert.NotNull(named);
        Assert.False(named!.MergedIntoExisting);
        Assert.Equal("Sam", (await faces.GetGroupAsync(owner, sam.PersonId))!.Name);

        var suggestions = await new ViewPeopleService(faces).SuggestAsync(owner, sam.PersonId, CancellationToken.None);
        var near = Assert.Single(suggestions);
        Assert.Null(near.Group.Name);

        // "Not Sam": the near group is never offered again and stays its own group.
        Assert.True(await faces.RejectFacesAsync(owner, sam.PersonId, near.Faces.Select(face => face.FaceId).ToList()));
        Assert.Empty(await new ViewPeopleService(faces).SuggestAsync(owner, sam.PersonId, CancellationToken.None));
        Assert.Equal(2, (await faces.GetGroupsAsync(owner, false, false, 10)).Count);

        // A yes joins the accepted faces and leaves unticked ones out.
        var other = await GroupHolding(faces, owner, fixture, -1f);
        Assert.True(await faces.ConfirmFacesAsync(owner, sam.PersonId, (await faces.GetFacesOfPersonAsync(owner, other.PersonId, 10)).Select(face => face.FaceId).ToList(), []));
        Assert.Equal(2, (await faces.GetGroupAsync(owner, sam.PersonId))!.FaceCount);
    }

    [Fact]
    public async Task ANameThatAlreadyBelongsToSomeone_JoinsThem_AndNamesAreWrittenToThePhotosPeopleList()
    {
        using var fixture = await ScannedAsync(Look(1f, 0f), Look(-1f, 0f));
        var faces = new ViewFaceRepository(fixture.Database);
        var owner = fixture.OwnerProfileId;
        var first = await GroupHolding(faces, owner, fixture, 1f);
        var second = await GroupHolding(faces, owner, fixture, -1f);

        Assert.False((await faces.NamePersonAsync(owner, first.PersonId, "Sam"))!.MergedIntoExisting);
        var joined = await faces.NamePersonAsync(owner, second.PersonId, "sam");
        Assert.True(joined!.MergedIntoExisting);
        Assert.Equal(first.PersonId, joined.PersonId);
        Assert.Equal(2, (await faces.GetGroupAsync(owner, first.PersonId))!.FaceCount);
        Assert.Null(await faces.GetGroupAsync(owner, second.PersonId));
        Assert.Equal(2, FaceNameAnnotations(fixture));

        Assert.True(await faces.SetHiddenAsync(owner, first.PersonId, true));
        Assert.Equal(0, FaceNameAnnotations(fixture));
        Assert.Empty(await faces.GetGroupsAsync(owner, true, false, 10));
        Assert.Single(await faces.GetGroupsAsync(owner, true, true, 10));
    }

    [Fact]
    public async Task CoverMergeRemoveFaceAndRemovePerson_EachDoOnlyTheirOwnJob_AndOtherPeopleAreNeverReachable()
    {
        using var fixture = await ScannedAsync(Look(1f, 0f), Look(-1f, 0f));
        var faces = new ViewFaceRepository(fixture.Database);
        var owner = fixture.OwnerProfileId;
        var first = await GroupHolding(faces, owner, fixture, 1f);
        var second = await GroupHolding(faces, owner, fixture, -1f);
        var firstFace = (await faces.GetFacesOfPersonAsync(owner, first.PersonId, 5)).Single();

        // Someone else's profile can neither read nor change these people.
        var stranger = Guid.NewGuid();
        Assert.Null(await faces.GetGroupAsync(stranger, first.PersonId));
        Assert.False(await faces.SetHiddenAsync(stranger, first.PersonId, true));
        Assert.False(await faces.DeletePersonAsync(stranger, first.PersonId));
        Assert.Null(await faces.NamePersonAsync(stranger, first.PersonId, "Mallory"));

        Assert.True(await faces.SetCoverAsync(owner, first.PersonId, firstFace.FaceId));
        Assert.Equal(firstFace.FaceId, (await faces.GetGroupAsync(owner, first.PersonId))!.CoverFaceId);
        Assert.False(await faces.SetCoverAsync(owner, second.PersonId, firstFace.FaceId));

        Assert.True(await faces.MergeAsync(owner, second.PersonId, first.PersonId));
        Assert.Equal(2, (await faces.GetGroupAsync(owner, first.PersonId))!.FaceCount);
        Assert.Null(await faces.GetGroupAsync(owner, second.PersonId));

        Assert.True(await faces.RemoveFaceAsync(owner, firstFace.FaceId));
        Assert.Equal(1, (await faces.GetGroupAsync(owner, first.PersonId))!.FaceCount);
        Assert.True(await faces.DeletePersonAsync(owner, first.PersonId));
        Assert.Empty(await faces.GetGroupsAsync(owner, null, true, 10));
    }

    private static async Task<ViewLibraryServiceTests.ViewFixture> ScannedAsync(params float[][] looks)
    {
        var fixture = new ViewLibraryServiceTests.ViewFixture(contentKind: ViewContentKinds.Photos);
        for (var i = 0; i < looks.Length; i++)
        {
            fixture.WritePersonal($"photo-{i}.png", OcrFixtures.Png(600 + i * 10, 400));
        }

        await fixture.Service.ScanAsync(fixture.PersonalLibraryId);
        var engine = new ScriptedEngine(looks);
        var scanner = new FaceScanService(new ViewFaceRepository(fixture.Database), new ViewProfileRepository(fixture.Database), fixture.Spaces, engine, NullLogger<FaceScanService>.Instance);
        Assert.Equal(looks.Length, await scanner.ScanBatchAsync(fixture.OwnerProfileId, 20, CancellationToken.None));
        return fixture;
    }

    // Photos are looked at in id order, so the face for a look is found by its fingerprint rather than by position.
    private static async Task<ViewFaceGroup> GroupHolding(ViewFaceRepository faces, Guid owner, ViewLibraryServiceTests.ViewFixture fixture, float firstValue)
    {
        var looks = await faces.GetAssignedLooksAsync(owner);
        var match = looks.Single(look => Math.Abs(look.Embedding[0] - Normalized(firstValue)) < 0.01f);
        return (await faces.GetGroupAsync(owner, match.PersonId!.Value))!;
    }

    private static float Normalized(float value) => value switch { 1f => 1f, -1f => -1f, _ => value };

    private static int FaceNameAnnotations(ViewLibraryServiceTests.ViewFixture fixture)
    {
        using var connection = fixture.Database.CreateConnection();
        return connection.ExecuteScalar<int>("SELECT COUNT(1) FROM local_item_annotations WHERE annotation_kind = 'face_name' AND source = 'faces';");
    }

    private static float[] Look(float a, float b)
    {
        var look = new float[128];
        look[0] = a;
        look[1] = b;
        return look;
    }

    private sealed class ScriptedEngine(float[][] looks) : IFaceEngine
    {
        private int _calls;

        public string ModelId => "test";

        public bool IsAvailable => true;

        public Task<IReadOnlyList<ViewDetectedFace>> AnalyseAsync(string imagePath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ViewDetectedFace>>([new ViewDetectedFace(0.2, 0.2, 0.3, 0.3, 0.9, looks[_calls++])]);
    }
}
