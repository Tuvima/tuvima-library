using MediaEngine.Api.Services.Plugins;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A plugin's own records: stored in the Library's data store, private to the plugin, kept across a restart, bounded by
/// limits that stop the write with a plain message and show on the plugin's card.
/// </summary>
public sealed class PluginRecordStoreTests : IDisposable
{
    private const string OtherPluginId = "test.other-plugin";

    private readonly HostServicesHarness _host;
    private readonly string _pluginId = HostServicesTestPlugin.DefaultId;

    public PluginRecordStoreTests()
    {
        _host = new HostServicesHarness(
            PluginHostLimits.Default with { RecordValueMaxBytes = 600, RecordsPerPlugin = 4, RecordBytesPerPlugin = 1024 },
            new HostServicesTestPlugin(),
            new HostServicesTestPlugin(OtherPluginId, name: "Other plugin"));
        _host.Enable();
        _host.Enable(OtherPluginId);
    }

    [Fact]
    public async Task PutThenGet_ReturnsTheRecordTheCollectionAndTheTimeItWasSaved()
    {
        var store = _host.RecordStoreFor(_pluginId);

        await store.PutAsync("watched", "dune", """{"seen":true}""");
        var record = await store.GetAsync("watched", "dune");

        Assert.NotNull(record);
        Assert.Equal("watched", record.Collection);
        Assert.Equal("dune", record.Key);
        Assert.Equal("""{"seen":true}""", record.Json);
        Assert.Equal(_host.Clock.GetUtcNow(), record.UpdatedAt);
        Assert.Null(await store.GetAsync("watched", "missing"));
    }

    [Fact]
    public async Task Put_ReplacesARecordWithTheSameKey_AndDeleteRemovesIt()
    {
        var store = _host.RecordStoreFor(_pluginId);
        await store.PutAsync("c", "k", """{"v":1}""");
        await store.PutAsync("c", "k", """{"v":2}""");

        Assert.Equal("""{"v":2}""", (await store.GetAsync("c", "k"))!.Json);
        Assert.Single(await store.ListAsync("c"));
        Assert.True(await store.DeleteAsync("c", "k"));
        Assert.False(await store.DeleteAsync("c", "k"));
        Assert.Null(await store.GetAsync("c", "k"));
    }

    [Fact]
    public async Task List_IsOrderedByKey_FiltersByPrefixLiterally_AndHonoursTheLimit()
    {
        var store = _host.RecordStoreFor(_pluginId);
        await store.PutAsync("c", "b-2", "{}");
        await store.PutAsync("c", "a-1", "{}");
        await store.PutAsync("c", "b%1", "{}");
        await store.PutAsync("other", "b-3", "{}");

        Assert.Equal(["a-1", "b%1", "b-2"], (await store.ListAsync("c")).Select(record => record.Key));
        Assert.Equal(["b%1", "b-2"], (await store.ListAsync("c", "b")).Select(record => record.Key));
        Assert.Equal(["b%1"], (await store.ListAsync("c", "b%")).Select(record => record.Key));
        Assert.Equal(["a-1"], (await store.ListAsync("c", limit: 1)).Select(record => record.Key));
    }

    [Fact]
    public async Task Records_SurviveARestart_BecauseTheyLiveInTheDataStore()
    {
        await _host.RecordStoreFor(_pluginId).PutAsync("watched", "dune", """{"seen":true}""");

        _host.Restart();
        _host.Enable();

        var record = await _host.RecordStoreFor(_pluginId).GetAsync("watched", "dune");
        Assert.Equal("""{"seen":true}""", record!.Json);
    }

    [Fact]
    public async Task OnePluginCannotSeeAnotherPluginsRecords()
    {
        await _host.RecordStoreFor(_pluginId).PutAsync("watched", "dune", """{"mine":true}""");
        var other = _host.RecordStoreFor(OtherPluginId);

        Assert.Null(await other.GetAsync("watched", "dune"));
        Assert.Empty(await other.ListAsync("watched"));
        Assert.False(await other.DeleteAsync("watched", "dune"));
        Assert.NotNull(await _host.RecordStoreFor(_pluginId).GetAsync("watched", "dune"));
    }

    [Fact]
    public void TheStoreHasNoWayToNameAnotherPlugin()
    {
        Assert.All(typeof(IPluginRecordStore).GetMethods(), method =>
            Assert.DoesNotContain(method.GetParameters(), parameter => parameter.Name == "pluginId"));
    }

    [Fact]
    public async Task APluginWithoutThePermission_OrSwitchedOff_CannotUseTheStore()
    {
        using var host = new HostServicesHarness(
            plugins: [new HostServicesTestPlugin(permissions: [PluginPermissionIds.ScheduledWork])]);
        host.Enable();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            host.RecordStoreFor(HostServicesTestPlugin.DefaultId).PutAsync("c", "k", "{}"));

