using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MediaEngine.Api.Services.EditSession;
using MediaEngine.Api.Services.Matching;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Tuvima.Wikidata;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The Match tab's Wikidata side on the Engine (universal editor v5, 3c): a pasted link or QID resolves to its item; the Engine, not the Dashboard,
/// says whether an item is the item itself or its parent (the show for an episode) from the classes in <c>config/wikidata_match.json</c>; and a
/// parent is refused both when searched (Apply disabled) and when the link is saved.
/// </summary>
public sealed class EditorWikidataMatchServiceTests
{
    private const string ShowQid = "Q900";
    private const string EpisodeQid = "Q777";
    private const string ShowClass = "Q5398426";
    private const string EpisodeClass = "Q21191270";

    private static readonly Guid ItemId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid WorkId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    // ----- the rules ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Q123", "Q123")]
    [InlineData("q123", "Q123")]
    [InlineData("  Q123  ", "Q123")]
    [InlineData("https://www.wikidata.org/wiki/Q123", "Q123")]
    [InlineData("https://m.wikidata.org/wiki/Q123", "Q123")]
    [InlineData("https://www.wikidata.org/entity/Q123", "Q123")]
    public void ParseQid_ReadsALinkOrAnItemNumber(string pasted, string expected) =>
        Assert.Equal(expected, EditorWikidataMatchService.ParseQid(pasted));

    [Theory]
    [InlineData("")]
    [InlineData("Heat")]
    [InlineData("https://example.com/wiki/Q123")]
    [InlineData("P31")]
    public void ParseQid_ReadsNothingFromWhatIsNotAWikidataItem(string pasted) =>
        Assert.Null(EditorWikidataMatchService.ParseQid(pasted));

    [Fact]
    public void ForAnEpisode_AShowClassIsTheParent_AndAnEpisodeClassIsTheItemItself()
    {
        var rules = WikidataMatchConfigurationProvider.BuiltIn;

        Assert.Equal(EditorWikidataRelations.Parent, EditorWikidataMatchService.RelationOf("tv_episode", [ShowClass], rules));
        Assert.Equal(EditorWikidataRelations.Direct, EditorWikidataMatchService.RelationOf("tv_episode", [EpisodeClass], rules));
        Assert.Equal(EditorWikidataRelations.Other, EditorWikidataMatchService.RelationOf("tv_episode", ["Q571"], rules));
        Assert.Equal(EditorWikidataRelations.Other, EditorWikidataMatchService.RelationOf(null, [ShowClass], rules));
    }

    [Fact]
    public void TheShippedRules_CapTheResults_AndNameTheItemItselfAndItsParentForEveryEditorKind()
    {
        var rules = WikidataMatchConfigurationProvider.BuiltIn;

        Assert.InRange(rules.SearchLimit, 1, 50);
        foreach (var kind in new[] { "tv_episode", "movie", "song", "book", "album", "tv_show" })
        {
            Assert.True(rules.Kinds.TryGetValue(kind, out var classes), kind);
            Assert.NotEmpty(classes!.Direct);
        }
    }

