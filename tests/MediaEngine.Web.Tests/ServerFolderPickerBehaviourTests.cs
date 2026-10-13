using Bunit;
using MediaEngine.Contracts.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Services.Ui;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class ServerFolderPickerBehaviourTests : AsyncBunitContext
{
    private static readonly ServerStorageLocationDto Drive = new() { Id = "drive-c", Label = "C:", Path = "C:\\srv", AllowWrite = true };
    private bool _hasRead = true;
    private bool _hasWrite = true;
    private readonly List<string> _browsed = [];

    public ServerFolderPickerBehaviourTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddNativeUiServices();
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetServerFolderRootsAsync), _ => Task.FromResult<IReadOnlyList<ServerStorageLocationDto>>([Drive]));
            stub.SetHandler(nameof(IEngineApiClient.BrowseServerFoldersAsync), args =>
            {
                var request = (BrowseServerFoldersRequest)args![0]!;
                var relative = request.RelativePath ?? string.Empty;
                _browsed.Add(relative);
                return Task.FromResult<BrowseServerFoldersResultDto?>(Browse(relative));
            });
            stub.SetHandler(nameof(IEngineApiClient.ValidateServerFolderAsync), args =>
            {
                var request = (ValidateServerFolderRequest)args![0]!;
                var relative = request.ManualPath is { } manual
                    ? manual.Replace("C:\\srv", string.Empty, StringComparison.OrdinalIgnoreCase).Trim('\\')
                    : request.RelativePath ?? string.Empty;
                return Task.FromResult<ServerFolderValidationResultDto?>(new()
                {
                    StorageLocationId = Drive.Id,
                    RelativePath = relative,
                    Path = relative.Length == 0 ? Drive.Path : $"{Drive.Path}\\{relative}",
                    Exists = true,
                    HasRead = _hasRead,
                    HasWrite = _hasWrite,
                    AvailableBytes = 5_000_000_000,
                    FileSystem = "NTFS",
                    CanSelect = _hasRead,
                });
            });
        }));
        Services.AddScoped<DeviceContextService>();
        Render<AppPopoverHost>();
    }

    private static BrowseServerFoldersResultDto Browse(string relative) => new()
    {
        StorageLocation = Drive,
        RelativePath = relative,
        ParentRelativePath = relative.Length == 0 ? null : relative.Contains('\\') ? relative[..relative.LastIndexOf('\\')] : string.Empty,
        DisplayPath = relative.Length == 0 ? Drive.Path : $"{Drive.Path}\\{relative}",
        Directories = relative switch
        {
            "" => [new() { Name = "Movies", RelativePath = "Movies" }, new() { Name = "TV", RelativePath = "TV" }],
            "Movies" => [new() { Name = "Action", RelativePath = "Movies\\Action" }],
            _ => [],
        },
    };

    private async Task<IRenderedComponent<AppDialogHost>> OpenPickerAsync(string selectionMode = ServerFolderSelectionModes.ExistingLibrary)
    {
        var host = Render<AppDialogHost>();
        var parameters = new AppDialogParameters<ServerFolderPicker> { { picker => picker.SelectionMode, selectionMode } };
        await host.InvokeAsync(() => Services.GetRequiredService<IAppDialogService>().ShowAsync<ServerFolderPicker>(string.Empty, parameters));
        host.WaitForElement(".server-folder-picker__folder");
        return host;
    }

    private static string PathBarValue(IRenderedComponent<AppDialogHost> host) =>
        host.Find(".server-folder-picker__pathbar input").GetAttribute("value") ?? string.Empty;

    [Fact]
    public async Task ClickingAFolderOpensItAndThePathBarFollows()
    {
        var host = await OpenPickerAsync();
        host.WaitForAssertion(() => Assert.Equal("C:\\srv", PathBarValue(host)));

        host.FindAll(".server-folder-picker__folder").Single(row => row.TextContent.Contains("Movies")).Click();

        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\Movies", PathBarValue(host)));
        Assert.Contains("Action", host.Find(".server-folder-picker__list").TextContent);
        Assert.Contains("Movies", _browsed);
    }

    [Fact]
    public async Task FolderRowsHaveNoSeparateSelectOrChevronButtons()
    {
        var host = await OpenPickerAsync();

        Assert.Empty(host.FindAll("[aria-label^='Select folder']"));
        Assert.All(host.FindAll(".server-folder-picker__folder"), row => Assert.Empty(row.QuerySelectorAll("button")));
        Assert.DoesNotContain("Enter path manually", host.Markup, StringComparison.Ordinal);
        Assert.Empty(host.FindAll(".server-folder-picker__manual"));
    }

    [Fact]
    public async Task TypingAPathAndPressingEnterNavigatesThere()
    {
        var host = await OpenPickerAsync();

        var input = host.Find(".server-folder-picker__pathbar input");
        input.Input("C:\\srv\\Movies");
        Assert.True(host.FindAll("button").Single(button => button.TextContent.Trim() == "Add").HasAttribute("disabled"));
        input.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });

        host.WaitForAssertion(() => Assert.Contains("Action", host.Find(".server-folder-picker__list").TextContent));
        Assert.Equal("C:\\srv\\Movies", PathBarValue(host));
        host.WaitForAssertion(() => Assert.False(host.FindAll("button").Single(button => button.TextContent.Trim() == "Add").HasAttribute("disabled")));
    }

    [Fact]
    public async Task OnlyFoldersAreEverRendered()
    {
        var host = await OpenPickerAsync();

        var rows = host.FindAll(".server-folder-picker__folder");
        Assert.Equal(["Movies", "TV"], rows.Select(row => row.QuerySelector("strong")!.TextContent).ToArray());
        Assert.DoesNotContain(".mkv", host.Find(".server-folder-picker__list").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task KeyboardMovesTheHighlightOpensFoldersAndBackspaceGoesUp()
    {
        var host = await OpenPickerAsync();
        var list = host.Find(".server-folder-picker__list");

        list.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowDown" });
        host.WaitForAssertion(() => Assert.Contains("is-highlighted", host.FindAll(".server-folder-picker__folder")[1].ClassName));
        list.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowUp" });
        list.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Enter" });
        host.WaitForAssertion(() => Assert.Equal("C:\\srv\\Movies", PathBarValue(host)));

        host.Find(".server-folder-picker__list").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Backspace" });
        host.WaitForAssertion(() => Assert.Equal("C:\\srv", PathBarValue(host)));
    }

    [Fact]
    public async Task AccessFactsAreHiddenWhenTheFolderIsFine()
    {
        var host = await OpenPickerAsync(ServerFolderSelectionModes.ManagedLibrary);
        host.WaitForAssertion(() => Assert.False(host.FindAll("button").Single(button => button.TextContent.Trim() == "Add").HasAttribute("disabled")));

        var text = host.Markup;
        Assert.DoesNotContain("Read access", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Write access", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Space available", text, StringComparison.Ordinal);
        Assert.DoesNotContain("NTFS", text, StringComparison.Ordinal);
        Assert.Empty(host.FindAll(".server-folder-picker__issues"));
    }

    [Fact]
    public async Task ProblemsAreShownInPlainEnglish()
    {
        _hasWrite = false;
        var host = await OpenPickerAsync(ServerFolderSelectionModes.ManagedLibrary);

        host.WaitForAssertion(() => Assert.Contains("can't write to this folder", host.Find(".server-folder-picker__notices").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PhoneDevicesGetThePhoneLayoutClass()
    {
        await Services.GetRequiredService<DeviceContextService>().InitialiseAsync("mobile");
        var host = await OpenPickerAsync();

        Assert.Contains("server-folder-picker--phone", host.Find(".server-folder-picker").ClassName);
    }
}
