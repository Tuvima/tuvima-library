using MediaEngine.Domain.Models;

namespace MediaEngine.Storage.Tests;

public sealed class ItemExtraRepositoryTests : IDisposable
{
    private const string Show = "/library/tv/Masterclass";
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ItemExtraRepository _extras;

    public ItemExtraRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_item_extras_{Guid.NewGuid():N}.db");
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
    public async Task Sync_StoresWhatWasFound_AndListsItByFolder()
    {
        await _extras.SyncItemFolderAsync(Show, [Extra("Workbook.pdf", ItemExtraKinds.Document)], []);

        var listed = await _extras.ListByItemFoldersAsync([Show]);

        var only = Assert.Single(listed);
        Assert.Equal("Workbook", only.Title);
        Assert.Equal(ItemExtraOrigins.Rule, only.Origin);
    }

    [Fact]
    public async Task Sync_AFileFoundAgainKeepsItsId_AndAFileThatIsGoneIsRemoved()
    {
        await _extras.SyncItemFolderAsync(Show, [Extra("Workbook.pdf", ItemExtraKinds.Document), Extra("Trailer.mkv", ItemExtraKinds.Clip)], []);
        var firstId = (await _extras.GetByPathAsync($"{Show}/Workbook.pdf"))!.Id;

        await _extras.SyncItemFolderAsync(Show, [Extra("Workbook.pdf", ItemExtraKinds.Document)], []);

        var listed = await _extras.ListByItemFoldersAsync([Show]);
        var only = Assert.Single(listed);
        Assert.Equal(firstId, only.Id);
    }

    [Fact]
    public async Task Sync_NeverTouchesAnotherFoldersExtras()
    {
        await _extras.SyncItemFolderAsync("/library/tv/Other", [Extra("Guide.pdf", ItemExtraKinds.Document, "/library/tv/Other")], []);

        await _extras.SyncItemFolderAsync(Show, [], []);

        Assert.Single(await _extras.ListByItemFoldersAsync(["/library/tv/Other"]));
    }

    [Fact]
    public async Task APersonsMoveToExtras_SurvivesTheRulesNextPass()
    {
        await _extras.SetDecisionAsync(Extra("Lesson 03.mp4", ItemExtraKinds.Clip) with { Origin = ItemExtraOrigins.UserExtra });

        await _extras.SyncItemFolderAsync(Show, [], []);

        var kept = Assert.Single(await _extras.ListByItemFoldersAsync([Show]));
        Assert.Equal(ItemExtraOrigins.UserExtra, kept.Origin);
    }

    [Fact]
    public async Task APersonsMoveToAnEpisode_HidesTheFile_AndTheRuleDoesNotBringItBack()
    {
        await _extras.SyncItemFolderAsync(Show, [Extra("Making of.mkv", ItemExtraKinds.Clip)], []);
        await _extras.SetDecisionAsync(Extra("Making of.mkv", ItemExtraKinds.Clip) with { Origin = ItemExtraOrigins.UserEpisode });

        await _extras.SyncItemFolderAsync(Show, [Extra("Making of.mkv", ItemExtraKinds.Clip)], []);

        Assert.Empty(await _extras.ListByItemFoldersAsync([Show]));
        Assert.Equal(ItemExtraOrigins.UserEpisode, (await _extras.GetByPathAsync($"{Show}/Making of.mkv"))!.Origin);
    }

    [Fact]
    public async Task APersonsDecision_GoesWhenItsFileIsGone()
    {
        await _extras.SetDecisionAsync(Extra("Lesson 03.mp4", ItemExtraKinds.Clip) with { Origin = ItemExtraOrigins.UserExtra });

        await _extras.SyncItemFolderAsync(Show, [], [$"{Show}/Lesson 03.mp4"]);

        Assert.Empty(await _extras.ListByItemFoldersAsync([Show]));
    }

    [Fact]
    public async Task RemoveUnderFolder_TakesTheTitlesExtrasAndNothingElse()
    {
        await _extras.SyncItemFolderAsync(Show, [Extra("Workbook.pdf", ItemExtraKinds.Document)], []);
        await _extras.SyncItemFolderAsync("/library/tv/Masterclass 2", [Extra("Guide.pdf", ItemExtraKinds.Document, "/library/tv/Masterclass 2")], []);

        var removed = await _extras.RemoveUnderFolderAsync(Show);

        Assert.Equal(1, removed);
        Assert.Single(await _extras.ListByItemFoldersAsync(["/library/tv/Masterclass 2"]));
    }

    [Fact]
    public async Task Paths_CompareWithoutRegardToCase()
    {
        await _extras.SyncItemFolderAsync(Show, [Extra("Workbook.pdf", ItemExtraKinds.Document)], []);

        Assert.NotNull(await _extras.GetByPathAsync(Show.ToUpperInvariant() + "/WORKBOOK.PDF"));
        Assert.Single(await _extras.ListByItemFoldersAsync([Show.ToUpperInvariant()]));
    }

    private static ItemExtra Extra(string fileName, string kind, string folder = Show) => new(
        Guid.NewGuid(), "lib", folder, $"{folder}/{fileName}", kind, ItemExtraOrigins.Rule,
        Path.GetFileNameWithoutExtension(fileName), null, 0, 10, null, null, DateTimeOffset.UtcNow);
}
