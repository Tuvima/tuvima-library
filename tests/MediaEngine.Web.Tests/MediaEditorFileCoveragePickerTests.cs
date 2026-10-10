using Bunit;
using MediaEngine.Contracts.Metadata;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

/// <summary>The editor's "This file covers" picker: chips for the season, a guarded save, phone-friendly styling.</summary>
public sealed class MediaEditorFileCoveragePickerTests : AsyncBunitContext
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly Guid Asset = Guid.NewGuid();
    private static readonly Guid Host = Guid.NewGuid();
    private static readonly Guid Second = Guid.NewGuid();
    private static readonly Guid Third = Guid.NewGuid();
    private static readonly Guid Taken = Guid.NewGuid();

    private static MediaEditorFileCoverageDto Season(params Guid[] covered) => new(
        Asset,
        Host,
        6,
        [
            new(Host, 1, "Pilot", true, true, false),
            new(Second, 2, "Second", false, covered.Contains(Second), false),
            new(Third, 3, "Third", false, covered.Contains(Third), false),
            new(Taken, 4, "Fourth", false, false, true),
        ]);

    private (IRenderedComponent<MediaEditorFileCoveragePicker> Cut, List<MediaEditorFileCoverageSaveRequestDto> Saves) Render(
        MediaEditorFileCoverageDto? loaded)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var saves = new List<MediaEditorFileCoverageSaveRequestDto>();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetMediaEditorFileCoverageAsync),
                _ => Task.FromResult(loaded));
            stub.SetHandler(nameof(IEngineApiClient.SaveMediaEditorFileCoverageAsync), args =>
            {
                var request = (MediaEditorFileCoverageSaveRequestDto)args![1]!;
                saves.Add(request);
                return Task.FromResult<MediaEditorFileCoverageDto?>(Season(request.WorkIds.ToArray()));
            });
        });
        Services.AddSingleton(api);
        var cut = Render<MediaEditorFileCoveragePicker>(parameters => parameters
            .Add(p => p.EntityId, Entity)
            .Add(p => p.AssetId, Asset));
        return (cut, saves);
    }

    [Fact]
    public void ShowsOneChipPerEpisode_WithTheHostAndTakenEpisodesLocked()
    {
        var (cut, _) = Render(Season());

        var chips = cut.FindAll(".sme-file-coverage__chip");
        Assert.Equal(4, chips.Count);
        var inputs = cut.FindAll(".sme-file-coverage__chip input");
        Assert.True(inputs[0].HasAttribute("disabled"));
        Assert.True(inputs[0].HasAttribute("checked"));
        Assert.False(inputs[1].HasAttribute("disabled"));
        Assert.False(inputs[2].HasAttribute("disabled"));
        Assert.True(inputs[3].HasAttribute("disabled"));
    }

    [Fact]
    public void StaysHidden_WhenTheFileIsNotAnEpisode()
    {
        var (missing, _) = Render(null);
        Assert.Empty(missing.FindAll(".sme-file-coverage"));
    }

    [Fact]
    public void SaveStaysOff_UntilThePersonChangesTheSelection()
    {
        var (cut, _) = Render(Season());

        Assert.True(SaveButton(cut).HasAttribute("disabled"));

        cut.FindAll(".sme-file-coverage__chip input")[1].Change(true);

        Assert.False(SaveButton(cut).HasAttribute("disabled"));
    }

    [Fact]
    public void Saving_SendsTheHostAndTickedEpisodes_InSeasonOrder()
    {
        var (cut, saves) = Render(Season());

        cut.FindAll(".sme-file-coverage__chip input")[2].Change(true);
        cut.FindAll(".sme-file-coverage__chip input")[1].Change(true);
        SaveButton(cut).Click();

        var request = Assert.Single(saves);
        Assert.Equal(Asset, request.AssetId);
        Assert.NotEqual(Guid.Empty, request.OperationId);
        Assert.Equal([Host, Second, Third], request.WorkIds);
        Assert.Contains("covers 3 episodes", cut.Find(".sme-file-coverage__status").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Styles_KeepTouchTargetsLargeAndStackChipsOnPhones()
    {
        var css = File.ReadAllText(Path.Combine(
            RepoRoot, "src", "MediaEngine.Web", "wwwroot", "app.css"));
        css = css[css.IndexOf("Editor \"This file covers\" picker", StringComparison.Ordinal)..];

        Assert.Contains("min-height: 44px", css, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 600px)", css, StringComparison.Ordinal);
        Assert.DoesNotContain("::deep", css, StringComparison.Ordinal);
        Assert.DoesNotContain("!important", css, StringComparison.Ordinal);
    }

    private static AngleSharp.Dom.IElement SaveButton(IRenderedComponent<MediaEditorFileCoveragePicker> cut) =>
        cut.FindAll("button").Single(button => button.TextContent.Contains("Save episodes", StringComparison.Ordinal));
}
