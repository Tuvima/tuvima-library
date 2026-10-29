using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Settings;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The editable Ingestion Flow (Provider settings spec P2): read-only for everyone, editable on a computer, and the
/// draft refuses a media type left without a provider that identifies it.
/// </summary>
public sealed class IngestionFlowEditorTests : AsyncBunitContext
{
    private readonly DeviceContextService _device;

    public IngestionFlowEditorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPipelinesAsync), _ => Task.FromResult<PipelineConfiguration?>(SamplePipelines()));
            stub.SetHandler(nameof(IEngineApiClient.GetPipelineFlowOptionsAsync), _ => Task.FromResult<PipelineFlowOptionsResponse?>(SampleOptions()));
        });
        Services.AddSingleton(api);
        _device = new DeviceContextService(api);
        Services.AddSingleton(_device);
        Services.AddSingleton<IAppDialogService>(new AppDialogService());
    }

    [Fact]
    public async Task Flow_OnAComputer_ShowsEveryMediaTypeAndAnEditButton()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));

        var cut = Render<IngestionFlowEditor>();

        Assert.Equal(2, cut.FindAll("[data-testid=flow-card]").Count);
        Assert.NotEmpty(cut.FindAll("[data-testid=flow-edit]"));
        Assert.DoesNotContain("flow-save", cut.Markup);
    }

    [Fact]
    public async Task Flow_OnATouchDevice_CannotBeEdited()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "mobile", true, FinePointer: false, Hover: false));

        var cut = Render<IngestionFlowEditor>();

        Assert.Empty(cut.FindAll("[data-testid=flow-edit]"));
        Assert.Equal(2, cut.FindAll("[data-testid=flow-card]").Count);
    }

    [Fact]
    public async Task Edit_ThenTurningOffTheOnlyIdentityProvider_BlocksSaving()
    {
        await _device.InitialiseAsync(new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true));
        var cut = Render<IngestionFlowEditor>();

        cut.Find("[data-testid=flow-edit]").Click();
        var select = cut.Find("select[aria-label='Role of Alpha in Movies']");
        select.Change(IngestionFlowDraft.Off);

        Assert.NotEmpty(cut.FindAll("[data-testid=flow-problem]"));
        Assert.True(cut.Find("[data-testid=flow-save]").HasAttribute("disabled"));
    }

    [Fact]
    public void Draft_ChangingARole_IsReviewedInPlainSentencesAndSentToTheEngine()
    {
        var draft = IngestionFlowDraft.Create(SamplePipelines(), SampleOptions(), ["Movies", "Music"]);

        draft.SetRole("Movies", 0, PipelineFlowRoles.Fallback);
        draft.Add("Movies", "beta");

        Assert.True(draft.IsChanged("Movies"));
        Assert.False(draft.IsChanged("Music"));
        var review = Assert.Single(draft.Review(type => type));
        Assert.Contains("Alpha changes from Primary to Fallback.", review.Lines);
        Assert.Contains("Beta is added as Fallback.", review.Lines);
        var request = draft.ToRequest();
        Assert.Equal(["alpha", "beta"], request.MediaTypes["Movies"].Select(provider => provider.Name));
        Assert.DoesNotContain("Music", request.MediaTypes.Keys);
    }

    [Fact]
    public void Draft_Discard_RestoresWhatWasSaved()
    {
        var draft = IngestionFlowDraft.Create(SamplePipelines(), SampleOptions(), ["Movies", "Music"]);
        draft.SetRole("Movies", 0, IngestionFlowDraft.Off);
        draft.Add("Music", "beta");

        draft.Discard();

        Assert.False(draft.HasChanges);
        Assert.Empty(draft.Problems);
    }

    [Fact]
    public void Draft_MovingARow_ChangesTheOrderAndNothingElse()
    {
        var draft = IngestionFlowDraft.Create(SamplePipelines(), SampleOptions(), ["Movies", "Music"]);
        draft.Add("Movies", "beta");

        Assert.True(draft.MoveUp("Movies", 1));
        Assert.False(draft.MoveUp("Movies", 0));

        Assert.Equal(["beta", "alpha"], draft.RowsFor("Movies").Select(row => row.Name));
    }

    private static PipelineConfiguration SamplePipelines()
    {
        var pipelines = new PipelineConfiguration();
        pipelines.Pipelines["Movies"] = new MediaTypePipeline
        {
            Providers = [new PipelineProviderEntry { Name = "alpha", Rank = 1, Purpose = "identity" }],
        };
        pipelines.Pipelines["Music"] = new MediaTypePipeline
        {
            Providers = [new PipelineProviderEntry { Name = "alpha", Rank = 1, Purpose = "identity" }],
        };
        return pipelines;
    }

    private static PipelineFlowOptionsResponse SampleOptions() => new()
    {
        Providers =
        [
            new PipelineFlowOptionDto { Name = "alpha", DisplayName = "Alpha", Enabled = true, MediaTypes = ["Movies", "Music"] },
            new PipelineFlowOptionDto { Name = "beta", DisplayName = "Beta", Enabled = true, MediaTypes = ["Movies", "Music"] },
        ],
    };
}
