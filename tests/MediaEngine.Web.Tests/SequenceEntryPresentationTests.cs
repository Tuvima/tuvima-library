using Bunit;
using MediaEngine.Contracts.Details;
using MediaEngine.Web.Components.Details;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace MediaEngine.Web.Tests;

public sealed class SequenceEntryPresentationTests : AsyncBunitContext
{
    public SequenceEntryPresentationTests()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void MissingEntryDoesNotPresentProviderArtworkAsOwnedOrInventActions()
    {
        var cut = Render<SequenceEntryContent>(parameters => parameters
            .Add(component => component.Item, new SequenceItemViewModel
            {
                Title = "The Missing Coast",
                IsOwned = false,
                ArtworkUrl = "/provider-cover.jpg",
            })
            .Add(component => component.PositionLabel, "3")
            .Add(component => component.Title, "The Missing Coast"));

        Assert.Equal("3", cut.Find(".tl-series-item__node").TextContent);
        Assert.Contains("Not in library", cut.Find("[aria-label='Missing from library']").TextContent);
        Assert.Empty(cut.FindAll("img, a, button"));
    }

    [Fact]
    public void CompletedEntryKeepsCompletionSeparateFromSequencePosition()
    {
        var cut = Render<SequenceEntryContent>(parameters => parameters
            .Add(component => component.Item, new SequenceItemViewModel
            {
                Title = "The First Coast",
                IsOwned = true,
                ProgressState = LibraryProgressState.Completed,
            })
            .Add(component => component.PositionLabel, "1")
            .Add(component => component.Title, "The First Coast"));

        Assert.Equal("1", cut.Find(".tl-series-item__node").TextContent);
        Assert.Equal("Completed", cut.Find(".tl-series-item__completed-badge .sr-only").TextContent);
        Assert.NotNull(cut.Find("[aria-label='Artwork unavailable']"));
        Assert.Empty(cut.FindAll("[role='progressbar']"));
    }
}
