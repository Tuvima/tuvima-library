using Dapper;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Extras;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// What the details page's Extras row carries (universal editor v5 WP11, A8): groups and the cards-per-group number, the default grouping by kind of
/// file for a title with many files and no groups, the lesson a file is shown alongside, and an episode's own page listing the files that belong to it
/// and the files shown alongside it (D31).
/// </summary>
public sealed class DetailExtrasRowTests : IDisposable
{
    private readonly SupportingFileTestWorld _world = new();
    private readonly MediaAssetRepository _assets;
    private readonly DetailExtrasService _service;

    public DetailExtrasRowTests()
    {
        _assets = new MediaAssetRepository(_world.Database);
        _service = new DetailExtrasService(_assets, _world.Extras, _world.Folders, editorConfig: new EditorConfigurationProvider(() => null));
    }

    public void Dispose() => _world.Dispose();

    [Fact]
    public async Task ATitlesPage_CarriesTheCardsPerGroupNumberFromTheEditorConfiguration()
    {
        var pilot = Seed(Path.Combine(_world.SeasonFolder(1), "The Last Signal S01E01.mkv"));
        await _world.SeedDiskFileAsync("guide.pdf", "Guide");
        var page = new DetailPageViewModel { EntityType = DetailEntityType.TvShow };

        await _service.ApplyAsync(page, [pilot], CancellationToken.None);

        Assert.Single(page.Extras);
        Assert.Equal(EditorConfigurationProvider.BuiltIn.CardsPerGroup, page.ExtrasCardsPerGroup);
        Assert.True(page.ExtrasCardsPerGroup > 0);
    }

    [Fact]
    public void ATitleWithManyFilesAndNoGroups_FallsIntoOneGroupPerKindOfFile()
    {
        var files = Enumerable.Range(0, 5).Select(number => _world.Row(Path.Combine(_world.Show, $"guide-{number}.pdf"), ItemExtraKinds.Document, $"Guide {number}", ExtraScope.Root))
            .Concat(Enumerable.Range(0, 3).Select(number => _world.Row(Path.Combine(_world.Show, $"scene-{number}.mp4"), ItemExtraKinds.Clip, $"Scene {number}", ExtraScope.Root)))
            .ToList();

        var cards = ExtraCardBuilder.Build(files, _ => true, groupByFamilyAbove: 6);

        Assert.Equal(["Documents", "Videos"], cards.Select(card => card.Group).Distinct());
        Assert.Equal(5, cards.Count(card => card.Group == "Documents"));
    }

    [Fact]
    public void ATitleWithAHandfulOfFiles_StaysUngrouped()
    {
        var files = Enumerable.Range(0, 4).Select(number => _world.Row(Path.Combine(_world.Show, $"guide-{number}.pdf"), ItemExtraKinds.Document, $"Guide {number}", ExtraScope.Root)).ToList();

        var cards = ExtraCardBuilder.Build(files, _ => true, groupByFamilyAbove: 12);

        Assert.All(cards, card => Assert.Null(card.Group));
    }

    [Fact]
    public void ATitleWhoseFilesAreGrouped_KeepsTheGroupsAPersonNamed()
    {
        var named = _world.Row(Path.Combine(_world.Show, "a.pdf"), ItemExtraKinds.Document, "A", ExtraScope.Root) with { GroupName = "Homework", GroupSource = ExtraGroupSources.You };
        var loose = Enumerable.Range(0, 8).Select(number => _world.Row(Path.Combine(_world.Show, $"b{number}.pdf"), ItemExtraKinds.Document, $"B{number}", ExtraScope.Root));

        var cards = ExtraCardBuilder.Build([named, .. loose], _ => true, groupByFamilyAbove: 6);

        Assert.Equal("Homework", Assert.Single(cards, card => card.Group is not null).Group);
    }