    // ----- search --------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APastedQid_ResolvesDirectlyToItsItem_WithoutAWordSearch()
    {
        using var http = new FakeWikidata();
        var service = Service(http, "tv_episode");

        var found = await service.SearchAsync(ItemId, new EditorWikidataSearchRequestDto { Paste = "https://www.wikidata.org/wiki/" + EpisodeQid });

        Assert.NotNull(found);
        Assert.True(found!.FromPaste);
        var result = Assert.Single(found.Results);
        Assert.Equal(EpisodeQid, result.Qid);
        Assert.Equal("Pilot", result.Label);
        Assert.Equal(EditorWikidataRelations.Direct, result.Relation);
        Assert.True(result.CanApply);
        Assert.DoesNotContain(http.Requests, request => request.Contains("list=search", StringComparison.Ordinal) || request.Contains("wbsearchentities", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ForAnEpisode_TheShowIsReturnedAsTheParent_AndCannotBeApplied()
    {
        using var http = new FakeWikidata();
        var service = Service(http, "tv_episode");

        var found = await service.SearchAsync(ItemId, new EditorWikidataSearchRequestDto { Paste = ShowQid });

        var result = Assert.Single(found!.Results);
        Assert.Equal(EditorWikidataRelations.Parent, result.Relation);
        Assert.False(result.CanApply);
        Assert.Equal(EditorWikidataMessageCodes.ParentNotAllowed, result.Reason);
        Assert.Equal("television series", result.Type);
    }

    [Fact]
    public async Task APastedThing_ThatIsNotAWikidataItem_GetsACode_AndNothingIsAskedOfWikidata()
    {
        using var http = new FakeWikidata();
        var service = Service(http, "tv_episode");

        var found = await service.SearchAsync(ItemId, new EditorWikidataSearchRequestDto { Paste = "https://example.com/x" });

        Assert.Empty(found!.Results);
        Assert.Equal(EditorWikidataMessageCodes.NotAnItem, found.MessageCode);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task NothingTypedAndNothingPasted_AsksNothingOfWikidata()
    {
        using var http = new FakeWikidata();
        var service = Service(http, "tv_episode");

        var found = await service.SearchAsync(ItemId, new EditorWikidataSearchRequestDto());

        Assert.Equal(EditorWikidataMessageCodes.EnterWords, found!.MessageCode);
        Assert.Empty(http.Requests);
    }

    // ----- saving the link: the Engine refuses a parent too -------------------------------------------------------------------

    [Fact]
    public async Task SavingTheLink_RefusesTheShowAsTheEpisodesIdentity_AndWritesNothing()
    {
        using var http = new FakeWikidata();
        var writer = new WriterSpy();
        var handler = Handler(http, "tv_episode", writer);
        var change = Link(ShowQid);

        Assert.Equal(EditSessionReasons.WikidataParentNotAllowed, await handler.CheckAsync(change));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.ApplyAsync(change));
        Assert.Empty(writer.Calls);
    }

    [Fact]
    public async Task SavingTheLink_OfTheEpisodeItself_WritesOnlyTheLink()
    {
        using var http = new FakeWikidata();
        var writer = new WriterSpy();
        var handler = Handler(http, "tv_episode", writer);
        var change = Link(EpisodeQid);

        Assert.Null(await handler.CheckAsync(change));
        await handler.ApplyAsync(change);

        Assert.Equal([$"set:{ItemId}:{EpisodeQid}"], writer.Calls);
    }

    [Fact]
    public async Task SavingALinkThatIsNotAQid_IsRefused()
    {
        using var http = new FakeWikidata();
        var handler = Handler(http, "tv_episode", new WriterSpy());

        Assert.Equal(EditSessionReasons.WikidataItemUnreadable, await handler.CheckAsync(Link("not a qid")));
    }

    [Fact]
    public async Task RemovingTheLink_ClearsOnlyTheLink_AndRefusesWhenThereIsNone()
    {
        using var http = new FakeWikidata();
        var writer = new WriterSpy { Stored = EpisodeQid };
        var remover = new WikidataRemovalEditSessionChangeHandler(Scopes(http, "tv_episode", writer));
        var change = new EditSessionChange { Id = "c1", Kind = EditSessionChangeKinds.WikidataRemoval, EntityId = ItemId };

        Assert.Null(await remover.CheckAsync(change));
        await remover.ApplyAsync(change);
        Assert.Equal([$"remove:{ItemId}"], writer.Calls);

        writer.Stored = null;
        Assert.Equal(EditSessionReasons.NoWikidataLinkToRemove, await remover.CheckAsync(change));
    }

    // ----- a link a person set is theirs ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WorkWikidataStatus.UserConfirmed, "retail", false, false)]
    [InlineData(WorkWikidataStatus.UserReplaced, "retail", false, false)]
    [InlineData(WorkWikidataStatus.Pending, WorkWikidataMatchSource.User, false, false)]
    [InlineData(WorkWikidataStatus.Pending, "retail", true, false)]
    [InlineData(WorkWikidataStatus.Pending, "retail", false, true)]
    public void AutomaticAlignment_OwnsOnlyWhatAPersonDidNotSet(string status, string source, bool locked, bool expected) =>
        Assert.Equal(expected, WorkWikidataStatus.IsAutomationOwned(status, source, locked));

    // ----- helpers ---------------------------------------------------------------------------------------------------------------

    private static EditSessionChange Link(string qid) => new() { Id = "c1", Kind = EditSessionChangeKinds.WikidataLink, EntityId = ItemId, TargetId = qid };

    private static WikidataLinkEditSessionChangeHandler Handler(FakeWikidata http, string kind, WriterSpy writer) =>
        new(Scopes(http, kind, writer));

    private static IServiceScopeFactory Scopes(FakeWikidata http, string kind, WriterSpy writer)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Service(http, kind));
        services.AddSingleton<IWikidataIdentityWriter>(writer);
        services.AddSingleton(Proxy<IExternalLookupGuard>((method, _) => method.Name == nameof(IExternalLookupGuard.BlocksEntityAsync) ? Task.FromResult(false) : null));
        services.AddLogging();
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static EditorWikidataMatchService Service(FakeWikidata http, string kind)
    {
        var reconciler = new WikidataReconciler(new HttpClient(http, disposeHandler: false), new WikidataReconcilerOptions { UserAgent = "Tuvima-Tests/1.0", MaxLag = 0 });
        var launches = Proxy<IMetadataEditorRepository>((method, _) => method.Name == nameof(IMetadataEditorRepository.ResolveEditorLaunchAsync)
            ? Task.FromResult<MetadataEditorLaunchContext?>(new MetadataEditorLaunchContext(ItemId, "work", WorkId, null, WorkId, "TV", "episode", null, null, null))
            : null);
        var canonicals = Proxy<ICanonicalValueRepository>((method, _) => method.Name == nameof(ICanonicalValueRepository.GetByEntityAsync)
            ? Task.FromResult<IReadOnlyList<CanonicalValue>>([new CanonicalValue { EntityId = WorkId, Key = MetadataFieldConstants.Title, Value = "Pilot", LastScoredAt = DateTimeOffset.UtcNow }])
            : null);
        var guard = Proxy<IExternalLookupGuard>((method, _) => method.Name == nameof(IExternalLookupGuard.BlocksEntityAsync) ? Task.FromResult(false) : null);
        return new EditorWikidataMatchService(
            reconciler.Reconcile, reconciler.Entities, new FixedKind(kind), launches, canonicals, guard,
            new WikidataMatchConfigurationProvider(() => null), NullLogger<EditorWikidataMatchService>.Instance);
    }

    private sealed class FixedKind(string kind) : IEditorMatchKindResolver
    {
        public Task<string?> ResolveAsync(Guid entityId, CancellationToken ct) => Task.FromResult<string?>(kind);
    }

    private sealed class WriterSpy : IWikidataIdentityWriter
    {
        public List<string> Calls { get; } = [];

        public string? Stored { get; set; }

        public Task<string?> ReadAsync(Guid entityId, CancellationToken ct) => Task.FromResult(Stored);

        public Task SetAsync(Guid entityId, string qid, CancellationToken ct)
        {
            Calls.Add($"set:{entityId}:{qid}");
            return Task.CompletedTask;
        }

        public Task RemoveAsync(Guid entityId, CancellationToken ct)
        {
            Calls.Add($"remove:{entityId}");
            return Task.CompletedTask;
        }
    }

    /// <summary>An interface answered by one function: it returns a task (or null for "not scripted", which gives the type's default).</summary>
    private static T Proxy<T>(Func<MethodInfo, object?[]?, object?> answer) where T : class
    {
        var proxy = DispatchProxy.Create<T, ScriptedProxy>();
        ((ScriptedProxy)(object)proxy).Answer = answer;
        return proxy;
    }

    private class ScriptedProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Answer { get; set; } = (_, _) => null;

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            return Answer(targetMethod, args) ?? throw new NotSupportedException(targetMethod.Name);
        }
    }

