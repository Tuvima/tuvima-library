using Bunit;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>The desktop lyrics card has its own close button, like the phone sheet (spec music-player-and-queue D7, bug F56).</summary>
public sealed class PlaybackLyricsCloseButtonTests : AsyncBunitContext
{
    public PlaybackLyricsCloseButtonTests() { JSInterop.Mode = JSRuntimeMode.Loose; Services.AddLogging(); Services.AddLocalization(); Services.AddNativeUiServices(); }

    private IRenderedComponent<PlaybackPopover> RenderPopover(string kind) =>
        Render<PlaybackPopover>(parameters => parameters
            .Add(p => p.Title, "Lyrics")
            .Add(p => p.Kind, kind)
            .Add(p => p.TriggerContent, (RenderFragment)(builder => builder.AddContent(0, "L")))
            .Add(p => p.ChildContent, (RenderFragment)(builder => builder.AddContent(0, "words"))));

    [Fact]
    public async Task TheDesktopLyricsCard_HasACloseButtonThatClosesIt()
    {
        var cut = RenderPopover("lyrics");
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));

        var close = cut.WaitForElement("button[aria-label='Close Lyrics']");
        await close.ClickAsync();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-playback-popover-panel]")));
    }

    [Fact]
    public async Task OtherPopovers_DoNotGetACloseButton()
    {
        var cut = RenderPopover("menu");
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));

        cut.WaitForElement("[data-playback-popover-panel]");
        Assert.Empty(cut.FindAll("button[aria-label='Close Lyrics']"));
    }
}
