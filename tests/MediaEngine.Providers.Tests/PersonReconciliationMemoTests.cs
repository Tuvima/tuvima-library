using System.Net;
using System.Text;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;
using MediaEngine.Providers.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Tuvima.Wikidata;

namespace MediaEngine.Providers.Tests;

public sealed class PersonReconciliationMemoTests
{
    [Fact]
    public async Task UnmatchedPerson_NamedByManyTitles_IsSearchedOnce()
    {
        var handler = new CountingHandler();
        var reconciler = new WikidataReconciler(
            new HttpClient(handler, disposeHandler: false),
            new WikidataReconcilerOptions { UserAgent = "Tuvima-Tests/1.0", MaxLag = 0 });
        var service = new PersonReconciliationService(
            new CoreOnlyLoader(), NullLogger<PersonReconciliationService>.Instance, reconciler);

        Assert.Null(await service.SearchPersonAsync("Nobody Atall", "Composer", "Film One"));
        var afterFirst = handler.Requests;
        Assert.True(afterFirst > 0);

        for (var i = 2; i <= 20; i++)
        {
            Assert.Null(await service.SearchPersonAsync("Nobody Atall", "Composer", $"Film {i}"));
        }

        Assert.Equal(afterFirst, handler.Requests);
    }

    [Fact]
    public async Task Artist_WithMusicBrainzId_IsFoundByThatIdOnceAcrossSongs()
    {
        var handler = new CountingHandler("""{"success":1,"search":[],"query":{"search":[{"title":"Q1299"}]},"entities":{"Q1299":{"type":"item","id":"Q1299","labels":{"en":{"language":"en","value":"The Beatles"}},"claims":{}}}}""");
        var reconciler = new WikidataReconciler(
            new HttpClient(handler, disposeHandler: false),
            new WikidataReconcilerOptions { UserAgent = "Tuvima-Tests/1.0", MaxLag = 0 });
        var service = new PersonReconciliationService(
            new CoreOnlyLoader(), NullLogger<PersonReconciliationService>.Instance, reconciler);
        const string beatles = "b10bbbfc-cf9e-42e0-be17-e2c3e1d2600d";

        var first = await service.SearchPersonAsync("The Beatles", "Performer", "Help!", externalId: ExternalPersonId.MusicBrainzArtist(beatles));
        var afterFirst = handler.Requests;
        var second = await service.SearchPersonAsync("The Beatles", "Performer", "Yesterday", externalId: ExternalPersonId.MusicBrainzArtist(beatles));

        Assert.Equal("Q1299", first?.WikidataQid);
        Assert.Equal("Q1299", second?.WikidataQid);
        Assert.Equal(afterFirst, handler.Requests);
    }

    private sealed class CountingHandler(string? body = null) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body ?? """{"success":1,"search":[],"query":{"search":[]},"entities":{}}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CoreOnlyLoader : IConfigurationLoader
    {
        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public ScoringSettings LoadScoring() => new();
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public HydrationSettings LoadHydration() => new();
        public void SaveHydration(HydrationSettings settings) { }
        public PipelineConfiguration LoadPipelines() => new();
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public TranscodingSettings LoadTranscoding() => new();
        public void SaveTranscoding(TranscodingSettings settings) { }
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public LibrariesConfiguration LoadLibraries() => new();
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void DeleteProvider(string name) { }
        public void SaveProvider(ProviderConfiguration config) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }
}
