using Bunit;
using MediaEngine.Web.Components.Profiles;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

/// <summary>The profile editor sheet: avatar choices, Kids switch, PIN and delete, reported through callbacks.</summary>
public sealed class ProfileEditorSheetTests
{
    private static readonly object Owner = new();

    private static ProfileViewModel Existing(bool hasPin = false, bool kids = false, string? icon = null, string? image = null) => new(
        Guid.NewGuid(), "Maya", "#3B82F6", kids ? "RestrictedProfile" : "StandardUser", DateTimeOffset.UtcNow,
        AvatarImageUrl: image, HasPin: hasPin, AvatarIcon: icon);

    private static IRenderedComponent<ProfileEditorSheet> Render(
        BunitContext ctx,
        ProfileViewModel? profile = null,
        Action<ProfileEditorResult>? saved = null,
        Action? deleted = null,
        Action? closed = null,
        bool canDelete = true,
        bool saving = false,
        string? error = null)
    {
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        return ctx.Render<ProfileEditorSheet>(parameters => parameters
            .Add(p => p.Open, true)
            .Add(p => p.Profile, profile)
            .Add(p => p.CanDelete, canDelete)
            .Add(p => p.Saving, saving)
            .Add(p => p.Error, error)
            .Add(p => p.OnSave, EventCallback.Factory.Create<ProfileEditorResult>(Owner, result => saved?.Invoke(result)))
            .Add(p => p.OnDelete, EventCallback.Factory.Create(Owner, () => deleted?.Invoke()))
            .Add(p => p.OnClose, EventCallback.Factory.Create(Owner, () => closed?.Invoke())));
    }

    private static AngleSharp.Dom.IElement ButtonWithText(IRenderedComponent<ProfileEditorSheet> cut, string text) =>
        cut.FindAll("button").First(button => button.TextContent.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void Adding_StartsEmpty_AndCannotBeSavedWithoutAName()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx);

        Assert.Contains("Add profile", cut.Find("#profile-editor-title").TextContent);
        Assert.True(cut.Find("button.who-button--primary").HasAttribute("disabled"));
        Assert.Empty(cut.FindAll("button.who-button--danger"));

        cut.Find("input.profile-editor__name").Input("Sam");

