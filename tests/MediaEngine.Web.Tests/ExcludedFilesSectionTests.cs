using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class ExcludedFilesSectionTests : AsyncBunitContext
{
    private const string LibraryId = "11111111-1111-4111-8111-111111111111";

    private readonly List<ExcludedFileDto> _files = [];
    private readonly List<Guid> _addedBack = [];
    private readonly List<string?> _loadedFor = [];
    private ExcludedFileAddBackResultDto? _addBackAnswer = new() { Queued = true, Message = "Added back. Library will scan it again." };
    private TaskCompletionSource<ExcludedFileAddBackResultDto?>? _gate;

    public ExcludedFilesSectionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetExcludedFilesAsync), args =>
            {
                _loadedFor.Add((string?)args![0]);
                return Task.FromResult<IReadOnlyList<ExcludedFileDto>>(_files.ToList());
            });
            stub.SetHandler(nameof(IEngineApiClient.AddBackExcludedFileAsync), args =>
            {
                _addedBack.Add((Guid)args![0]!);
                return _gate is null ? Task.FromResult<ExcludedFileAddBackResultDto?>(_addBackAnswer) : _gate.Task;
            });
        }));
    }

    private static ExcludedFileDto File(string name, string folder, DateTimeOffset when, string? by = "Shy") => new()
    {
        Id = Guid.NewGuid(),
        LibraryId = LibraryId,
        FileName = name,
        FolderLabel = folder,
        Title = "Dune",
        ExcludedAt = when,
        ExcludedBy = by,
        Reason = "as_is",
    };

    private IRenderedComponent<ExcludedFilesSection> Open(bool readOnly = false) =>
        Render<ExcludedFilesSection>(parameters => parameters
            .Add(component => component.LibraryId, LibraryId)
            .Add(component => component.ReadOnly, readOnly));

    [Fact]
    public void WithNoExcludedFiles_TheSectionIsNotDrawnAtAll()
    {
        var cut = Open();

        cut.WaitForAssertion(() => Assert.Contains(LibraryId, _loadedFor));
        Assert.Empty(cut.FindAll(".excluded-files"));
        Assert.DoesNotContain("Excluded files", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutALibrary_NothingIsAskedOfTheEngine_AndNothingIsDrawn()
    {
        var cut = Render<ExcludedFilesSection>();

        Assert.Empty(_loadedFor);
        Assert.Empty(cut.FindAll(".excluded-files"));
    }

    [Fact]
    public void EachRow_ShowsTheFile_ItsFolder_WhenWithTheExactTimeOnHover_AndWho()
    {
        var when = DateTimeOffset.UtcNow.AddHours(-3);
        _files.Add(File("Dune.mkv", "Old Rips › Sci-Fi", when));

        var cut = Open();

        cut.WaitForElement(".excluded-files");
        Assert.Contains("Excluded files", cut.Find(".excluded-files__title").TextContent);
        Assert.Contains("These files stayed on disk when their title was deleted. Scans skip them until you add them back.", cut.Find(".excluded-files__hint").TextContent);
        var row = Assert.Single(cut.FindAll("tbody tr"));
        var cells = row.QuerySelectorAll("td").Select(cell => cell.TextContent.Trim()).ToList();
        Assert.Equal("Dune.mkv", cells[0]);
        Assert.Equal("Old Rips › Sci-Fi", cells[1]);
        Assert.Equal("3h ago", cells[2]);
        Assert.Equal("Shy", cells[3]);
        Assert.False(string.IsNullOrWhiteSpace(row.QuerySelector(".excluded-files__when")!.GetAttribute("title")));
        Assert.Equal("Add back", row.QuerySelector("button")!.TextContent.Trim());
    }

    [Fact]
    public void ARowWithNoRecordedPerson_ShowsADashInsteadOfAnEmptyCell()
    {
        _files.Add(File("Dune.mkv", "Movies", DateTimeOffset.UtcNow, by: null));

        var cut = Open();

        cut.WaitForElement(".excluded-files");
        Assert.Equal("—", cut.FindAll("tbody tr td")[3].TextContent.Trim());
    }

    [Fact]
    public void AddBack_CallsTheEngine_RemovesThatRow_AndTellsThePerson()
    {
        var dune = File("Dune.mkv", "Movies", DateTimeOffset.UtcNow.AddHours(-2));
        var arrival = File("Arrival.mkv", "Movies", DateTimeOffset.UtcNow.AddHours(-1));
        _files.AddRange([dune, arrival]);
        var toast = Services.GetRequiredService<AppToastService>();
        var cut = Open();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tbody tr").Count));

        cut.FindAll("tbody tr").Single(row => row.TextContent.Contains("Dune.mkv")).QuerySelector("button")!.Click();

        cut.WaitForAssertion(() => Assert.Equal(["Arrival.mkv"], cut.FindAll("tbody tr td:first-child").Select(cell => cell.TextContent.Trim())));
        Assert.Equal([dune.Id], _addedBack);
        Assert.Contains(toast.Toasts, item => item.Message == "Added back. Library will scan it again." && item.Severity == AppSeverity.Success);
    }

    [Fact]
    public void AddingTheLastFileBack_RemovesTheWholeSection()
    {
        _files.Add(File("Dune.mkv", "Movies", DateTimeOffset.UtcNow));
        var cut = Open();
        cut.WaitForElement(".excluded-files");

        cut.Find("tbody tr button").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".excluded-files")));
    }

    [Fact]
    public void WhenTheFileIsNoLongerOnDisk_TheRowStillGoes_AndTheEnginesMessageIsShown()
    {
        _files.Add(File("Gone.mkv", "Movies", DateTimeOffset.UtcNow));
        _addBackAnswer = new() { Queued = false, Message = "The file is no longer on disk." };
        var toast = Services.GetRequiredService<AppToastService>();
        var cut = Open();
        cut.WaitForElement(".excluded-files");

        cut.Find("tbody tr button").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".excluded-files")));
        Assert.Contains(toast.Toasts, item => item.Message == "The file is no longer on disk.");
    }

    [Fact]
    public void WhenTheEngineCannotAddItBack_TheRowStays_AndAnErrorIsShown()
    {
        _files.Add(File("Dune.mkv", "Movies", DateTimeOffset.UtcNow));
        _addBackAnswer = null;
        var toast = Services.GetRequiredService<AppToastService>();
        var cut = Open();
        cut.WaitForElement(".excluded-files");

        cut.Find("tbody tr button").Click();

        cut.WaitForAssertion(() => Assert.Contains(toast.Toasts, item => item.Severity == AppSeverity.Error));
        Assert.Single(cut.FindAll("tbody tr"));
        Assert.False(cut.Find("tbody tr button").HasAttribute("disabled"));
    }

    [Fact]
    public void WhileOneFileIsBeingAddedBack_EveryAddBackButtonIsDisabled()
    {
        _files.Add(File("Dune.mkv", "Movies", DateTimeOffset.UtcNow.AddHours(-2)));
        _files.Add(File("Arrival.mkv", "Movies", DateTimeOffset.UtcNow.AddHours(-1)));
        _gate = new TaskCompletionSource<ExcludedFileAddBackResultDto?>();
        var cut = Open();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("tbody tr").Count));

        cut.FindAll("tbody tr button")[0].Click();

        cut.WaitForAssertion(() => Assert.All(cut.FindAll("tbody tr button"), button => Assert.True(button.HasAttribute("disabled"))));
        _gate.SetResult(new ExcludedFileAddBackResultDto { Queued = true, Message = "Added back. Library will scan it again." });
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("tbody tr")));
    }

    [Fact]
    public void WhenReadOnly_TheListShowsButThereIsNoAddBack()
    {
        _files.Add(File("Dune.mkv", "Movies", DateTimeOffset.UtcNow));

        var cut = Open(readOnly: true);

        cut.WaitForElement(".excluded-files");
        Assert.Empty(cut.FindAll("tbody tr button"));
        Assert.DoesNotContain("Add back", cut.FindAll("tbody tr").Single().TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingTheLibrary_ListsThatLibrarysFilesInstead()
    {
        _files.Add(File("Dune.mkv", "Movies", DateTimeOffset.UtcNow));
        var cut = Open();
        cut.WaitForElement(".excluded-files");

        cut.Render(parameters => parameters.Add(component => component.LibraryId, "22222222-2222-4222-8222-222222222222"));

        cut.WaitForAssertion(() => Assert.Equal([LibraryId, "22222222-2222-4222-8222-222222222222"], _loadedFor));
    }
}
