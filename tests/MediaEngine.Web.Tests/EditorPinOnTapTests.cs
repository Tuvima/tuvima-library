using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Edit is offered to an administrator whose settings are locked, and asks for the administrator PIN on tap. Nothing opens until the
/// Engine says the profile may edit; closing the prompt stops quietly; nobody else is ever asked. The Engine still checks every save.
/// </summary>
public sealed class EditorPinOnTapTests
{
    private static async Task<DeviceContextService> DeviceAsync(string formFactor)
    {
        var device = new DeviceContextService(EngineApiClientStub.CreateDefault());
        await device.InitialiseAsync(formFactor switch
        {
            "tv" => new DeviceContextSnapshot("television", "tv", false, FinePointer: true, Hover: true),
            "phone" => new DeviceContextSnapshot("mobile", "phone", true),
            "tablet" => new DeviceContextSnapshot("web", "tablet", true),
            _ => new DeviceContextSnapshot("web", "desktop", false, FinePointer: true, Hover: true),
        });
        return device;
    }

    private static DashboardSessionAccessor LockedAdministrator() => TestSessions.For(kids: false, administrator: true, canManageMetadata: false);

    [Fact]
    public void Edit_IsOffered_ToAnUnlockedAdministratorAndALockedOne_AndToNobodyElse()
    {
        Assert.True(TestSessions.For(kids: false, administrator: true).CanOfferMetadataEdit);
        Assert.True(LockedAdministrator().CanOfferMetadataEdit);
        Assert.False(LockedAdministrator().CanManageMetadata);
        Assert.False(TestSessions.For(kids: false, administrator: false).CanOfferMetadataEdit);
        Assert.False(TestSessions.For(kids: true, administrator: false).CanOfferMetadataEdit);
        Assert.False(new DashboardSessionAccessor().CanOfferMetadataEdit);
    }

    [Fact]
    public async Task AnUnlockedAdministrator_Passes_WithoutBeingAsked()
    {
        var pin = new PinPrompt(null);

        var gate = await EditorDeviceRule.EnsureMayEditItemsAsync(await DeviceAsync("desktop"), TestSessions.For(kids: false, administrator: true), pin);

        Assert.True(gate.Allowed);
        Assert.Equal(0, pin.Asked);
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("tablet")]
    [InlineData("phone")]
    public async Task ALockedAdministrator_IsAsked_AndPassesOnceTheEngineSaysYes(string formFactor)
    {
        var session = LockedAdministrator();
        var pin = new PinPrompt(session) { Enter = true };

        var gate = await EditorDeviceRule.EnsureMayEditItemsAsync(await DeviceAsync(formFactor), session, pin);

        Assert.True(gate.Allowed);
        Assert.Equal(1, pin.Asked);
    }

    [Fact]
    public async Task ALockedAdministrator_WhoClosesThePrompt_IsStoppedQuietly()
    {
        var session = LockedAdministrator();
        var pin = new PinPrompt(session) { Enter = false };

        var gate = await EditorDeviceRule.EnsureMayEditItemsAsync(await DeviceAsync("desktop"), session, pin);

        Assert.False(gate.Allowed);
        Assert.Null(gate.Message);
        Assert.Equal(1, pin.Asked);
    }

    [Fact]
    public async Task ThePromptSayingYes_IsNotEnough_WhenTheEngineStillSaysTheProfileMayNotEdit()
    {
        var session = LockedAdministrator();
        var pin = new PinPrompt(null) { Enter = true };

        var gate = await EditorDeviceRule.EnsureMayEditItemsAsync(await DeviceAsync("desktop"), session, pin);

        Assert.False(gate.Allowed);
        Assert.False(await EditorDeviceRule.EnsureUnlockedForItemAsync(session, pin));
    }

    [Fact]
    public async Task ALockedAdministrator_WithNoPromptAvailable_IsRefused()
    {
        var gate = await EditorDeviceRule.EnsureMayEditItemsAsync(await DeviceAsync("desktop"), LockedAdministrator(), null);

        Assert.False(gate.Allowed);
        Assert.Equal(EditorDeviceRule.ProfileMessage, gate.Message);
        Assert.False(await EditorDeviceRule.EnsureUnlockedForItemAsync(LockedAdministrator(), null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProfileThatIsNotAnAdministrator_IsRefusedWithALine_AndNeverAsked(bool kids)
    {
        var pin = new PinPrompt(null) { Enter = true };

        var gate = await EditorDeviceRule.EnsureMayEditItemsAsync(await DeviceAsync("desktop"), TestSessions.For(kids, administrator: false), pin);

        Assert.False(gate.Allowed);
        Assert.Equal(EditorDeviceRule.ProfileMessage, gate.Message);
        Assert.Equal(0, pin.Asked);
    }

    [Fact]
    public async Task NoSignedInProfile_IsRefused_AndNeverAsked()
    {
        var pin = new PinPrompt(null) { Enter = true };

        Assert.False((await EditorDeviceRule.EnsureMayEditItemsAsync(await DeviceAsync("desktop"), null, pin)).Allowed);
        Assert.False((await EditorDeviceRule.EnsureMayEditItemsAsync(await DeviceAsync("desktop"), new DashboardSessionAccessor(), pin)).Allowed);
        Assert.Equal(0, pin.Asked);
    }

    [Fact]
    public async Task OnATv_ALockedAdministrator_IsRefusedByTheDevice_AndNeverAsked()
    {
        var session = LockedAdministrator();
        var pin = new PinPrompt(session) { Enter = true };

        var gate = await EditorDeviceRule.EnsureMayEditItemsAsync(await DeviceAsync("tv"), session, pin);

        Assert.False(gate.Allowed);
        Assert.Equal(EditorDeviceRule.ItemMessage, gate.Message);
        Assert.Equal(0, pin.Asked);
        Assert.False(EditorDeviceRule.CanEditItems(await DeviceAsync("tv"), session));
    }

    [Fact]
    public async Task ForAnItemTheEngineAnsweredFor_OnlyALockedAdministratorIsAsked()
    {
        var pin = new PinPrompt(null) { Enter = false };

        // The Engine's per-item answer already decided for these; the prompt is not theirs.
        Assert.True(await EditorDeviceRule.EnsureUnlockedForItemAsync(null, pin));
        Assert.True(await EditorDeviceRule.EnsureUnlockedForItemAsync(TestSessions.For(kids: false, administrator: true), pin));
        Assert.True(await EditorDeviceRule.EnsureUnlockedForItemAsync(TestSessions.For(kids: false, administrator: false), pin));
        Assert.Equal(0, pin.Asked);

        Assert.False(await EditorDeviceRule.EnsureUnlockedForItemAsync(LockedAdministrator(), pin));
        Assert.Equal(1, pin.Asked);

        var session = LockedAdministrator();
        Assert.True(await EditorDeviceRule.EnsureUnlockedForItemAsync(session, new PinPrompt(session) { Enter = true }));
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
                session.Set("token", TestSessions.Account, TestSessions.Profile, Guid.NewGuid(), TestSessions.For(kids: false, administrator: true).Authority!);
            }

            return Task.FromResult(Enter);
        }
    }
}
