using Bunit;
using MediaEngine.Web.Components.MediaEditor;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class EditorDetailsInlineFieldTests : AsyncBunitContext
{
    public EditorDetailsInlineFieldTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Render<AppPopoverHost>();
    }

    [Fact]
    public async Task CancellingAnInlineFieldRestoresItsEditButtonAfterTheParentRenders()
    {
        var cut = Render<EditorDetailsInlineField>(parameters => parameters
            .Add(component => component.Field, TitleField())
            .Add(component => component.IsEditing, true)
            .Add(component => component.OnCancel, () => { }));
        Assert.Equal("field", cut.Find(".sme-details-row").GetAttribute("data-app-escape-owner"));
        await cut.Find("input").KeyDownAsync(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        cut.Render(parameters => parameters.Add(component => component.IsEditing, false));
        Assert.False(cut.Find(".sme-details-row").HasAttribute("data-app-escape-owner"));
        Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "Blazor._internal.domWrapper.focus");
    }

    [Fact]
    public void OneRowEditorUpdatesDraftAndEscapeCancelsWithoutSaving()
    {
        var field = TitleField();
        var draft = "Original title";
        var saved = false;
        var cancelled = false;

        var cut = Render<EditorDetailsInlineField>(parameters => parameters
            .Add(component => component.Field, field)
            .Add(component => component.IsEditing, true)
            .Add(component => component.DraftValue, draft)
            .Add(component => component.DraftValueChanged, EventCallback.Factory.Create<string?>(this, value => draft = value ?? string.Empty))
            .Add(component => component.OnSave, EventCallback.Factory.Create(this, () => saved = true))
            .Add(component => component.OnCancel, EventCallback.Factory.Create(this, () => cancelled = true)));

        Assert.Single(cut.FindAll(".sme-details-row"));
        cut.Find("input").Input("Unsaved title");
        cut.Find("input").KeyDown("Escape");

        Assert.Equal("Unsaved title", draft);
        Assert.True(cancelled);
        Assert.False(saved);
    }

    [Fact]
    public void EnterSavesOnlyTheActiveRow()
    {
        var saved = false;
        var cancelled = false;
        var cut = Render<EditorDetailsInlineField>(parameters => parameters
            .Add(component => component.Field, TitleField())
            .Add(component => component.IsEditing, true)
            .Add(component => component.DraftValue, "Draft")
            .Add(component => component.OnSave, EventCallback.Factory.Create(this, () => saved = true))
            .Add(component => component.OnCancel, EventCallback.Factory.Create(this, () => cancelled = true)));

        cut.Find("input").KeyDown("Enter");

        Assert.True(saved);
        Assert.False(cancelled);
    }

    [Fact]
    public void ContributorChipsShowTwoValuesAndCanExpandWithoutLosingNames()
    {
        var cut = Render<EditorDetailsInlineField>(parameters => parameters
            .Add(component => component.Field, new MediaEngine.Web.Services.Editing.MediaEditorDetailsFieldPresentation(
                "cast_member", "cast_member", "Cast", "Actor One; Actor Two; Actor Three", "People", "text",
                "Canonical", null, "TMDB", true, false, true, 2,
                "Actor One; Actor Two; Actor Three", "Actor One; Actor Two; Actor Three", true)));

        Assert.Equal(2, cut.FindAll(".sme-details-chip").Count);
        Assert.Equal("Show more", cut.Find(".sme-details-chip-more").TextContent.Trim());

        cut.Find(".sme-details-chip-more").Click();

        Assert.Equal(3, cut.FindAll(".sme-details-chip").Count);
        Assert.Contains("Actor Three", cut.Find(".sme-details-row").TextContent);
        Assert.Contains("Show less", cut.Find(".sme-details-row").TextContent);
    }

    [Fact]
    public void GenreChipsCollapseButEditingShowsEveryValue()
    {
        var field = new MediaEngine.Web.Services.Editing.MediaEditorDetailsFieldPresentation(
            "genre", "genre", "Genres", "Drama, Comedy, Adventure", "Category", "text",
            "Canonical", null, "Provider", true, false, true, 1,
            "Drama, Comedy, Adventure", "Drama, Comedy, Adventure", false);
        var cut = Render<EditorDetailsInlineField>(parameters => parameters.Add(component => component.Field, field));

        Assert.Equal(2, cut.FindAll(".sme-details-chip").Count);
        Assert.Equal("Show more", cut.Find(".sme-details-chip-more").TextContent.Trim());

        cut.Find(".sme-details-chip-more").Click();
        Assert.Equal(3, cut.FindAll(".sme-details-chip").Count);

        var editing = Render<EditorDetailsInlineField>(parameters => parameters
            .Add(component => component.Field, field)
            .Add(component => component.IsEditing, true)
            .Add(component => component.DraftValue, "Drama, Comedy, Adventure"));
        Assert.Equal(3, editing.FindAll(".sme-details-chip").Count);
        Assert.Empty(editing.FindAll(".sme-details-chip-more"));
    }

    [Fact]
    public void InheritedValueShowsItsSourceWithoutChildEditOrRevertActions()
    {
        var field = new MediaEngine.Web.Services.Editing.MediaEditorDetailsFieldPresentation(
            "genre", "genre", "Genre", "Inherited genre", "Category", "text", "Inherited", "Series", null,
            true, true, false, 1, "Inherited genre", "Inherited genre", false, "series");
        var cut = Render<EditorDetailsInlineField>(parameters => parameters.Add(component => component.Field, field));

        Assert.Contains("From Series", cut.Find(".sme-details-row").TextContent);
        Assert.Empty(cut.FindAll(".sme-details-row__provenance .app-icon-button"));

        var guardedEdit = Render<EditorDetailsInlineField>(parameters => parameters
            .Add(component => component.Field, field)
            .Add(component => component.IsEditing, true)
            .Add(component => component.DraftValue, "Child draft"));
        Assert.Empty(guardedEdit.FindAll("input"));
        Assert.DoesNotContain("Save this field", guardedEdit.Find(".sme-details-row").TextContent);
    }

    [Fact]
    public void ServerProjectedLinkedValueUsesItsRouteAndNavigationCallback()
    {
        string? navigatedTo = null;
        var field = new MediaEngine.Web.Services.Editing.MediaEditorDetailsFieldPresentation(
            "director", "director", "Director", "A Director", "Person", "text", "Canonical", null, "TMDB",
            true, false, true, 2, "A Director", "A Director", true,
            LinkedValues: [new MediaEngine.Web.Services.Editing.MediaEditorDetailsLinkedValue("A Director", "/details/person/verified")]);
        var cut = Render<EditorDetailsInlineField>(parameters => parameters
            .Add(component => component.Field, field)
            .Add(component => component.OnNavigate, EventCallback.Factory.Create<string>(this, location => navigatedTo = location)));

        var link = cut.Find("a.sme-details-linked-chip");
        Assert.Equal("/details/person/verified", link.GetAttribute("href"));
        link.Click();

        Assert.Equal("/details/person/verified", navigatedTo);

        navigatedTo = null;
        var guarded = Render<EditorDetailsInlineField>(parameters => parameters
            .Add(component => component.Field, field)
            .Add(component => component.HasOtherDraft, true)
            .Add(component => component.OnNavigate, EventCallback.Factory.Create<string>(this, location => navigatedTo = location)));
        guarded.Find("a.sme-details-linked-chip").Click();
        Assert.Null(navigatedTo);
    }

    [Fact]
    public void UnresolvedLinkedIdentityRemainsPlainText()
    {
        var field = new MediaEngine.Web.Services.Editing.MediaEditorDetailsFieldPresentation(
            "director", "director", "Director", "Unknown Director", "Person", "text", "Canonical", null, "TMDB",
            true, false, true, 2, "Unknown Director", "Unknown Director", true,
            LinkedValues: [new MediaEngine.Web.Services.Editing.MediaEditorDetailsLinkedValue("Unknown Director", null)]);
        var cut = Render<EditorDetailsInlineField>(parameters => parameters.Add(component => component.Field, field));

        Assert.Empty(cut.FindAll("a.sme-details-linked-chip"));
        Assert.Contains("Unknown Director", cut.Find(".sme-details-row").TextContent);
    }

    [Fact]
    public void StructuredContributorLinksPreserveDuplicateLabelsOrderAndPunctuation()
    {
        var field = new MediaEngine.Web.Services.Editing.MediaEditorDetailsFieldPresentation(
            "cast_member", "cast_member", "Cast", "Same Name; Same Name; Last; First", "People", "text",
            "Canonical", null, "TMDB", true, false, true, 2,
            "Same Name; Same Name; Last; First", "Same Name; Same Name; Last; First", true,
            LinkedValues:
            [
                new("Same Name", "/details/person/first-id"),
                new("Same Name", "/details/person/second-id"),
                new("Last; First", null),
            ]);
        var cut = Render<EditorDetailsInlineField>(parameters => parameters.Add(component => component.Field, field));

        Assert.Equal(new string?[] { "/details/person/first-id", "/details/person/second-id" }, cut.FindAll("a.sme-details-linked-chip").Select(link => link.GetAttribute("href")));
        cut.Find(".sme-details-chip-more").Click();

        Assert.Equal(
            new string?[] { "/details/person/first-id", "/details/person/second-id" },
            cut.FindAll("a.sme-details-linked-chip").Select(link => link.GetAttribute("href")));
        Assert.Contains("Last; First", cut.Find(".sme-details-chip-list").TextContent);
    }

    [Fact]
    public void StructuredOrganizationArrayCollapsesInOrderWithoutInventingLinks()
    {
        var field = new MediaEngine.Web.Services.Editing.MediaEditorDetailsFieldPresentation(
            "production_company", "production_company", "Production company", "Acme, Inc., North Studio, Final Films", "Business", "text",
            "Canonical", null, "TMDB", false, false, true, 2,
            "Acme, Inc., North Studio, Final Films", "Acme, Inc., North Studio, Final Films", false,
            LinkedValues:
            [
                new("Acme, Inc.", null),
                new("North Studio", null),
                new("Final Films", null),
            ]);
        var cut = Render<EditorDetailsInlineField>(parameters => parameters.Add(component => component.Field, field));

        Assert.Equal(new[] { "Acme, Inc.", "North Studio" }, cut.FindAll(".sme-details-chip").Select(chip => chip.TextContent));
        Assert.Empty(cut.FindAll("a.sme-details-linked-chip"));
        cut.Find(".sme-details-chip-more").Click();
        Assert.Equal(new[] { "Acme, Inc.", "North Studio", "Final Films" }, cut.FindAll(".sme-details-chip").Select(chip => chip.TextContent));
        Assert.Empty(cut.FindAll("a.sme-details-linked-chip"));
    }

    private static MediaEngine.Web.Services.Editing.MediaEditorDetailsFieldPresentation TitleField() => new(
        "title", "title", "Title", "Original title", "Title", "text", "Canonical", null, "TMDB",
        CanOverride: true, CanRevert: false, IsChips: false, Tier: 1,
        RawValue: "Original title", DisplayValue: "Original title", IsUserLocked: true);
}
