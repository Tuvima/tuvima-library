using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Delete from library asks an administrator whose settings are locked for the administrator PIN before its "are you sure" question,
/// with the same prompt as Edit. Nothing goes on until the Engine says the settings are unlocked; closing the prompt stops quietly;
/// nobody else is ever asked. The Engine still decides on every delete.
/// </summary>
public sealed class DeletePinOnTapTests
{
    private static readonly Guid Id = Guid.NewGuid();

    private static async Task<DeviceContextService> DeviceAsync(string formFactor)
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(formFactor switch
        {
            "tv" => new DeviceContextSnapshot("television", "tv", false, FinePointer: true, Hover: true),
            "phone" => new DeviceContextSnapshot("mobile", "phone", true),
            _ => new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true),
        });
        return device;
    }

    private static DashboardAuthorityResponse Authority(bool administrator, bool unlocked, DateTimeOffset? expires = null) =>
        new(Id, Id, true, true, 1, 1, administrator, unlocked, expires, 1, [], [], []);

    private static DashboardSessionAccessor Session(bool administrator, bool unlocked, DateTimeOffset? expires = null)
    {
        var session = new DashboardSessionAccessor();
        session.Set("token", Id, Id, Guid.NewGuid(), Authority(administrator, unlocked, expires));
        return session;
    }

    [Fact]
    public async Task AnUnlockedAdministrator_GoesStraightToTheQuestion_WithoutBeingAsked()
    {
        var pin = new PinPrompt(null);

        var gate = await LibraryItemDeletionFlow.EnsureMayDeleteAsync(await DeviceAsync("desktop"), Session(true, true), pin);

        Assert.True(gate.Allowed);
        Assert.Equal(0, pin.Asked);
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("phone")]
    public async Task ALockedAdministrator_IsAskedForThePin_AndGoesOnOnceTheSettingsAreUnlocked(string formFactor)
    {
        var session = Session(true, false);
        var pin = new PinPrompt(session) { Enter = true };

        var gate = await LibraryItemDeletionFlow.EnsureMayDeleteAsync(await DeviceAsync(formFactor), session, pin);

        Assert.True(gate.Allowed);
        Assert.Equal(1, pin.Asked);
    }

    [Fact]
    public async Task ALockedAdministrator_WhoClosesThePrompt_IsStoppedQuietly()
    {
        var session = Session(true, false);
        var pin = new PinPrompt(session) { Enter = false };

        var gate = await LibraryItemDeletionFlow.EnsureMayDeleteAsync(await DeviceAsync("desktop"), session, pin);

        Assert.False(gate.Allowed);
        Assert.Null(gate.Message);
        Assert.Equal(1, pin.Asked);
    }

    [Fact]
    public async Task AnUnlockThatHasRunOut_CountsAsLocked()
    {
        var session = Session(true, true, DateTimeOffset.UtcNow.AddMinutes(-1));
        var pin = new PinPrompt(session) { Enter = false };

        var gate = await LibraryItemDeletionFlow.EnsureMayDeleteAsync(await DeviceAsync("desktop"), session, pin);

        Assert.False(gate.Allowed);
        Assert.Equal(1, pin.Asked);
    }

    [Fact]
    public async Task ThePromptSayingYes_IsNotEnough_WhenTheSettingsAreStillLocked()
    {
        var session = Session(true, false);
        var pin = new PinPrompt(null) { Enter = true };

        var gate = await LibraryItemDeletionFlow.EnsureMayDeleteAsync(await DeviceAsync("desktop"), session, pin);

        Assert.False(gate.Allowed);
    }

    [Fact]
    public async Task ALockedAdministrator_WithNoPromptAvailable_IsRefusedWithALine()
    {
        var gate = await LibraryItemDeletionFlow.EnsureMayDeleteAsync(await DeviceAsync("desktop"), Session(true, false), null);

        Assert.False(gate.Allowed);
        Assert.Equal(LibraryItemDeletionText.Forbidden, gate.Message);
    }

    [Fact]
    public async Task SomeoneWhoIsNotAnAdministrator_IsRefusedWithALine_AndNeverAsked()
    {
        var pin = new PinPrompt(null) { Enter = true };

        var member = await LibraryItemDeletionFlow.EnsureMayDeleteAsync(await DeviceAsync("desktop"), Session(false, false), pin);
        var nobody = await LibraryItemDeletionFlow.EnsureMayDeleteAsync(await DeviceAsync("desktop"), null, pin);

        Assert.False(member.Allowed);
        Assert.Equal(LibraryItemDeletionText.Forbidden, member.Message);
        Assert.False(nobody.Allowed);
        Assert.Equal(0, pin.Asked);
    }

    [Fact]
    public async Task OnATv_ALockedAdministrator_IsRefusedByTheDevice_AndNeverAsked()
    {
        var session = Session(true, false);
        var pin = new PinPrompt(session) { Enter = true };

        var gate = await LibraryItemDeletionFlow.EnsureMayDeleteAsync(await DeviceAsync("tv"), session, pin);

        Assert.False(gate.Allowed);
        Assert.Equal(EditorDeviceRule.ItemMessage, gate.Message);
        Assert.Equal(0, pin.Asked);
    }

    /// <summary>The PIN prompt, hand-written: counts how often it was shown and, when the PIN is "entered", unlocks the session it was given as the Engine would.</summary>
    private sealed class PinPrompt(DashboardSessionAccessor? session) : IAdministratorSurfaceAccessService
    {
        public bool Enter { get; set; }
        public int Asked { get; private set; }

        public Task<bool> EnsureUnlockedAsync(CancellationToken ct = default)
        {
            Asked++;
            if (Enter && session is not null)
            {
                session.Set("token", Id, Id, Guid.NewGuid(), Authority(true, true));
            }

            return Task.FromResult(Enter);
        }
    }
}
