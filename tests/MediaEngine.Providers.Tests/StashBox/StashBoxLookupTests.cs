using System.Net;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services.StashBox;

namespace MediaEngine.Providers.Tests.StashBox;

/// <summary>The Official Stash-box lookup, against recorded stash-box responses. Nothing here touches the network.</summary>
public sealed class StashBoxLookupTests : IDisposable
{
    private const string Key = "test-account-key-1234";
    private readonly StashBoxConfigFolder _config = new();
    private readonly RecordedStashBox _server = new();

    public void Dispose() => _config.Dispose();

    private async Task<StashBoxVideoLookup> ReadyAsync(bool fingerprintPermission = false)
    {
        await _config.Settings.SetKeyAsync(StashBoxSettings.DefaultServerId, Key);
        await _config.Settings.SetFingerprintPermissionAsync(fingerprintPermission);
        return _config.Lookup(_server);
    }

    private static AdultFingerprint[] SomeFingerprints() =>
        [new(AdultFingerprintAlgorithms.Oshash, "8e245d9679d31e12"), new(AdultFingerprintAlgorithms.Md5, "d41d8cd98f00b204e9800998ecf8427e")];

    [Fact]
    public async Task LookupAsync_ByTitle_ReturnsTheRecordedSceneAndItsCover()
    {
        _server.Respond = RecordedStashBox.Script(search: RecordedStashBox.Fixture("search-scene-match.json"));
        var lookup = await ReadyAsync();

        var info = await lookup.LookupAsync(new AdultLookupQuery("example scene one", [], null, null, 1800));

        Assert.NotNull(info);
        Assert.Equal(AdultMatchSignals.Title, info.MatchedBy);
        Assert.Equal("Example Scene One", info.Title);
        Assert.Equal(new DateOnly(2021, 3, 4), info.Date);
        Assert.Equal("ABC-123", info.ProductCode);
        Assert.Equal("Example Studio", info.Studio);
        Assert.Equal(["Performer One", "Performer Two"], info.Performers);
        Assert.Equal(["Tag One", "Tag Two"], info.Tags);
        Assert.Contains("second line", info.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("image/jpeg", info.CoverMimeType);
        Assert.NotNull(info.Cover);
    }

    [Fact]
    public async Task LookupAsync_SendsTheKeyInAnApiKeyHeaderToTheServerOnly_NeverInAnAddress()
    {
        _server.Respond = RecordedStashBox.Script(search: RecordedStashBox.Fixture("search-scene-match.json"));
        var lookup = await ReadyAsync();

        await lookup.LookupAsync(new AdultLookupQuery("Example Scene One", [], null, null, 1800));

        var graphql = _server.Requests.Where(r => r.Uri.AbsolutePath == "/graphql").ToList();
        Assert.NotEmpty(graphql);
        Assert.All(graphql, r => Assert.Equal(Key, r.ApiKey));
        Assert.All(_server.Requests, r => Assert.DoesNotContain(Key, r.Uri.OriginalString, StringComparison.Ordinal));
        Assert.All(_server.Requests, r => Assert.DoesNotContain(Key, r.Body, StringComparison.Ordinal));
        // The cover comes from the server's own site and carries no key.
        Assert.All(_server.Requests.Where(r => r.Uri.AbsolutePath.EndsWith("cover.jpg", StringComparison.Ordinal)), r => Assert.Null(r.ApiKey));
    }

    [Fact]
    public async Task LookupAsync_WithoutTheFingerprintPermission_NothingAboutTheFileLeaves()
    {
        _server.Respond = RecordedStashBox.Script(search: RecordedStashBox.Fixture("search-scene-match.json"));
        var lookup = await ReadyAsync(fingerprintPermission: false);

        await lookup.LookupAsync(new AdultLookupQuery("Example Scene One", SomeFingerprints()));

        Assert.NotEmpty(_server.Requests);
        Assert.All(_server.Requests, r =>
        {
            Assert.DoesNotContain("findScenesByFullFingerprints", r.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("8e245d9679d31e12", r.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("d41d8cd98f00b204e9800998ecf8427e", r.Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task LookupAsync_WithoutThePermissionAndWithoutATitle_SendsNothingAtAll()
    {
        var lookup = await ReadyAsync(fingerprintPermission: false);

        var info = await lookup.LookupAsync(new AdultLookupQuery(null, SomeFingerprints()));

        Assert.Null(info);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task LookupAsync_WithThePermission_MatchesByFingerprintAndSendsOnlyTheHashes()
    {
        _server.Respond = RecordedStashBox.Script(fingerprint: RecordedStashBox.Fixture("fingerprint-match.json"));
        var lookup = await ReadyAsync(fingerprintPermission: true);

        var info = await lookup.LookupAsync(new AdultLookupQuery("a different title", SomeFingerprints()));

        Assert.NotNull(info);
        Assert.Equal(AdultMatchSignals.Fingerprint, info.MatchedBy);
        Assert.Equal("Example Scene One (Fingerprint Record)", info.Title);
        var sent = _server.Requests.Single(r => r.Body.Contains("findScenesByFullFingerprints", StringComparison.Ordinal));
        Assert.Contains("8e245d9679d31e12", sent.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("a different title", sent.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LookupAsync_ATitleThatOnlyPartlyMatches_IsNotApplied()
    {
        _server.Respond = RecordedStashBox.Script(search: RecordedStashBox.Fixture("search-scene-match.json"));
        var lookup = await ReadyAsync();

        Assert.Null(await lookup.LookupAsync(new AdultLookupQuery("Example Scene", [])));
    }

    [Fact]
    public async Task LookupAsync_AWrongKey_ShowsNotConnectedAndTheLibraryBehavesAsNone()
    {
        _server.Respond = _ => RecordedStashBox.Json("{}", HttpStatusCode.Unauthorized);
        var lookup = await ReadyAsync();

        Assert.Null(await lookup.LookupAsync(new AdultLookupQuery("Example Scene One", [])));

        var server = Assert.Single((await _config.Settings.GetAsync()).Servers);
        Assert.Equal(StashBoxConnectionStates.NotConnected, server.Connection);

        // Once marked not connected, no further request is made until a new key is saved.
        var before = _server.Requests.Count;
        Assert.Null(await lookup.LookupAsync(new AdultLookupQuery("Example Scene One", [])));
        Assert.Equal(before, _server.Requests.Count);
    }

    [Fact]
    public async Task LookupAsync_AnUnauthorisedGraphqlAnswer_AlsoShowsNotConnected()
    {
        _server.Respond = _ => RecordedStashBox.Json(RecordedStashBox.Fixture("me-unauthorised.json"));
        var lookup = await ReadyAsync();

        Assert.Null(await lookup.LookupAsync(new AdultLookupQuery("Example Scene One", [])));
        Assert.Equal(StashBoxConnectionStates.NotConnected, Assert.Single((await _config.Settings.GetAsync()).Servers).Connection);
    }

    [Fact]
    public async Task LookupAsync_WithNoKey_AsksNobody()
    {
        var lookup = _config.Lookup(_server);

        Assert.Null(await lookup.LookupAsync(new AdultLookupQuery("Example Scene One", [])));
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public async Task LookupAsync_ARedirect_IsNeverFollowedAndFindsNothing()
    {
        _server.Respond = _ =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://evil.example.net/graphql");
            return redirect;
        };
        var lookup = await ReadyAsync();

        Assert.Null(await lookup.LookupAsync(new AdultLookupQuery("Example Scene One", [])));
        Assert.DoesNotContain(_server.Requests, r => r.Uri.Host == "evil.example.net");
    }

    [Fact]
    public async Task LookupAsync_AnOversizedAnswer_IsRefused()
    {
        _server.Respond = _ => RecordedStashBox.Json("{\"data\":{\"searchScene\":[],\"pad\":\"" + new string('x', (int)StashBoxClient.MaxReplyBytes + 10) + "\"}}");
        var lookup = await ReadyAsync();

        Assert.Null(await lookup.LookupAsync(new AdultLookupQuery("Example Scene One", [])));
    }

    [Fact]
    public async Task LookupAsync_ACoverFromAnotherHost_IsRefusedButTheTextStillFills()
    {
        var json = RecordedStashBox.Fixture("search-scene-match.json").Replace("cdn.stashdb.org", "evil.example.net", StringComparison.Ordinal);
        _server.Respond = RecordedStashBox.Script(search: json);
        var lookup = await ReadyAsync();

        var info = await lookup.LookupAsync(new AdultLookupQuery("Example Scene One", [], null, null, 1800));

        Assert.NotNull(info);
        Assert.Null(info.Cover);
        Assert.DoesNotContain(_server.Requests, r => r.Uri.Host == "evil.example.net");
    }

    [Fact]
    public async Task LookupAsync_ACoverThatIsNotAnImage_IsRefused()
    {
        _server.Respond = seen => seen.Uri.AbsolutePath.EndsWith("cover.jpg", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) }
            : RecordedStashBox.Script(search: RecordedStashBox.Fixture("search-scene-match.json"))(seen);
        var lookup = await ReadyAsync();

        var info = await lookup.LookupAsync(new AdultLookupQuery("Example Scene One", [], null, null, 1800));

        Assert.NotNull(info);
        Assert.Null(info.Cover);
    }

    [Fact]
    public async Task SearchByTitleAsync_CleansAndBoundsWhatTheServerSays()
    {
        _server.Respond = RecordedStashBox.Script(search: RecordedStashBox.Fixture("search-hostile-fields.json"));
        var client = _config.Client(_server);

        var reply = await client.SearchByTitleAsync(new Uri("https://stashdb.org/graphql"), Key, "anything");

        // The record whose id is not a plain identifier is dropped.
        var scene = Assert.Single(reply.Scenes);
        Assert.Equal("HostileTitle With Controls", scene.Title);
        Assert.DoesNotContain("\0", scene.Details!, StringComparison.Ordinal);
        Assert.Contains("\n", scene.Details!, StringComparison.Ordinal);
        Assert.Null(scene.Date);
        Assert.Equal(64, scene.Code!.Length);
        Assert.Equal(["tag"], scene.Tags);
        Assert.Equal(["Someone"], scene.Performers);
    }

    [Fact]
    public async Task TestAsync_ARightKeyIsConnected_AWrongOneIsNot()
    {
        var client = _config.Client(_server);
        _server.Respond = RecordedStashBox.Script(me: RecordedStashBox.Fixture("me-ok.json"));
        Assert.True((await client.TestAsync(new Uri("https://stashdb.org/graphql"), Key)).Connected);

        _server.Respond = RecordedStashBox.Script(me: RecordedStashBox.Fixture("me-unauthorised.json"));
        var wrong = await client.TestAsync(new Uri("https://stashdb.org/graphql"), Key);
        Assert.False(wrong.Connected);
        Assert.Equal(StashBoxConnectionStates.NotConnected, wrong.Status);
        Assert.DoesNotContain(Key, wrong.Message, StringComparison.Ordinal);

        _server.Respond = _ => throw new HttpRequestException("no route");
        Assert.Equal("unavailable", (await client.TestAsync(new Uri("https://stashdb.org/graphql"), Key)).Status);
    }

    [Fact]
    public async Task LookupAsync_TriesTheLibrarysServerFirstThenTheOthersInTheirSavedOrder()
    {
        await _config.Settings.SetKeyAsync(StashBoxSettings.DefaultServerId, Key);
        var second = await _config.Settings.AddServerAsync("Second", "https://second.example.org");
        var third = await _config.Settings.AddServerAsync("Third", "https://third.example.org");
        await _config.Settings.SetKeyAsync(second!.Id, "second-account-key-1");
        await _config.Settings.SetKeyAsync(third!.Id, "third-account-key-1");
        await _config.Settings.SetOrderAsync([StashBoxSettings.DefaultServerId, second.Id, third.Id]);
        _server.Respond = RecordedStashBox.Script();

        await _config.Lookup(_server).LookupAsync(new AdultLookupQuery("Nothing Known", [], third.Id));

        Assert.Equal(["third.example.org", "stashdb.org", "second.example.org"], _server.Requests.Select(r => r.Uri.Host));
    }
}
