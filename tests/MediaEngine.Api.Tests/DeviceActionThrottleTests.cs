using MediaEngine.Api.Security;

namespace MediaEngine.Api.Tests;

public sealed class DeviceActionThrottleTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    [Fact]
    public void ADevice_GetsThirtyActionsAMinute_ThenWaitsForTheOldestToAgeOut()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var throttle = new DeviceActionThrottle(clock);
        var account = Guid.NewGuid();
        var device = Guid.NewGuid();

        for (var call = 0; call < DeviceActionThrottle.PermitLimit; call++)
        {
            Assert.True(throttle.TryAcquire(account, device, out _));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.False(throttle.TryAcquire(account, device, out var retryAfter));
        Assert.InRange(retryAfter, 1, 60);
        Assert.True(throttle.TryAcquire(Guid.NewGuid(), Guid.NewGuid(), out _));

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.True(throttle.TryAcquire(account, device, out _));
    }

    [Fact]
    public void IdleDevices_AreForgotten_WhenTheTableGrows()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var throttle = new DeviceActionThrottle(clock);
        for (var index = 0; index < 400; index++)
        {
            Assert.True(throttle.TryAcquire(Guid.NewGuid(), Guid.NewGuid(), out _));
        }

        Assert.Equal(400, throttle.TrackedDevices);
        clock.Advance(DeviceActionThrottle.Window + TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryAcquire(Guid.NewGuid(), Guid.NewGuid(), out _));

        Assert.Equal(1, throttle.TrackedDevices);
    }

    [Fact]
    public void TheTable_NeverGrowsPastItsBound_AndFailsClosedForNewcomers()
    {
        var throttle = new DeviceActionThrottle(new Clock(DateTimeOffset.UtcNow));
        for (var index = 0; index < DeviceActionThrottle.MaxTrackedDevices; index++)
        {
            Assert.True(throttle.TryAcquire(Guid.NewGuid(), Guid.NewGuid(), out _));
        }

        Assert.False(throttle.TryAcquire(Guid.NewGuid(), Guid.NewGuid(), out var retryAfter));
        Assert.Equal(DeviceActionThrottle.MaxTrackedDevices, throttle.TrackedDevices);
        Assert.True(retryAfter > 0);
    }

    [Fact]
    public void OneAccount_CannotFillTheTable_ItsNewestDeviceIsRefused()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var throttle = new DeviceActionThrottle(clock);
        var household = Guid.NewGuid();
        var first = Guid.NewGuid();
        Assert.True(throttle.TryAcquire(household, first, out _));
        for (var index = 1; index < DeviceActionThrottle.MaxTrackedDevicesPerAccount; index++)
        {
            Assert.True(throttle.TryAcquire(household, Guid.NewGuid(), out _));
        }

        // The 65th device of this account is refused (the same answer as being over the allowance)...
        Assert.False(throttle.TryAcquire(household, Guid.NewGuid(), out var retryAfter));
        Assert.True(retryAfter > 0);
        Assert.Equal(DeviceActionThrottle.MaxTrackedDevicesPerAccount, throttle.TrackedDevices);

        // ...devices already tracked and every other account carry on...
        Assert.True(throttle.TryAcquire(household, first, out _));
        Assert.True(throttle.TryAcquire(Guid.NewGuid(), Guid.NewGuid(), out _));

        // ...and once the others have been idle a whole window there is room again.
        clock.Advance(DeviceActionThrottle.Window + TimeSpan.FromSeconds(1));
        Assert.True(throttle.TryAcquire(household, Guid.NewGuid(), out _));
    }
}