        Assert.False(cut.Find("button.who-button--primary").HasAttribute("disabled"));
    }

    [Fact]
    public void Adding_ReportsTheNameSwatchIconAndKidsChoice()
    {
        using var ctx = new BunitContext();
        ProfileEditorResult? result = null;
        var cut = Render(ctx, saved: r => result = r);

        cut.Find("input.profile-editor__name").Input("  Sam ");
        cut.Find("button[aria-label='Pink']").Click();
        cut.Find("button[aria-label='fox']").Click();
        cut.Find("input[aria-label='Kids profile']").Change(true);
        cut.Find("button.who-button--primary").Click();

        Assert.NotNull(result);
        Assert.Equal("Sam", result!.DisplayName);
        Assert.Equal("#EC4899", result.Color);
        Assert.Equal("fox", result.Icon);
        Assert.True(result.IsChild);
        Assert.Null(result.NewPin);
        Assert.False(result.RemovePin);
        Assert.Null(result.NewPhoto);
    }

    [Fact]
    public void TheEditor_OffersTwelveSwatchesTwelveIconsAndTheInitial()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx);

        Assert.Equal(12, cut.FindAll("button.avatar-picker__option--swatch").Count);
        Assert.Equal(13, cut.FindAll("[aria-label='Avatar icon'] button[role='radio']").Count);
        Assert.Empty(cut.FindAll("input[type='text'][aria-label='Avatar color']"));
    }

    [Fact]
    public void Editing_FillsInTheProfile_AndMarksTheCurrentChoices()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, Existing(icon: "owl"));

        Assert.Equal("Maya", cut.Find("input.profile-editor__name").GetAttribute("value"));
        Assert.Equal("true", cut.Find("button[aria-label='Blue']").GetAttribute("aria-checked"));
        Assert.Equal("true", cut.Find("button[aria-label='owl']").GetAttribute("aria-checked"));
        Assert.True(cut.Find("input[aria-label='Kids profile']").HasAttribute("disabled"));
    }

    [Fact]
    public void PickingAnIcon_DropsASavedPhoto()
    {
        using var ctx = new BunitContext();
        ProfileEditorResult? result = null;
        var cut = Render(ctx, Existing(image: "/profiles/1/avatar"), saved: r => result = r);

        Assert.Contains("Remove photo", cut.Markup);
        cut.Find("button[aria-label='cat']").Click();
        cut.Find("button.who-button--primary").Click();

        Assert.NotNull(result);
        Assert.True(result!.RemovePhoto);
        Assert.Equal("cat", result.Icon);
    }

    [Fact]
    public void Pin_CanBeSetFromTheNumberPad_AndOnlyWhenLongEnough()
    {
        using var ctx = new BunitContext();
        ProfileEditorResult? result = null;
        var cut = Render(ctx, Existing(), saved: r => result = r);

        ButtonWithText(cut, "Set a PIN").Click();
        foreach (var digit in "123")
        {
            cut.Find($"button.pin-pad__key[aria-label='{digit}']").Click();
        }

        Assert.True(cut.Find("button.who-button--primary").HasAttribute("disabled"));

        cut.Find("button.pin-pad__key[aria-label='4']").Click();
        cut.Find("button.who-button--primary").Click();

        Assert.Equal("1234", result!.NewPin);
    }

    [Fact]
    public void Pin_CanBeRemoved()
    {
        using var ctx = new BunitContext();
        ProfileEditorResult? result = null;
        var cut = Render(ctx, Existing(hasPin: true), saved: r => result = r);

        ButtonWithText(cut, "Remove PIN").Click();
        cut.Find("button.who-button--primary").Click();

        Assert.True(result!.RemovePin);
        Assert.Null(result.NewPin);
    }

    [Fact]
    public void Delete_AsksBeforeItDeletes()
    {
        using var ctx = new BunitContext();
        var deletes = 0;
        var cut = Render(ctx, Existing(), deleted: () => deletes++);

        cut.Find("button.profile-editor__delete").Click();
        Assert.Equal(0, deletes);
        Assert.Contains("Delete Maya?", cut.Markup);

        cut.Find(".profile-editor__confirm button.who-button--danger").Click();
        Assert.Equal(1, deletes);
    }

    [Fact]
    public void Delete_IsHiddenWhenThePageDoesNotAllowIt()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, Existing(), canDelete: false);

        Assert.Empty(cut.FindAll("button.profile-editor__delete"));
    }

    [Fact]
    public void WhileSaving_NothingCanBeSentTwice_AndAFailureIsShown()
    {
        using var ctx = new BunitContext();
        var cut = Render(ctx, Existing(), saving: true, error: "That name is taken.");

        Assert.True(cut.Find("button.who-button--primary").HasAttribute("disabled"));
        Assert.Contains("That name is taken.", cut.Find("[role='alert']").TextContent);
    }

    [Fact]
    public void EscapeAndTheBackdrop_CloseTheSheet()
    {
        using var ctx = new BunitContext();
        var closes = 0;
        var cut = Render(ctx, closed: () => closes++);

        cut.Find(".profile-editor").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        cut.Find(".profile-editor-backdrop").Click();

        Assert.Equal(2, closes);
    }

    [Fact]
    public void AvatarIcons_AreDrawnOnTheChosenColour()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<ProfileAvatar>(parameters => parameters
            .Add(p => p.DisplayName, "Maya")
            .Add(p => p.Color, "#3B82F6")
            .Add(p => p.Icon, "rocket"));

        Assert.Contains("#3B82F6", cut.Markup);
        Assert.Contains("<path", cut.Markup);
        Assert.DoesNotContain("<text", cut.Markup);
    }

    [Fact]
    public void AvatarWithoutAnIcon_ShowsTheInitial_AndAnUnknownIconIsIgnored()
    {
        using var ctx = new BunitContext();
        var cut = ctx.Render<ProfileAvatar>(parameters => parameters
            .Add(p => p.DisplayName, "Maya")
            .Add(p => p.Icon, "nope"));

        Assert.Contains(">M<", cut.Markup);
    }

    [Fact]
    public void ThePalette_HasTwelveDistinctValidColours()
    {
        Assert.Equal(12, ProfileAvatarPalette.Swatches.Count);
        Assert.Equal(12, ProfileAvatarPalette.Swatches.Select(s => s.Hex.ToUpperInvariant()).Distinct().Count());
        Assert.All(ProfileAvatarPalette.Swatches, s => Assert.Matches("^#[0-9A-Fa-f]{6}$", s.Hex));
    }
}
