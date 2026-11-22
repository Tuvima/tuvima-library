using System.Reflection;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;

namespace MediaEngine.Identity.Tests;

/// <summary>
/// The away-from-home switch as the Engine reads it (spec remote-access D6): only a known way in counts as on; a missing, blank or
/// unknown connection mode is off; the answer is kept for a few seconds and cleared the moment settings are saved.
/// </summary>
public sealed class ConfigurationOutsideAccessPolicyTests
{
    private readonly ManualClock _clock = new();
    private readonly IConfigurationLoader _loader = DispatchProxy.Create<IConfigurationLoader, NetworkLoader>();

    private NetworkLoader Network => (NetworkLoader)(object)_loader;

    [Theory]
    [InlineData(NetworkConnectionModes.Tailscale, true)]
    [InlineData(NetworkConnectionModes.DirectOnly, true)]
    [InlineData(NetworkConnectionModes.Custom, true)]
    [InlineData("  Tailscale ", true)]
    [InlineData(NetworkConnectionModes.LocalOnly, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("carrier-pigeon", false)]
    [InlineData("remote", false)]
    public void OnlyAKnownWayIn_CountsAsOn(string? mode, bool expected)
    {
        Network.Mode = mode;

        Assert.Equal(expected, new ConfigurationOutsideAccessPolicy(_loader, _clock).IsOutsideAccessOn());
    }

    [Fact]
    public void TheAnswer_IsKeptForAFewSeconds_ThenReadAgain()
    {
        Network.Mode = NetworkConnectionModes.Tailscale;
        var policy = new ConfigurationOutsideAccessPolicy(_loader, _clock);
        Assert.True(policy.IsOutsideAccessOn());

        Network.Mode = NetworkConnectionModes.LocalOnly; // changed behind the policy's back
        _clock.Advance(TimeSpan.FromSeconds(1));
        for (var request = 0; request < 50; request++)
        {
            Assert.True(policy.IsOutsideAccessOn()); // still the kept answer
        }

        Assert.Equal(1, Network.Reads); // outside requests did not read the settings each time

        _clock.Advance(ConfigurationOutsideAccessPolicy.CacheLifetime);
        Assert.False(policy.IsOutsideAccessOn());
        Assert.Equal(2, Network.Reads);
    }

    [Fact]
    public void Invalidating_AppliesASavedChangeAtOnce()
    {
        Network.Mode = NetworkConnectionModes.Tailscale;
        var policy = new ConfigurationOutsideAccessPolicy(_loader, _clock);
        Assert.True(policy.IsOutsideAccessOn());

        Network.Mode = NetworkConnectionModes.LocalOnly;
        policy.Invalidate();

        Assert.False(policy.IsOutsideAccessOn());
    }

    [Fact]
    public void TheCacheLifetime_IsAboutFiveSeconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ConfigurationOutsideAccessPolicy.CacheLifetime);
    }

    /// <summary>Answers only <c>LoadNetwork</c>, with whatever connection mode the test set (the real loader refuses to hold an unknown one).</summary>
    public class NetworkLoader : DispatchProxy
    {
        public string? Mode { get; set; }

        public int Reads { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(IConfigurationLoader.LoadNetwork))
            {
                throw new NotSupportedException($"{targetMethod?.Name} is not part of this test.");
            }

            Reads++;
            var settings = new NetworkSettings();
            settings.Remote.ConnectionMode = Mode!;
            return settings;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _milliseconds;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _milliseconds;

        public void Advance(TimeSpan by) => _milliseconds += (long)by.TotalMilliseconds + 1;
    }
}