    [Fact]
    public async Task AFileShownAlongsideALesson_CarriesTheLessonsAssetAndTheWordsForIt()
    {
        var lesson = Seed(Path.Combine(_world.Show, "Section 1", "02 Passing parameters.mkv"));
        Seed(Path.Combine(_world.Show, "Section 1", "03 Component events.mkv"));
        var worksheet = _world.Row(Path.Combine(_world.Show, "Section 1", "02 exercises.pdf"), ItemExtraKinds.Document, "Component exercises", ExtraScope.ForFolder("Section 1"))
            with
        { InferredAlongside = ExtraScope.ForFile("Section 1/02 Passing parameters.mkv") };
        await _world.Extras.SyncItemFolderAsync(_world.Show, [worksheet], []);
        var page = new DetailPageViewModel { EntityType = DetailEntityType.TvShow };

        await _service.ApplyAsync(page, [lesson], CancellationToken.None);

        var card = Assert.Single(page.Extras);
        Assert.Equal(lesson.ToString("D"), card.AlongsideAssetId);
        Assert.Equal("file:Section 1/02 Passing parameters.mkv", card.AlongsideKey);
        Assert.Equal("02 Passing parameters", card.AlongsideLabel);
        Assert.Equal("PDF", card.FileBadge);
    }

    [Fact]
    public async Task AnEpisodesOwnPage_ListsTheFilesThatBelongToItAndTheOnesShownAlongsideIt_AndNoOthers()
    {
        var episode = Seed(Path.Combine(_world.SeasonFolder(2), "The Last Signal S02E03.mkv"));
        var own = _world.Row(Path.Combine(_world.SeasonFolder(2), "S02E03 notes.pdf"), ItemExtraKinds.Document, "Episode notes", ExtraScope.ForEpisode(2, 3));
        var alongside = _world.Row(Path.Combine(_world.SeasonFolder(2), "Character guide.pdf"), ItemExtraKinds.Document, "Character guide", ExtraScope.ForSeason(2))
            with
        { InferredAlongside = ExtraScope.ForEpisode(2, 3) };
        var otherEpisode = _world.Row(Path.Combine(_world.SeasonFolder(2), "Other guide.pdf"), ItemExtraKinds.Document, "Other guide", ExtraScope.ForSeason(2))
            with
        { InferredAlongside = ExtraScope.ForEpisode(2, 4) };
        var wholeShow = _world.Row(Path.Combine(_world.Show, "Series bible.pdf"), ItemExtraKinds.Document, "Series bible", ExtraScope.Root);
        await _world.Extras.SyncItemFolderAsync(_world.Show, [own, alongside, otherEpisode, wholeShow], []);
        var page = new DetailPageViewModel
        {
            EntityType = DetailEntityType.TvEpisode,
            Facts = new DetailFactsViewModel { SeasonNumber = "2", EpisodeNumber = "3" },
        };

        await _service.ApplyAsync(page, [episode], CancellationToken.None);

        Assert.Equal(["Character guide", "Episode notes"], page.Extras.Select(card => card.Title).Order());
    }

    [Fact]
    public async Task ALessonsOwnPage_ListsTheFileShownAlongsideItByPath()
    {
        var lesson = Seed(Path.Combine(_world.Show, "Section 1", "02 Passing parameters.mkv"));
        var other = Seed(Path.Combine(_world.Show, "Section 1", "03 Component events.mkv"));
        var worksheet = _world.Row(Path.Combine(_world.Show, "Section 1", "02 exercises.pdf"), ItemExtraKinds.Document, "Component exercises", ExtraScope.ForFolder("Section 1"))
            with
        { InferredAlongside = ExtraScope.ForFile("Section 1/02 Passing parameters.mkv") };
        await _world.Extras.SyncItemFolderAsync(_world.Show, [worksheet], []);

        var lessonPage = new DetailPageViewModel { EntityType = DetailEntityType.TvEpisode };
        var otherPage = new DetailPageViewModel { EntityType = DetailEntityType.TvEpisode };
        await _service.ApplyAsync(lessonPage, [lesson], CancellationToken.None);
        await _service.ApplyAsync(otherPage, [other], CancellationToken.None);

        Assert.Equal("Component exercises", Assert.Single(lessonPage.Extras).Title);
        Assert.Empty(otherPage.Extras);
    }

    [Fact]
    public async Task ATitlesPage_WithMoreFilesThanTheCapAndNoNamedGroups_IsGroupedByKindOfFile()
    {
        var pilot = Seed(Path.Combine(_world.SeasonFolder(1), "The Last Signal S01E01.mkv"));
        var rows = Enumerable.Range(0, 8).Select(number => _world.Row(Path.Combine(_world.Show, $"guide-{number}.pdf"), ItemExtraKinds.Document, $"Guide {number}", ExtraScope.Root))
            .Concat(Enumerable.Range(0, 6).Select(number => _world.Row(Path.Combine(_world.Show, $"scene-{number}.mp4"), ItemExtraKinds.Clip, $"Scene {number}", ExtraScope.Root)))
            .ToList();
        await _world.Extras.SyncItemFolderAsync(_world.Show, rows, []);
        var page = new DetailPageViewModel { EntityType = DetailEntityType.TvShow };

        await _service.ApplyAsync(page, [pilot], CancellationToken.None);

        Assert.True(page.Extras.Count > page.ExtrasCardsPerGroup);
        Assert.Equal(["Documents", "Videos"], page.Extras.Select(card => card.Group).Distinct().Order());
        Assert.Equal(8, page.Extras.Count(card => card.Group == "Documents"));
    }