    /// <summary>Wikidata over a recording handler: the show is a television series, the episode a television episode; every request is kept.</summary>
    private sealed class FakeWikidata : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var ids = (query["ids"] ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries);
            var entities = new Dictionary<string, object>();
            foreach (var id in ids)
            {
                entities[id] = Entity(id);
            }

            var json = JsonSerializer.Serialize(new { entities, success = 1 });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        private static object Entity(string id)
        {
            var label = id switch
            {
                ShowQid => "The Wire",
                EpisodeQid => "Pilot",
                ShowClass => "television series",
                EpisodeClass => "television episode",
                _ => id,
            };
            var instanceOf = id switch
            {
                ShowQid => ShowClass,
                EpisodeQid => EpisodeClass,
                _ => null,
            };
            var claims = new Dictionary<string, object>();
            if (instanceOf is not null)
            {
                claims["P31"] = new object[]
                {
                    new
                    {
                        mainsnak = new { snaktype = "value", property = "P31", datatype = "wikibase-item", datavalue = new { value = new Dictionary<string, object> { ["entity-type"] = "item", ["id"] = instanceOf }, type = "wikibase-entityid" } },
                        type = "statement",
                        rank = "normal",
                    },
                };
            }

            return new
            {
                type = "item",
                id,
                labels = new Dictionary<string, object> { ["en"] = new { language = "en", value = label } },
                descriptions = new Dictionary<string, object>(),
                aliases = new Dictionary<string, object>(),
                claims,
            };
        }
    }
}
