using Bunit;
using MediaEngine.Contracts.Paging;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class IngestionMediaDrawerTests : AsyncBunitContext
{
    public IngestionMediaDrawerTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void DrawerLoadsEveryPagedChildIntoOneScrollableList()
    {
        var offsets = new List<int>();
        Services.AddSingleton(EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetIngestionMediaChildrenAsync), args =>
            {
                var offset = (int)args![2]!;
                offsets.Add(offset);
                var children = offset == 0
                    ? Children(250, 1)
                    : Children(1, 251);
                return Task.FromResult<PagedResponse<IngestionMediaChildDto>?>(new(
                    children, offset, 250, offset == 0, 251));
            })));

        var cut = Render<IngestionMediaDrawer>(parameters => parameters.Add(component => component.Item, Group()));

        cut.WaitForAssertion(() => Assert.Equal(251, cut.FindAll(".ingestion-drawer__children li").Count));
        Assert.Equal([0, 250], offsets);
        Assert.Single(cut.FindAll(".ingestion-drawer__children ol"));
        Assert.Contains("Child 251", cut.Markup);
    }

    [Fact]
    public async Task DrawerDoesNotApplyAStaleSlowResponseAfterGroupChanges()
    {
        var first = Group("First");
        var second = Group("Second");
        var slowResponse = new TaskCompletionSource<PagedResponse<IngestionMediaChildDto>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRequested = false;
        Services.AddSingleton(EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetIngestionMediaChildrenAsync), args =>
            {
                var groupId = (Guid)args![1]!;
                if (groupId == first.GroupId)
                {
                    return slowResponse.Task;
                }

                secondRequested = true;
                return Task.FromResult<PagedResponse<IngestionMediaChildDto>?>(new(Children(1, 1, "Second child"), 0, 250, false, 1));
            })));

        var cut = Render<IngestionMediaDrawer>(parameters => parameters.Add(component => component.Item, first));
        cut.Render(parameters => parameters.Add(component => component.Item, second));
        cut.WaitForAssertion(() => Assert.True(secondRequested));
        cut.WaitForAssertion(() => Assert.Contains("Second child", cut.Markup));

        slowResponse.SetResult(new(Children(1, 1, "Stale child"), 0, 250, false, 1));
        await cut.InvokeAsync(() => Task.CompletedTask);

        Assert.DoesNotContain("Stale child", cut.Markup);
        Assert.Contains("Second child", cut.Markup);
    }

    [Fact]
    public void DrawerCancelsAnOutstandingChildRequestWhenDisposed()
    {
        CancellationToken requestToken = default;
        var response = new TaskCompletionSource<PagedResponse<IngestionMediaChildDto>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Services.AddSingleton(EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetIngestionMediaChildrenAsync), args =>
            {
                requestToken = (CancellationToken)args![4]!;
                return response.Task;
            })));

        var cut = Render<IngestionMediaDrawer>(parameters => parameters.Add(component => component.Item, Group()));
        cut.WaitForAssertion(() => Assert.True(requestToken.CanBeCanceled));

        cut.Instance.Dispose();
        response.SetResult(new(Children(1, 1), 0, 250, false, 1));

        Assert.True(requestToken.IsCancellationRequested);
    }

    [Fact]
    public void DrawerShowsARetryableErrorWhenChildLoadingFails()
    {
        var attempts = 0;
        Services.AddSingleton(EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetIngestionMediaChildrenAsync), _ =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException<PagedResponse<IngestionMediaChildDto>?>(new HttpRequestException("offline"))
                    : Task.FromResult<PagedResponse<IngestionMediaChildDto>?>(new(Children(1, 1, "Recovered child"), 0, 250, false, 1));
            })));

        var cut = Render<IngestionMediaDrawer>(parameters => parameters.Add(component => component.Item, Group()));

        cut.WaitForAssertion(() => Assert.Contains("Child details could not be loaded. Try again.", cut.Markup));
        Assert.Single(cut.FindAll("[role='alert']"));
        Assert.Contains("Try again", cut.Find("[role='alert']").TextContent);
        cut.Find("[role='alert'] button").Click();
        cut.WaitForAssertion(() => Assert.Contains("Recovered child", cut.Markup));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void DrawerStopsAndExplainsMalformedPagingInsteadOfLooping()
    {
        var calls = 0;
        Services.AddSingleton(EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetIngestionMediaChildrenAsync), _ =>
            {
                calls++;
                return Task.FromResult<PagedResponse<IngestionMediaChildDto>?>(new([], 0, 250, true, null));
            })));

        var cut = Render<IngestionMediaDrawer>(parameters => parameters.Add(component => component.Item, Group()));

        cut.WaitForAssertion(() => Assert.Contains("Child details could not be loaded. Try again.", cut.Markup));
        Assert.Equal(1, calls);
    }

    private static IngestionMediaGroupDto Group(string title = "Album") => new()
    {
        BatchId = Guid.NewGuid(),
        GroupId = Guid.NewGuid(),
        Title = title,
        MediaType = "Music",
        ChildUnit = "tracks",
        ChildCompleted = 1,
    };

    private static IReadOnlyList<IngestionMediaChildDto> Children(int count, int firstSequence, string? title = null) =>
        Enumerable.Range(firstSequence, count)
            .Select(index => new IngestionMediaChildDto
            {
                Title = title ?? $"Child {index}",
                SequenceLabel = index.ToString(),
                Status = "complete",
            })
            .ToList();
}