        var store = _host.RecordStoreFor(_pluginId);
        await store.PutAsync("c", "k", "{}");
        _host.Disable();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => store.GetAsync("c", "k"));
    }

    [Theory]
    [InlineData("", "key")]
    [InlineData("collection", "")]
    [InlineData("coll\u0001ection", "key")]
    public async Task ABadCollectionOrKey_IsRefused(string collection, string key)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.RecordStoreFor(_pluginId).PutAsync(collection, key, "{}"));
    }

    [Fact]
    public async Task ANameLongerThan128Characters_AndAValueThatIsNotJson_AreRefused()
    {
        var store = _host.RecordStoreFor(_pluginId);

        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(new string('a', 129), "k", "{}"));
        await store.PutAsync(new string('a', 128), "k", "{}");
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync("c", "k", "not json"));
    }

    [Fact]
    public async Task AValueOverTheLimit_ThrowsWithAPlainMessage_AndTheCardShowsNeedsAttention()
    {
        var store = _host.RecordStoreFor(_pluginId);
        var tooBig = "\"" + new string('x', 700) + "\"";

        var error = await Assert.ThrowsAsync<PluginLimitExceededException>(() => store.PutAsync("c", "k", tooBig));

        Assert.Equal("A record can be at most 600 bytes.", error.Message);
        Assert.Null(await store.GetAsync("c", "k"));
        var host = _host.Statuses.Get(_pluginId).Describe();
        Assert.Equal("needs_attention", host.State);
        Assert.Equal("A record was larger than 600 bytes", host.Problem);
    }

    [Fact]
    public async Task TooManyRecords_ThrowsAndSetsTheStatus_ButReplacingARecordStillWorks()
    {
        var store = _host.RecordStoreFor(_pluginId);
        for (var i = 0; i < 4; i++)
        {
            await store.PutAsync("c", $"k{i}", "{}");
        }

        var error = await Assert.ThrowsAsync<PluginLimitExceededException>(() => store.PutAsync("c", "k4", "{}"));

        Assert.Equal("A plugin can keep at most 4 records.", error.Message);
        Assert.Equal("Reached its limit of 4 records", _host.Statuses.Get(_pluginId).Describe().Problem);
        await store.PutAsync("c", "k0", """{"replaced":true}""");
        Assert.Equal(4, (await store.ListAsync("c")).Count);
        Assert.Equal("working", _host.Statuses.Get(_pluginId).Describe().State);
    }

    [Fact]
    public async Task TheTotalSpaceLimit_StopsTheWrite_RecordsTheStorageLimitStatus_AndFreeingSpaceLetsWritesThroughAgain()
    {
        var store = _host.RecordStoreFor(_pluginId);
        var half = "\"" + new string('x', 498) + "\""; // 500 bytes
        await store.PutAsync("c", "a", half);
        await store.PutAsync("c", "b", half);

        var error = await Assert.ThrowsAsync<PluginLimitExceededException>(() => store.PutAsync("c", "c", half));

        Assert.Equal("A plugin can keep at most 1 KB of records.", error.Message);
        var status = _host.Statuses.Get(_pluginId).Describe();
        Assert.Equal("needs_attention", status.State);
        Assert.Equal("Reached its storage limit (1 KB)", status.Problem);

        Assert.True(await store.DeleteAsync("c", "b"));
        await store.PutAsync("c", "c", half);
        Assert.Equal("working", _host.Statuses.Get(_pluginId).Describe().State);
    }

    [Fact]
    public void WithTheDefaultLimits_TheStorageLimitMessageNamesSixteenMegabytes()
    {
        Assert.Equal("Reached its storage limit (16 MB)", PluginHostMessages.StorageFull(PluginHostLimits.Default));
        Assert.Equal(64 * 1024, PluginHostLimits.Default.RecordValueMaxBytes);
        Assert.Equal(10_000, PluginHostLimits.Default.RecordsPerPlugin);
    }

    [Fact]
    public async Task TheRecordUsage_ShownOnTheCard_CountsRecordsAndBytes()
    {
        var store = _host.RecordStoreFor(_pluginId);
        await store.PutAsync("c", "a", "{}");
        await store.PutAsync("c", "b", "[1,2]");
        var reader = new PluginHostServicesReader(_host.Statuses, _host.Records);

        var description = await reader.DescribeAsync(_host.Catalog.Get(_pluginId)!, CancellationToken.None);

        Assert.Equal(2, description.RecordsUsed);
        Assert.Equal(7, description.RecordBytesUsed);
    }

    public void Dispose() => _host.Dispose();
}
