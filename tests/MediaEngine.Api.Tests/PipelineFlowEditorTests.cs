using MediaEngine.Api.Services.Settings;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;
using MediaTypePipeline = MediaEngine.Domain.Configuration.MediaTypePipeline;
using PipelineProviderEntry = MediaEngine.Domain.Configuration.PipelineProviderEntry;

namespace MediaEngine.Api.Tests;

/// <summary>The editable Ingestion Flow: order, role and Off per media type, saved without touching scoring or strategy.</summary>
public sealed class PipelineFlowEditorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tuvima-flow", Guid.NewGuid().ToString("N"));
    private readonly ConfigurationDirectoryLoader _loader;
    private readonly PipelineFlowEditor _editor;
    private readonly int _coverBefore;

    public PipelineFlowEditorTests()
    {
        _loader = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        Provider("musicbrainz", "MusicBrainz", "Music");
        Provider("apple_api", "Apple API", "Music", "Books");
        Provider("newcomer", "Newcomer", "Music");
        Provider("tmdb", "TMDB", "Movies");
        // Start from the shipped flow so the schema (scoring scopes) is satisfied.
        var shipped = Path.Combine(FindRepoRoot(), "config", "pipelines.json");
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        File.Copy(shipped, Path.Combine(_root, "config", "pipelines.json"));
        var pipelines = _loader.LoadPipelines();
        _coverBefore = pipelines.Pipelines["Music"].FieldPriorities.Count;
        _editor = new PipelineFlowEditor(_loader);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder another process still holds is left for the OS to clean up.
        }
    }

    [Fact]
    public void ReorderingAndAddingAProvider_SavesTheNewOrder_AndKeepsScoringAndFieldPriorities()
    {
        var result = _editor.Save(Edit("Music", ("newcomer", "fallback"), ("musicbrainz", "primary"), ("apple_api", "fallback")));

        Assert.True(result.Saved);
        var music = _loader.LoadPipelines().Pipelines["Music"];
        Assert.Equal(["newcomer", "musicbrainz", "apple_api"], music.Providers.OrderBy(p => p.Rank).Select(p => p.Name));
        Assert.Equal(ProviderStrategy.Sequential, music.Strategy);
        Assert.Equal(_coverBefore, music.FieldPriorities.Count);
        Assert.NotEmpty(music.Scoring.Scopes);
        var apple = music.Providers.Single(p => p.Name == "apple_api");
        Assert.Equal(["apple-album-manifest"], apple.AcceptedActions);
    }

    [Fact]
    public void ChangingARole_StartsFromACleanEntry()
    {
        _editor.Save(Edit("Music", ("musicbrainz", "primary"), ("apple_api", "primary")));

        var apple = _loader.LoadPipelines().Pipelines["Music"].Providers.Single(p => p.Name == "apple_api");
        Assert.False(apple.UseAsIdentityFallback);
        Assert.False(apple.RequiresIdentity);
        Assert.Empty(apple.AcceptedActions);
        Assert.Equal("identity", apple.Purpose);
        Assert.Equal("identity", _loader.LoadPipelines().Pipelines["Music"].Providers.Single(p => p.Name == "musicbrainz").Purpose);
    }

    [Fact]
    public void TurningAProviderOff_RemovesItFromTheFlow()
    {
        var result = _editor.Save(Edit("Music", ("musicbrainz", "primary")));

        Assert.True(result.Saved);
        Assert.Equal(["musicbrainz"], _loader.LoadPipelines().Pipelines["Music"].Providers.Select(p => p.Name));
    }

    [Fact]
    public void RemovingTheLastIdentityProvider_IsBlockedWithAPlainSentence_AndNothingIsSaved()
    {
        var before = _loader.LoadPipelines().Pipelines["Movies"].Providers.Count;

        var result = _editor.Save(Edit("Movies"));

        Assert.False(result.Saved);
        Assert.Contains("needs at least one provider that identifies it", Assert.Single(result.Problems).Message);
        Assert.Equal(before, _loader.LoadPipelines().Pipelines["Movies"].Providers.Count);
    }

    [Fact]
    public void AFlowWithOnlyAnEnrichmentProvider_IsBlocked()
    {
        var result = _editor.Save(Edit("Music", ("apple_api", "enrichment")));

        Assert.False(result.Saved);
    }

    [Fact]
    public void AProviderThatDoesNotHandleTheMediaType_IsRefused_AndOneBadMediaTypeSavesNothing()
    {
        var request = Edit("Movies", ("tmdb", "primary"), ("musicbrainz", "fallback"));
        request.MediaTypes["Music"] = [new PipelineFlowProviderDto { Name = "newcomer", Role = "primary" }];

        var result = _editor.Save(request);

        Assert.False(result.Saved);
        Assert.Equal(["musicbrainz", "apple_api"], _loader.LoadPipelines().Pipelines["Music"].Providers.Select(p => p.Name));
    }

    [Fact]
    public void Options_ListsEachInstalledProviderWithWhereItIsUsed_SoANewOneShowsAsNotUsedYet()
    {
        var options = _editor.Options().Providers;

        var newcomer = options.Single(p => p.Name == "newcomer");
        Assert.Equal(["Music"], newcomer.MediaTypes);
        Assert.Empty(newcomer.UsedIn);
        Assert.Equal(["Music"], options.Single(p => p.Name == "musicbrainz").UsedIn);
    }

    [Fact]
    public void TheRefreshSchedule_NeverReadsTheIngestionFlow()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "Services", "EnrichmentRefreshScheduleService.cs"));

        Assert.DoesNotContain("LoadPipelines", source);
    }

    [Fact]
    public void TheShippedRefreshInterval_IsNinetyDays()
    {
        var hydration = new HydrationSettings();

        Assert.Equal(90, hydration.Stage3RefreshDays);
        Assert.Equal(90, hydration.PersonRefreshDays);
        Assert.Equal(90, hydration.SeriesManifestRefreshDays);
    }

    private static PipelineFlowSaveRequest Edit(string mediaType, params (string Name, string Role)[] rows)
    {
        var request = new PipelineFlowSaveRequest();
        request.MediaTypes[mediaType] = rows.Select(row => new PipelineFlowProviderDto { Name = row.Name, Role = row.Role }).ToList();
        return request;
    }

    private void Provider(string name, string display, params string[] media) => _loader.SaveProvider(new ProviderConfiguration
    {
        Name = name,
        DisplayName = display,
        AdapterType = "config_driven",
        Enabled = true,
        CanHandle = new CanHandleConfig { MediaTypes = [.. media] },
    });

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