    [Fact]
    public async Task AnEpisodesPage_SendsNoCap_BecauseItHasNoSeeAll()
    {
        var episode = Seed(Path.Combine(_world.SeasonFolder(2), "The Last Signal S02E03.mkv"));
        var rows = Enumerable.Range(0, 15).Select(number => _world.Row(Path.Combine(_world.SeasonFolder(2), $"notes-{number}.pdf"), ItemExtraKinds.Document, $"Notes {number}", ExtraScope.ForEpisode(2, 3))).ToList();
        await _world.Extras.SyncItemFolderAsync(_world.Show, rows, []);
        var page = new DetailPageViewModel
        {
            EntityType = DetailEntityType.TvEpisode,
            Facts = new DetailFactsViewModel { SeasonNumber = "2", EpisodeNumber = "3" },
        };

        await _service.ApplyAsync(page, [episode], CancellationToken.None);

        Assert.Equal(15, page.Extras.Count);
        Assert.Equal(0, page.ExtrasCardsPerGroup);
    }

    [Fact]
    public async Task AFileThatBelongsToALesson_CarriesTheLessonsAssetAsItsScopeAsset()
    {
        var lesson = Seed(Path.Combine(_world.Show, "Section 1", "02 Passing parameters.mkv"));
        var notes = _world.Row(Path.Combine(_world.Show, "Section 1", "02 notes.pdf"), ItemExtraKinds.Document, "Lesson notes", ExtraScope.ForFile("Section 1/02 Passing parameters.mkv"));
        await _world.Extras.SyncItemFolderAsync(_world.Show, [notes], []);
        var page = new DetailPageViewModel { EntityType = DetailEntityType.TvShow };

        await _service.ApplyAsync(page, [lesson], CancellationToken.None);

        var card = Assert.Single(page.Extras);
        Assert.Equal(lesson.ToString("D"), card.ScopeAssetId);
        Assert.Null(card.AlongsideAssetId);
    }

    [Fact]
    public async Task AFortyLessonCourse_GivesEveryLessonItsFile()
    {
        var lessons = new List<Guid>();
        var rows = new List<ItemExtra>();
        for (var number = 1; number <= 40; number++)
        {
            var name = $"{number:00} Lesson.mkv";
            lessons.Add(Seed(Path.Combine(_world.Show, $"Section {number}", name)));
            rows.Add(_world.Row(Path.Combine(_world.Show, $"Section {number}", $"{number:00} worksheet.pdf"), ItemExtraKinds.Document, $"Worksheet {number}", ExtraScope.ForFolder($"Section {number}"))
                with
            { InferredAlongside = ExtraScope.ForFile($"Section {number}/{name}") });
        }

        await _world.Extras.SyncItemFolderAsync(_world.Show, rows, []);
        var page = new DetailPageViewModel { EntityType = DetailEntityType.TvShow };

        await _service.ApplyAsync(page, lessons, CancellationToken.None);

        Assert.Equal(40, page.Extras.Count);
        Assert.Equal(lessons.Select(id => id.ToString("D")).Order(), page.Extras.Select(card => card.AlongsideAssetId!).Order());
    }

    // A work with one media file at the path, in the test data store; returns the file's asset id.
    private Guid Seed(string filePath)
    {
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        using var connection = _world.Database.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type) VALUES (@work, 'TV')", new { work = GuidSql.ToBlob(workId) });
        var edition = GuidSql.ToBlob(Guid.NewGuid());
        connection.Execute("INSERT INTO editions (id, work_id) VALUES (@edition, @work)", new { edition, work = GuidSql.ToBlob(workId) });
        connection.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@asset, @edition, @hash, @path)",
            new { asset = GuidSql.ToBlob(assetId), edition, hash = $"hash-{assetId:N}", path = filePath });
        return assetId;
    }
}
