using Dapper;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Tests;

/// <summary>Supporting files (universal editor v5 WP2): the values a person owns survive a rescan, inferred values are recomputed.</summary>
public sealed class SupportingFileRepositoryTests : IDisposable
{
    private const string Show = "/library/tv/Masterclass";
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ItemExtraRepository _extras;

    public SupportingFileRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_supporting_files_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _extras = new ItemExtraRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task ARescan_RecomputesWhatTheFoldersSay_AndNeverTouchesWhatAPersonSet()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("Workbook.pdf", role: ExtraRoles.Worksheet, scope: ExtraScope.Root)], []);
        var id = (await _extras.GetByPathAsync($"{Show}/Workbook.pdf"))!.Id;
        await _extras.UpdateOwnedAsync(id, new ExtraOwnedUpdate
        {
            Title = ExtraValue<string>.Set("Week 1 workbook"),
            Description = ExtraValue<string>.Set("Exercises for the first week"),
            Role = ExtraValue<string>.Set(ExtraRoles.Guide),
            Scope = ExtraValue<ExtraScope>.Set(ExtraScope.ForSeason(2)),
            Alongside = ExtraValue<ExtraScope>.Set(ExtraScope.ForEpisode(2, 3)),
            GroupName = ExtraValue<string>.Set("Homework"),
            AddTags = ["maths", "week 1"],
        });

        // The next pass reads different things from the folders.
        await _extras.SyncItemFolderAsync(
            Show,
            [Found("Workbook.pdf", role: ExtraRoles.Notes, scope: ExtraScope.ForSeason(1), alongside: ExtraScope.ForEpisode(1, 1), group: "Character guides")],
            []);

        var row = (await _extras.GetByPathAsync($"{Show}/Workbook.pdf"))!;
        Assert.Equal(id, row.Id);
        Assert.Equal("Week 1 workbook", row.DisplayTitle);
        Assert.Equal("Exercises for the first week", row.Description);
        Assert.Equal(ExtraRoles.Guide, row.EffectiveRole);
        Assert.Equal(ExtraScope.ForSeason(2), row.EffectiveScope);
        Assert.Equal(ExtraScopeSources.You, row.ScopeSource);
        Assert.Equal(ExtraScope.ForEpisode(2, 3), row.EffectiveAlongside);
        Assert.Equal("Homework", row.GroupName);
        Assert.Equal(["maths", "week 1"], row.TagList);

        // What the folders say is still recomputed, beside the person's values.
        Assert.Equal(ExtraRoles.Notes, row.InferredRole);
        Assert.Equal(ExtraScope.ForSeason(1), row.InferredScope);
        Assert.Equal(ExtraScope.ForEpisode(1, 1), row.InferredAlongside);
        Assert.Equal("Character guides", row.InferredGroup);
    }

    [Fact]
    public async Task AFileWithNothingSet_FollowsWhatTheFolderSaysOnEveryPass()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("Guide.pdf", role: ExtraRoles.Guide, scope: ExtraScope.ForSeason(1))], []);

        await _extras.SyncItemFolderAsync(Show, [Found("Guide.pdf", role: ExtraRoles.Worksheet, scope: ExtraScope.ForSeason(2), group: "Week 1")], []);

        var row = (await _extras.GetByPathAsync($"{Show}/Guide.pdf"))!;
        Assert.Equal(ExtraRoles.Worksheet, row.EffectiveRole);
        Assert.False(row.RoleIsSetByYou);
        Assert.Equal(ExtraScope.ForSeason(2), row.EffectiveScope);
        Assert.Equal(ExtraScopeSources.Folder, row.ScopeSource);
        Assert.Equal("Week 1", row.GroupName);
        Assert.Equal(ExtraGroupSources.Folder, row.GroupSource);
    }

    [Fact]
    public async Task AGroupAPersonChose_SurvivesARescanThatNamesAnotherGroup()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("Guide.pdf", group: "Character guides")], []);
        var id = (await _extras.GetByPathAsync($"{Show}/Guide.pdf"))!.Id;
        await _extras.UpdateOwnedAsync(id, new ExtraOwnedUpdate { GroupName = ExtraValue<string>.Set("Homework") });

        await _extras.SyncItemFolderAsync(Show, [Found("Guide.pdf", group: "Week 2")], []);

        var row = (await _extras.GetByPathAsync($"{Show}/Guide.pdf"))!;
        Assert.Equal("Homework", row.GroupName);
        Assert.Equal(ExtraGroupSources.You, row.GroupSource);
        Assert.Equal("Week 2", row.InferredGroup);
    }

    [Fact]
    public async Task ClearingAGroup_ReturnsToTheGroupTheFolderNames()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("Guide.pdf", group: "Character guides")], []);
        var id = (await _extras.GetByPathAsync($"{Show}/Guide.pdf"))!.Id;
        await _extras.UpdateOwnedAsync(id, new ExtraOwnedUpdate { GroupName = ExtraValue<string>.Set("Homework") });

        var reverted = await _extras.UpdateOwnedAsync(id, new ExtraOwnedUpdate { GroupName = ExtraValue<string>.Clear });

        Assert.Equal("Character guides", reverted!.GroupName);
        Assert.Equal(ExtraGroupSources.Folder, reverted.GroupSource);
    }

    [Fact]
    public async Task AGroupReadFromAFolder_GoesWhenNoFileIsInItAnyMore_ButAGroupAPersonMadeStays()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("Guide.pdf", group: "Character guides")], []);
        await _extras.EnsureGroupAsync(Show, "Homework");
        Assert.Equal(2, (await _extras.ListGroupsAsync(Show)).Count);

        await _extras.SyncItemFolderAsync(Show, [Found("Guide.pdf")], []);

        var groups = await _extras.ListGroupsAsync(Show);
        Assert.Equal(["Homework"], groups.Select(group => group.Name));
    }

    [Fact]
    public async Task ChangingScopeThenClearingIt_ReturnsToTheFolderLocation()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("Guide.pdf", scope: ExtraScope.ForSeason(1))], []);
        var id = (await _extras.GetByPathAsync($"{Show}/Guide.pdf"))!.Id;

        var moved = await _extras.UpdateOwnedAsync(id, new ExtraOwnedUpdate { Scope = ExtraValue<ExtraScope>.Set(ExtraScope.Root) });
        Assert.Equal(ExtraScope.Root, moved!.EffectiveScope);
        Assert.Equal(ExtraScopeSources.You, moved.ScopeSource);

        var back = await _extras.UpdateOwnedAsync(id, new ExtraOwnedUpdate { Scope = ExtraValue<ExtraScope>.Clear });
        Assert.Equal(ExtraScope.ForSeason(1), back!.EffectiveScope);
        Assert.Equal(ExtraScopeSources.Folder, back.ScopeSource);
    }

    [Fact]
    public async Task ShownAlongside_CanBeSetToNothing_OverridingTheGuess()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("Guide.pdf", alongside: ExtraScope.ForEpisode(1, 2))], []);
        var id = (await _extras.GetByPathAsync($"{Show}/Guide.pdf"))!.Id;
        Assert.Equal(ExtraScope.ForEpisode(1, 2), (await _extras.GetAsync(id))!.EffectiveAlongside);

        var none = await _extras.UpdateOwnedAsync(id, new ExtraOwnedUpdate { Alongside = ExtraValue<ExtraScope>.Set(ExtraScope.None) });
        Assert.Null(none!.EffectiveAlongside);

        var guess = await _extras.UpdateOwnedAsync(id, new ExtraOwnedUpdate { Alongside = ExtraValue<ExtraScope>.Clear });
        Assert.Equal(ExtraScope.ForEpisode(1, 2), guess!.EffectiveAlongside);
    }

    [Fact]
    public async Task ABulkChange_SetsGroupRoleScopeAndTagsOnEveryTickedFile()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("A.pdf"), Found("B.pdf"), Found("C.pdf")], []);
        var ids = new List<Guid>
        {
            (await _extras.GetByPathAsync($"{Show}/A.pdf"))!.Id,
            (await _extras.GetByPathAsync($"{Show}/B.pdf"))!.Id,
        };

        var changed = await _extras.UpdateOwnedManyAsync(ids, new ExtraOwnedUpdate
        {
            GroupName = ExtraValue<string>.Set("Week 1"),
            Role = ExtraValue<string>.Set(ExtraRoles.Worksheet),
            Scope = ExtraValue<ExtraScope>.Set(ExtraScope.ForSeason(3)),
            AddTags = ["maths"],
        });

        Assert.Equal(2, changed);
        foreach (var name in new[] { "A.pdf", "B.pdf" })
        {
            var row = (await _extras.GetByPathAsync($"{Show}/{name}"))!;
            Assert.Equal("Week 1", row.GroupName);
            Assert.Equal(ExtraRoles.Worksheet, row.EffectiveRole);
            Assert.True(row.RoleIsSetByYou);
            Assert.Equal(ExtraScope.ForSeason(3), row.EffectiveScope);
            Assert.Equal(["maths"], row.TagList);
        }

        var untouched = (await _extras.GetByPathAsync($"{Show}/C.pdf"))!;
        Assert.Null(untouched.GroupName);
        Assert.Empty(untouched.TagList);

        var groups = await _extras.ListGroupsAsync(Show);
        var group = Assert.Single(groups);
        Assert.Equal(("Week 1", 2), (group.Name, group.FileCount));
    }

    [Fact]
    public async Task Tags_ReuseTheGlobalList_CanBeAddedRemovedAndReplaced_AndMatchIgnoringCase()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("A.pdf"), Found("B.pdf")], []);
        var a = (await _extras.GetByPathAsync($"{Show}/A.pdf"))!.Id;
        var b = (await _extras.GetByPathAsync($"{Show}/B.pdf"))!.Id;

        await _extras.UpdateOwnedAsync(a, new ExtraOwnedUpdate { AddTags = ["Maths", "revision"] });
        await _extras.UpdateOwnedAsync(b, new ExtraOwnedUpdate { AddTags = ["maths"] });

        using (var connection = _db.CreateConnection())
        {
            Assert.Equal(2, connection.QuerySingle<int>("SELECT COUNT(*) FROM tags;"));
        }

        var removed = await _extras.UpdateOwnedAsync(a, new ExtraOwnedUpdate { RemoveTags = ["MATHS"] });
        Assert.Equal(["revision"], removed!.TagList);

        var replaced = await _extras.UpdateOwnedAsync(a, new ExtraOwnedUpdate { ReplaceTags = ["exam"] });
        Assert.Equal(["exam"], replaced!.TagList);
        Assert.Equal(["Maths"], (await _extras.GetAsync(b))!.TagList);
    }

    [Fact]
    public async Task ClearingTitleAndDescription_ShowsTheFileNameAgain()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("Workbook.pdf")], []);
        var id = (await _extras.GetByPathAsync($"{Show}/Workbook.pdf"))!.Id;
        await _extras.UpdateOwnedAsync(id, new ExtraOwnedUpdate { Title = ExtraValue<string>.Set("Mine"), Description = ExtraValue<string>.Set("About it") });

        var cleared = await _extras.UpdateOwnedAsync(id, new ExtraOwnedUpdate { Title = ExtraValue<string>.Clear, Description = ExtraValue<string>.Set("  ") });

        Assert.Equal("Workbook", cleared!.DisplayTitle);
        Assert.Null(cleared.Description);
    }

    [Fact]
    public async Task APlacePickedInTheV4Editor_ReadsAsTheScopeAndTheRole()
    {
        await _extras.SetDecisionAsync(Found("Season 1 trailer.mkv", ItemExtraKinds.Clip) with
        {
            Origin = ItemExtraOrigins.UserExtra,
            ExtraKind = ExtraKinds.Trailer,
            PlaceSeason = 1,
        });

        var row = (await _extras.GetByPathAsync($"{Show}/Season 1 trailer.mkv"))!;

        Assert.Equal(ExtraScope.ForSeason(1), row.EffectiveScope);
        Assert.Equal(ExtraScopeSources.You, row.ScopeSource);
        Assert.Equal(ExtraRoles.Trailer, row.EffectiveRole);
    }

    [Fact]
    public async Task Groups_CanBeRenamedReorderedAndDeleted_AndFilesFallBackToUngrouped()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("A.pdf")], []);
        var id = (await _extras.GetByPathAsync($"{Show}/A.pdf"))!.Id;
        var one = await _extras.EnsureGroupAsync(Show, "One");
        var two = await _extras.EnsureGroupAsync(Show, "Two");
        Assert.Equal(one.Id, (await _extras.EnsureGroupAsync(Show, "ONE")).Id);
        await _extras.UpdateOwnedAsync(id, new ExtraOwnedUpdate { GroupName = ExtraValue<string>.Set("Two") });

        await _extras.ReorderGroupsAsync(Show, [two.Id, one.Id]);
        Assert.Equal(["Two", "One"], (await _extras.ListGroupsAsync(Show)).Select(group => group.Name));

        Assert.False(await _extras.RenameGroupAsync(two.Id, "one"));
        Assert.True(await _extras.RenameGroupAsync(two.Id, "Homework"));
        Assert.Equal("Homework", (await _extras.GetAsync(id))!.GroupName);

        Assert.True(await _extras.DeleteGroupAsync(two.Id));
        Assert.Null((await _extras.GetAsync(id))!.GroupName);
    }

    [Fact]
    public async Task ARenamedFolderGroup_KeepsItsFilesAcrossARescan_AndTheOldFolderGroupDoesNotComeBack()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("A.pdf", group: "Character guides"), Found("B.pdf", group: "Character guides")], []);
        var group = Assert.Single(await _extras.ListGroupsAsync(Show));
        Assert.True(await _extras.RenameGroupAsync(group.Id, "Guides"));

        // The folder on disk still says "Character guides".
        await _extras.SyncItemFolderAsync(Show, [Found("A.pdf", group: "Character guides"), Found("B.pdf", group: "Character guides")], []);

        var groups = await _extras.ListGroupsAsync(Show);
        var only = Assert.Single(groups);
        Assert.Equal("Guides", only.Name);
        Assert.Equal(2, only.FileCount);
        foreach (var name in new[] { "A.pdf", "B.pdf" })
        {
            var row = (await _extras.GetByPathAsync($"{Show}/{name}"))!;
            Assert.Equal("Guides", row.GroupName);
            Assert.Equal(ExtraGroupSources.You, row.GroupSource);
            Assert.Equal("Character guides", row.InferredGroup);
        }
    }

    [Fact]
    public async Task AFileTheRuleNoLongerReadsAsAnExtra_KeepsItsRecordWhenAPersonSetAnything_AndLosesItOtherwise()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("Plain.pdf"), Found("Titled.pdf"), Found("Tagged.pdf"), Found("Grouped.pdf"), Found("Moved.pdf")], []);
        async Task<Guid> IdOf(string name) => (await _extras.GetByPathAsync($"{Show}/{name}"))!.Id;
        await _extras.UpdateOwnedAsync(await IdOf("Titled.pdf"), new ExtraOwnedUpdate { Title = ExtraValue<string>.Set("My title") });
        await _extras.UpdateOwnedAsync(await IdOf("Tagged.pdf"), new ExtraOwnedUpdate { AddTags = ["maths"] });
        await _extras.UpdateOwnedAsync(await IdOf("Grouped.pdf"), new ExtraOwnedUpdate { GroupName = ExtraValue<string>.Set("Homework") });
        await _extras.UpdateOwnedAsync(await IdOf("Moved.pdf"), new ExtraOwnedUpdate { Scope = ExtraValue<ExtraScope>.Set(ExtraScope.ForSeason(2)) });

        // The files are all still on disk (none is in goneFiles) but the rule now reads none of them.
        await _extras.SyncItemFolderAsync(Show, [], []);

        Assert.Null(await _extras.GetByPathAsync($"{Show}/Plain.pdf"));
        Assert.Equal("My title", (await _extras.GetByPathAsync($"{Show}/Titled.pdf"))!.DisplayTitle);
        Assert.Equal(["maths"], (await _extras.GetByPathAsync($"{Show}/Tagged.pdf"))!.TagList);
        Assert.Equal("Homework", (await _extras.GetByPathAsync($"{Show}/Grouped.pdf"))!.GroupName);
        Assert.Equal(ExtraScope.ForSeason(2), (await _extras.GetByPathAsync($"{Show}/Moved.pdf"))!.EffectiveScope);
    }

    [Fact]
    public async Task ARoleTheConfiguredListDoesNotOfferForTheKindOfFile_IsRefused_AndNothingChanges()
    {
        var rules = new ItemExtraRepository(_db, new FixedRules());
        await rules.SyncItemFolderAsync(Show, [Found("Guide.pdf"), Found("Photo.jpg", ItemExtraKinds.Picture)], []);
        var document = (await rules.GetByPathAsync($"{Show}/Guide.pdf"))!.Id;
        var picture = (await rules.GetByPathAsync($"{Show}/Photo.jpg"))!.Id;

        var refused = await Assert.ThrowsAsync<ExtraRoleNotAllowedException>(
            () => rules.UpdateOwnedAsync(document, new ExtraOwnedUpdate { Role = ExtraValue<string>.Set(ExtraRoles.Trailer) }));
        Assert.Equal(ExtraRoles.Trailer, refused.Role);
        Assert.False((await rules.GetAsync(document))!.RoleIsSetByYou);

        var accepted = await rules.UpdateOwnedAsync(document, new ExtraOwnedUpdate { Role = ExtraValue<string>.Set(ExtraRoles.Worksheet) });
        Assert.Equal(ExtraRoles.Worksheet, accepted!.EffectiveRole);

        // Clearing is always allowed, and a bulk change that includes a file the role does not fit changes none of them.
        var cleared = await rules.UpdateOwnedAsync(document, new ExtraOwnedUpdate { Role = ExtraValue<string>.Clear });
        Assert.False(cleared!.RoleIsSetByYou);
        await Assert.ThrowsAsync<ExtraRoleNotAllowedException>(
            () => rules.UpdateOwnedManyAsync([document, picture], new ExtraOwnedUpdate { Role = ExtraValue<string>.Set(ExtraRoles.Guide) }));
        Assert.False((await rules.GetAsync(document))!.RoleIsSetByYou);
    }

    [Fact]
    public async Task RemovingATitlesExtras_AlsoRemovesItsGroupsAndTagLinks()
    {
        await _extras.SyncItemFolderAsync(Show, [Found("A.pdf")], []);
        var id = (await _extras.GetByPathAsync($"{Show}/A.pdf"))!.Id;
        await _extras.UpdateOwnedAsync(id, new ExtraOwnedUpdate { GroupName = ExtraValue<string>.Set("Homework"), AddTags = ["maths"] });
        await _extras.EnsureGroupAsync("/library/tv/Other", "Kept");

        await _extras.RemoveUnderFolderAsync(Show);

        Assert.Empty(await _extras.ListGroupsAsync(Show));
        Assert.Single(await _extras.ListGroupsAsync("/library/tv/Other"));
        using var connection = _db.CreateConnection();
        Assert.Equal(0, connection.QuerySingle<int>("SELECT COUNT(*) FROM item_extra_tag_links;"));
    }

    [Fact]
    public void AScopeKey_ReadsBackToTheSameScope()
    {
        var scopes = new[]
        {
            ExtraScope.Root,
            ExtraScope.ForSeason(0),
            ExtraScope.ForSeason(2),
            ExtraScope.ForEpisode(2, 3),
            ExtraScope.ForTrack(4),
            ExtraScope.ForFolder("Section 1"),
            ExtraScope.ForFile("Section 1\\02 Intro.mkv"),
            ExtraScope.None,
        };

        foreach (var scope in scopes)
        {
            Assert.True(ExtraScope.TryParse(scope.Key, out var parsed), scope.Key);
            Assert.Equal(scope, parsed);
        }

        Assert.Equal("file:Section 1/02 Intro.mkv", ExtraScope.ForFile("Section 1\\02 Intro.mkv").Key);
        Assert.False(ExtraScope.TryParse("season:x", out _));
        Assert.False(ExtraScope.TryParse("folder:", out _));
    }

    /// <summary>A small role list per kind of file, as <c>config/extras.json</c> gives.</summary>
    private sealed class FixedRules : IExtrasConfigurationSource
    {
        public ExtrasConfiguration Current { get; } = new()
        {
            Roles = new Dictionary<string, List<ExtrasRoleEntry>>(StringComparer.OrdinalIgnoreCase)
            {
                ["document"] = [new() { Id = ExtraRoles.Guide, Label = "Guide" }, new() { Id = ExtraRoles.Worksheet, Label = "Worksheet" }],
                ["picture"] = [new() { Id = ExtraRoles.Pictures, Label = "Pictures" }],
                ["clip"] = [new() { Id = ExtraRoles.Trailer, Label = "Trailer" }, new() { Id = ExtraRoles.Clip, Label = "Clip" }],
            },
        };
    }

    private static ItemExtra Found(
        string fileName,
        string kind = ItemExtraKinds.Document,
        string? role = null,
        ExtraScope? scope = null,
        ExtraScope? alongside = null,
        string? group = null) => new(
        Guid.NewGuid(), "lib", Show, $"{Show}/{fileName}", kind, ItemExtraOrigins.Rule,
        Path.GetFileNameWithoutExtension(fileName), null, 0, 10, null, null, DateTimeOffset.UtcNow,
        InferredRole: role,
        InferredGroup: group,
        InferredScope: scope ?? ExtraScope.Root,
        InferredAlongside: alongside);
}
