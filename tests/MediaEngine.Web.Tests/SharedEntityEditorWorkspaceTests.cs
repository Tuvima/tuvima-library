using Bunit;
using MediaEngine.Contracts.Universe;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class SharedEntityEditorWorkspaceTests : AsyncBunitContext
{
    private static readonly Guid CharacterId = Guid.Parse("48d9a63a-f945-467e-b14f-046b1f748441");
    private static readonly Guid SiblingId = Guid.Parse("04dbf67c-a0ea-4bf6-a105-e939c2e18491");
    private readonly List<bool> _dirtyChanges = [];

    public SharedEntityEditorWorkspaceTests()
    {
        Services.AddNativeUiServices();
        Services.AddSingleton(CreateApi());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void UniverseCategoryRailUsesApiOrderAndAnchoredBoundedSelectorsRetargetInPlace()
    {
        var cut = Render<SharedEntityEditorWorkspace>(parameters => parameters
            .Add(component => component.InitialTarget, RootTarget())
            .Add(component => component.DirtyChanged, EventCallback.Factory.Create<bool>(this, RecordDirty)));

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".see-category").Count));
        Assert.Equal(
            ["Characters", "Locations/Places", "Organizations/Groups", "Events", "Objects"],
            cut.FindAll(".see-category").Select(node => node.GetAttribute("aria-label")!).ToArray());
        Assert.Equal(5, cut.FindAll(".see-category .tl-icon").Count);
        Assert.DoesNotContain("More", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Scroll categories left", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Scroll categories right", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("see-selector-popover", cut.Find("#see-category-Character").GetAttribute("aria-controls"));
        Assert.Equal("dialog", cut.Find("#see-category-Character").GetAttribute("aria-haspopup"));
        Assert.Contains("@onwheel", ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedEntityEditorWorkspace.razor"), StringComparison.Ordinal);
        Assert.Contains("position: fixed", ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedEntityEditorWorkspace.razor.css"), StringComparison.Ordinal);
        Assert.Contains("tuvimaPositionSharedEntityPopover", ReadSource("src/MediaEngine.Web/wwwroot/app.js"), StringComparison.Ordinal);
        Assert.Contains("roomBelow", ReadSource("src/MediaEngine.Web/wwwroot/app.js"), StringComparison.Ordinal);
        Assert.Contains("event.stopPropagation()", ReadSource("src/MediaEngine.Web/wwwroot/app.js"), StringComparison.Ordinal);
        Assert.Contains("@onkeydown:stopPropagation=\"true\"", ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedEntityEditorWorkspace.razor"), StringComparison.Ordinal);
        cut.Find("#see-category-Character").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowRight" });
        cut.WaitForAssertion(() => Assert.Contains("is-active", cut.Find("#see-category-Location").ClassList));
        Assert.Equal("0", cut.Find("#see-category-Location").GetAttribute("tabindex"));
        Assert.Contains("tuvimaFocusById", ReadSource("src/MediaEngine.Web/wwwroot/app.js"), StringComparison.Ordinal);
        cut.Find("#see-category-Location").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowLeft" });
        cut.WaitForAssertion(() => Assert.Contains("is-active", cut.Find("#see-category-Character").ClassList));

        cut.FindAll(".see-category")[0].Click();
        cut.WaitForAssertion(() => Assert.Contains("Mara Venn", cut.Find(".see-selector-popover").TextContent, StringComparison.Ordinal));
        Assert.Contains("Load more", cut.Find(".see-selector-popover").TextContent, StringComparison.Ordinal);
        cut.Find(".see-selector-popover .see-selector-item").Click();

        cut.WaitForAssertion(() => Assert.Contains("Mara Venn", cut.Find(".see-breadcrumb").TextContent, StringComparison.Ordinal));
        Assert.Contains("Chronicle World", cut.Find(".see-breadcrumb").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("see-category-rail", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Other Character", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("Files", string.Join(" ", cut.FindAll(".see-nav-item").Select(node => node.TextContent)), StringComparison.OrdinalIgnoreCase);
        cut.Find(".see-sibling-toggle").Click();
        cut.WaitForAssertion(() => Assert.Contains("Mara Venn", cut.Find(".see-selector-popover").TextContent, StringComparison.Ordinal));
        Assert.Equal("true", cut.FindAll(".see-selector-popover .see-selector-item").First().GetAttribute("aria-selected"));
        cut.Find(".see-selector-popover").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".see-selector-popover")));
        Assert.Equal("see-selector-popover", cut.Find("#see-sibling-toggle").GetAttribute("aria-controls"));
        Assert.Equal("dialog", cut.Find("#see-sibling-toggle").GetAttribute("aria-haspopup"));
        cut.Find("#see-sibling-toggle").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".see-selector-popover")));
        cut.Find(".see-breadcrumb button").Click();
        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".see-category").Count));
    }

    [Fact]
    public void UniverseCategorySelectorRetargetsToLocationPlace()
    {
        var cut = Render<SharedEntityEditorWorkspace>(parameters => parameters
            .Add(component => component.InitialTarget, RootTarget()));

        cut.WaitForAssertion(() => Assert.Equal(5, cut.FindAll(".see-category").Count));
        cut.Find("#see-category-Location").Click();
        cut.WaitForAssertion(() => Assert.Contains("Old Harbor", cut.Find(".see-selector-popover").TextContent, StringComparison.Ordinal));
        cut.FindAll(".see-selector-popover .see-selector-item").Single().Click();

        cut.WaitForAssertion(() => Assert.Contains("Old Harbor", cut.Find(".see-breadcrumb").TextContent, StringComparison.Ordinal));
        Assert.Contains("Appearances", cut.FindAll(".see-nav-item").Select(node => node.TextContent));
    }

    [Fact]
    public void OrganizationMembersAndEventParticipantsUseFriendlyCapabilityProjections()
    {
        var organization = Render<SharedEntityEditorWorkspace>(parameters => parameters
            .Add(component => component.InitialTarget, EntityTarget(CharacterId, "QORG1")));
        organization.WaitForAssertion(() => Assert.Contains("Members", organization.FindAll(".see-nav-item").Select(node => node.TextContent)));
        Assert.Contains("Appearances", organization.FindAll(".see-nav-item").Select(node => node.TextContent));
        organization.FindAll(".see-nav-item").Single(node => node.TextContent == "Members").Click();
        organization.WaitForAssertion(() => Assert.Equal("Members", organization.Find(".see-content h3").TextContent));
        Assert.Equal(2, organization.FindAll(".see-record-list article").Count);
        Assert.Contains(organization.FindAll(".see-record-list article"), row => row.TextContent.Contains("member_of", StringComparison.Ordinal));
        Assert.Contains(organization.FindAll(".see-record-list article"), row => row.TextContent.Contains("has_parts", StringComparison.Ordinal));
        Assert.DoesNotContain("related_to", organization.Markup, StringComparison.OrdinalIgnoreCase);

        var eventEntity = Render<SharedEntityEditorWorkspace>(parameters => parameters
            .Add(component => component.InitialTarget, EntityTarget(SiblingId, "QEVENT1")));
        eventEntity.WaitForAssertion(() => Assert.Contains("Participants", eventEntity.FindAll(".see-nav-item").Select(node => node.TextContent)));
        Assert.Contains("Appearances", eventEntity.FindAll(".see-nav-item").Select(node => node.TextContent));
        eventEntity.FindAll(".see-nav-item").Single(node => node.TextContent == "Participants").Click();
        eventEntity.WaitForAssertion(() => Assert.Equal("Participants", eventEntity.Find(".see-content h3").TextContent));
        Assert.Single(eventEntity.FindAll(".see-record-list article"));
        Assert.Contains("participant", eventEntity.Find(".see-record-list article").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EntitySectionsAreCapabilityDrivenAndKeepTimelineSeparateFromHistory()
    {
        var entityTarget = EntityTarget(CharacterId, "QCHAR1");
        var cut = Render<SharedEntityEditorWorkspace>(parameters => parameters
            .Add(component => component.InitialTarget, entityTarget)
            .Add(component => component.DirtyChanged, EventCallback.Factory.Create<bool>(this, RecordDirty)));

        cut.WaitForAssertion(() => Assert.Contains("Appearances", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("Files", string.Join(" ", cut.FindAll(".see-nav-item").Select(node => node.TextContent)), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("In-universe timeline", cut.FindAll(".see-nav-item").Select(node => node.TextContent));

        cut.FindAll(".see-nav-item").Single(node => node.TextContent == "In-universe timeline").Click();
        cut.WaitForAssertion(() => Assert.Contains("Narrative chronology, not editor or audit history.", cut.Markup, StringComparison.Ordinal));
        cut.FindAll(".see-nav-item").Single(node => node.TextContent == "History").Click();
        cut.WaitForAssertion(() => Assert.Equal("History", cut.Find(".see-content h3").TextContent));

        cut.Find(".see-nav-item").Click();
        Assert.NotNull(cut.Find(".see-field textarea"));
        cut.Find(".see-field input").Input("Mara of the North");
        Assert.Contains(true, _dirtyChanges);
        cut.Find(".see-sibling-toggle").Click();
        cut.FindAll(".see-selector-popover .see-load-more").Last().Click();
        cut.FindAll(".see-selector-popover .see-selector-item").Single(node => node.TextContent.Contains("Elian", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Contains("Save changes before editing Elian?", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Discard and switch", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Save and switch", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("flex-direction: row", ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedEntityEditorWorkspace.razor.css").Split("@media (max-width: 720px)", StringSplitOptions.None).Last(), StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEditorShellBranchesBeforeMediaEntityLookupAndContainsWorkspaceOnly()
    {
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        var workspace = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedEntityEditorWorkspace.razor");
        var detailPage = ReadSource("src/MediaEngine.Web/Components/Details/DetailPage.razor");
        var sharedEntityIntegration = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.SharedEntity.cs");
        var branch = code.IndexOf("if (IsSharedEntityMode)", StringComparison.Ordinal);
        var providerInit = code.IndexOf("ProviderCatalogue.GetCatalogueAsync", StringComparison.Ordinal);

        Assert.True(branch >= 0 && branch < providerInit);
        Assert.Contains("Request.EntityIds.FirstOrDefault()", code, StringComparison.Ordinal);
        Assert.DoesNotContain("SharedEntityTarget =", detailPage, StringComparison.Ordinal);
        Assert.DoesNotContain("CanSwitchEditorSurface", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit shared Universe", sharedEntityIntegration, StringComparison.Ordinal);
        var explorer = ReadSource("src/MediaEngine.Web/Components/Pages/ChronicleExplorer.razor");
        Assert.Contains("MediaEditorLauncher.OpenAsync", explorer, StringComparison.Ordinal);
        Assert.Contains("EffectiveAdministrator", explorer, StringComparison.Ordinal);
        Assert.Contains("if (IsSharedEntityMode)", shell, StringComparison.Ordinal);
        Assert.Contains("<SharedEntityEditorWorkspace", shell, StringComparison.Ordinal);
        Assert.Contains("<AppTextarea", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("@page", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowAsync<SharedEntityEditorWorkspace>", shell, StringComparison.Ordinal);
        Assert.Contains("else if (_context is null && !string.IsNullOrWhiteSpace(_error))", workspace, StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessfulDetailsCommitIsPropagatedToShellAppliedResultState()
    {
        var commits = new List<SharedEntityEditorCommitKind>();
        var cut = Render<SharedEntityEditorWorkspace>(parameters => parameters
            .Add(component => component.InitialTarget, RootTarget())
            .Add(component => component.Committed, EventCallback.Factory.Create<SharedEntityEditorCommitKind>(this, commits.Add)));

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".see-field input")));
        cut.Find(".see-field input").Input("Chronicle World Revised");
        cut.Find(".see-primary").Click();

        cut.WaitForAssertion(() => Assert.Contains(SharedEntityEditorCommitKind.Details, commits));
        var shell = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor");
        var integration = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.SharedEntity.cs");
        var code = ReadSource("src/MediaEngine.Web/Components/MediaEditor/SharedMediaEditorShell.razor.cs");
        Assert.Contains("Committed=\"OnSharedEntityCommittedAsync\"", shell, StringComparison.Ordinal);
        Assert.Contains("_hasCommittedChanges = true", integration, StringComparison.Ordinal);
        Assert.Contains("if (_hasCommittedChanges)", code, StringComparison.Ordinal);
        Assert.Contains("Request.OnArtworkChanged.Invoke()", integration, StringComparison.Ordinal);
    }

    private static IEngineApiClient CreateApi() => EngineApiClientStub.Create(stub =>
    {
        stub.SetHandler(nameof(IEngineApiClient.GetSharedEntityEditorContextAsync), args =>
        {
            var target = (SharedEntityEditorTargetDto)args![0]!;
            var isRoot = string.Equals(target.Kind, SharedEntityEditorTargetKinds.Universe, StringComparison.OrdinalIgnoreCase);
            var category = GetCategory(target);
            return Task.FromResult<SharedEntityEditorContextDto?>(new(
                target,
                isRoot ? "Chronicle World" : target.Qid switch { "QORG1" => "Lantern Council", "QEVENT1" => "Night of Ash", "QLOC1" => "Old Harbor", _ => string.Equals(target.FictionalEntityId, SiblingId) ? "Elian" : "Mara Venn" },
                category,
                isRoot ? RootCapabilities() : EntityCapabilities(category),
                "available",
                isRoot ? ["Chronicle World"] : ["Chronicle World", target.Qid ?? "Mara Venn"]));
        });
        stub.SetHandler(nameof(IEngineApiClient.GetSharedEntityCategoriesAsync), _ => Task.FromResult<IReadOnlyList<SharedEntityCategorySummaryDto>>(
        [new("Character", "Character", 2), new("Location", "Places", 1), new("Organization", "Groups", 1), new("Event", "Event", 1), new("Object", "Object", 1)]));
        stub.SetHandler(nameof(IEngineApiClient.GetSharedEntitySelectorPageAsync), args =>
        {
            var category = (string?)args![1] ?? "Character";
            var offset = (int)args[3]!;
            IReadOnlyList<SharedEntitySelectorItemDto> all = category switch
            {
                "Location" => [new(CharacterId, "QLOC1", "Old Harbor", "Location", "A coastal district")],
                "Organization" => [new(CharacterId, "QORG1", "Lantern Council", "Organization", null)],
                "Event" => [new(SiblingId, "QEVENT1", "Night of Ash", "Event", null)],
                "Object" => [new(SiblingId, "QOBJ1", "Glass Compass", "Object", null)],
                _ => [new(CharacterId, "QCHAR1", "Mara Venn", "Character", "A traveler"), new(SiblingId, "QCHAR2", "Elian", "Character", null)],
            };
            var items = all.Skip(offset).Take(1).ToList();
            return Task.FromResult<SharedEntitySelectorPageDto?>(new(items, offset, 1, 2, offset + items.Count < 2));
        });
        stub.SetHandler(nameof(IEngineApiClient.GetSharedEntityDetailsAsync), args =>
        {
            var target = (SharedEntityEditorTargetDto)args![0]!;
            return Task.FromResult<SharedEntityDetailsDto?>(new(target.FictionalEntityId, target.Qid ?? "Q42", string.Equals(target.Kind, SharedEntityEditorTargetKinds.Universe, StringComparison.OrdinalIgnoreCase) ? "Chronicle World" : target.Qid switch { "QORG1" => "Lantern Council", "QEVENT1" => "Night of Ash", "QLOC1" => "Old Harbor", _ => "Mara Venn" }, GetCategory(target), "The shared world", target.UniverseQid, "Chronicle World"));
        });
        stub.SetHandler(nameof(IEngineApiClient.UpdateSharedEntityDetailsAsync), args =>
        {
            var target = (SharedEntityEditorTargetDto)args![0]!;
            var request = (SharedEntityDetailsUpdateRequest)args[1]!;
            return Task.FromResult<SharedEntityDetailsDto?>(new(target.FictionalEntityId, target.Qid ?? "Q42", request.label, GetCategory(target), request.description, target.UniverseQid, "Chronicle World"));
        });
        stub.SetHandler(nameof(IEngineApiClient.GetSharedEntityArtworkAsync), _ => Task.FromResult<IReadOnlyList<SharedEntityArtworkDto>>([]));
        stub.SetHandler(nameof(IEngineApiClient.GetSharedEntityEnrichmentAsync), _ => Task.FromResult<SharedEntityEnrichmentStatusDto?>(new("available", null, 10)));
        stub.SetHandler(nameof(IEngineApiClient.GetSharedEntityAppearancesAsync), _ => Task.FromResult<IReadOnlyList<SharedEntityAppearanceDto>>([]));
        stub.SetHandler(nameof(IEngineApiClient.GetSharedEntityRelationshipsAsync), _ => Task.FromResult<IReadOnlyList<SharedEntityRelationshipDto>>(
        [new("member-1", "QORG1", "member_of", "QPERSON1", "Wikidata", null, []), new("member-2", "QORG1", "has_parts", "QPERSON2", "Wikidata", null, []), new("unrelated-1", "QORG1", "related_to", "QWORK1", "Wikidata", null, []), new("participant-1", "QEVENT1", "participant", "QPERSON3", "Wikidata", null, []), new("unrelated-2", "QEVENT1", "located_in", "QPLACE1", "Wikidata", null, [])]));
        stub.SetHandler(nameof(IEngineApiClient.GetSharedEntityTimelineAsync), _ => Task.FromResult<IReadOnlyList<SharedEntityTimelineEntryDto>>([]));
        stub.SetHandler(nameof(IEngineApiClient.GetSharedEntitySourcesAsync), _ => Task.FromResult<IReadOnlyList<SharedEntitySourceDto>>([]));
        stub.SetHandler(nameof(IEngineApiClient.GetSharedEntityHistoryAsync), _ => Task.FromResult<IReadOnlyList<SharedEntityHistoryEntryDto>>([]));
    });

    private static SharedEntityEditorTargetDto RootTarget() => new(SharedEntityEditorTargetKinds.Universe, "Q42", null, "Q42");
    private static SharedEntityEditorTargetDto EntityTarget(Guid id, string qid) => new(SharedEntityEditorTargetKinds.FictionalEntity, "Q42", id, qid);
    private static string GetCategory(SharedEntityEditorTargetDto target) => target.Kind == SharedEntityEditorTargetKinds.Universe ? "Universe" : target.Qid switch { "QORG1" => "Organization", "QEVENT1" => "Event", "QLOC1" => "Location", "QOBJ1" => "Object", _ => "Character" };
    private static IReadOnlyList<SharedEntityEditorCapabilityDto> RootCapabilities() =>
    [new(SharedEntityEditorSections.Details, true, true), new(SharedEntityEditorSections.Artwork, true, true), new(SharedEntityEditorSections.Entities, true, false), new(SharedEntityEditorSections.Relationships, true, false), new(SharedEntityEditorSections.Timeline, true, false), new(SharedEntityEditorSections.Sources, true, false), new(SharedEntityEditorSections.History, true, false), new(SharedEntityEditorSections.Enrichment, true, false)];
    private static IReadOnlyList<SharedEntityEditorCapabilityDto> EntityCapabilities(string category)
    {
        var subjectSection = category switch
        {
            "Organization" => SharedEntityEditorSections.Members,
            "Event" => SharedEntityEditorSections.Participants,
            _ => SharedEntityEditorSections.Appearances,
        };
        var capabilities = new List<SharedEntityEditorCapabilityDto>
        {
            new(SharedEntityEditorSections.Details, true, true),
            new(SharedEntityEditorSections.Artwork, true, true),
            new(subjectSection, true, false),
            new(SharedEntityEditorSections.Relationships, true, false),
            new(SharedEntityEditorSections.Timeline, true, false),
            new(SharedEntityEditorSections.Sources, true, false),
            new(SharedEntityEditorSections.History, true, false),
            new(SharedEntityEditorSections.Enrichment, true, false),
            new("private", false, true),
        };
        if (category is "Organization" or "Event")
            capabilities.Insert(3, new(SharedEntityEditorSections.Appearances, true, false));
        return capabilities;
    }

    private void RecordDirty(bool dirty) => _dirtyChanges.Add(dirty);

    private static string ReadSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, relativePath));
    }
}
