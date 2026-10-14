using Bunit;
using MediaEngine.Contracts.Review;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>The "From merge" badge on a Review queue row, for files sitting in a managed folder's From merge holding folder.</summary>
public sealed class ReviewFromMergeBadgeTests : AsyncBunitContext
{
    [Fact]
    public void ReviewQueueRow_ShowsTheFromMergeBadgeOnlyForFilesFromAMerge()
    {
        var held = Item("33333333-3333-4333-8333-333333333333", "Held back film", fromMerge: true);
        var ordinary = Item("44444444-4444-4444-8444-444444444444", "Ordinary film", fromMerge: false);
        var importFile = Item("55555555-5555-4555-8555-555555555555", "Import film", fromMerge: false);
        importFile.FromImportFolder = true;
        var api = EngineApiClientStub.Create(stub =>
            stub.SetHandler(nameof(IEngineApiClient.GetPendingReviewsAsync), _ =>
                Task.FromResult(new List<ReviewItemViewModel> { held, ordinary, importFile })));
        Services.AddSingleton(api);
        Services.AddSingleton(provider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            new ActiveProfileSessionService(provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
        Services.AddScoped<MediaEditorLauncherService>();
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render<SettingsReviewQueueTab>();

        cut.WaitForAssertion(() => Assert.Equal(3, cut.FindAll("article.settings-review-row").Count));
        var rows = cut.FindAll("article.settings-review-row");
        Assert.Contains("From merge", RowFor(rows, "Held back film").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("From merge", RowFor(rows, "Ordinary film").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("From merge", RowFor(rows, "Import film").TextContent, StringComparison.Ordinal);
        Assert.Contains("From the Import folder", RowFor(rows, "Import film").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDashboardMapper_CarriesTheFlagAcross_AndTheWireNameIsFromMerge()
    {
        var dto = new ReviewItemDto { Id = Guid.NewGuid(), FromMerge = true };

        Assert.True(dto.ToViewModel().FromMerge);
        Assert.False(new ReviewItemDto { Id = Guid.NewGuid() }.ToViewModel().FromMerge);
        Assert.Contains("\"from_merge\":true", System.Text.Json.JsonSerializer.Serialize(dto), StringComparison.Ordinal);
    }

    private static AngleSharp.Dom.IElement RowFor(IReadOnlyList<AngleSharp.Dom.IElement> rows, string title) =>
        rows.Single(row => row.TextContent.Contains(title, StringComparison.Ordinal));

    private static ReviewItemViewModel Item(string id, string title, bool fromMerge) => new()
    {
        Id = Guid.Parse(id),
        EntityId = Guid.NewGuid(),
        EntityType = "MediaAsset",
        Trigger = "RetailMatchFailed",
        Status = "Pending",
        MediaType = "Movies",
        EntityTitle = title,
        CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
        FromMerge = fromMerge,
    };
}
