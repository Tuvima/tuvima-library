using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// The folder add and organise steps hand back what the person must see (a notice or nothing) and never pop anything up themselves.
public sealed class LibraryFolderAddFeedbackTests : AsyncBunitContext
{
    private int _totalFiles;
    private bool _jobStarts = true;

    public LibraryFolderAddFeedbackTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler("get_LastError", _ => null);
            stub.SetHandler(nameof(IEngineApiClient.PreviewFolderConversionAsync), _ => Task.FromResult<FolderConversionPreviewResponse?>(new() { TotalFiles = _totalFiles }));
            stub.SetHandler(nameof(IEngineApiClient.ConvertFolderAsync), args => Task.FromResult<FolderJobStatusDto?>(
                _jobStarts ? new FolderJobStatusDto { OperationId = "job-1", SourceId = (string)args![0]!, State = "queued" } : null));
        }));
    }

    private LibraryFolderAddService Service() =>
        new(Services.GetRequiredService<IEngineApiClient>(), Services.GetRequiredService<IAppDialogService>(), Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<MediaEngine.Web.Resources.SharedStrings>>());

    private static readonly LibrarySourceDto Source = new() { Id = "movies-main", Path = "E:\\Movies" };

    [Fact]
    public async Task OrganisingAFolderWithNoMediaFiles_WhenAsked_HandsBackANoticeToShowInPlace()
    {
        _totalFiles = 0;

        var followUp = await Service().OfferOrganiseAsync(FolderAddScope.Settings, Source, alreadyManaged: true, quiet: false);

        Assert.False(followUp.Started);
        Assert.Equal("There are no media files in this folder to organise.", followUp.Notice);
        // Nothing went wrong, so the panel draws it as information and not as a warning.
        Assert.True(followUp.NoticeIsInfo);
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);
    }

    [Fact]
    public async Task OrganisingAFolderWithNoMediaFiles_AfterAddingIt_SaysNothing()
    {
        _totalFiles = 0;

        var followUp = await Service().OfferOrganiseAsync(FolderAddScope.Settings, Source, alreadyManaged: false, quiet: true);

        Assert.False(followUp.Started);
        Assert.Null(followUp.Notice);
    }

    [Fact]
    public async Task StartingTheOrganisingJob_ReportsItAsStartedWithoutAToast()
    {
        var followUp = await Service().StartConversionAsync(Source);

        Assert.True(followUp.Started);
        Assert.Null(followUp.Notice);
        Assert.Empty(Services.GetRequiredService<AppToastService>().Toasts);
    }

    [Fact]
    public async Task WhenTheOrganisingJobCannotStart_TheReasonComesBackAsTheNotice()
    {
        _jobStarts = false;

        var followUp = await Service().StartConversionAsync(Source);

        Assert.False(followUp.Started);
        Assert.Equal("Library could not start organising this folder.", followUp.Notice);
    }
}
