using MediaEngine.AI.Infrastructure;

namespace MediaEngine.AI.Tests;

/// <summary>Switches a test flips by hand: a feature is on exactly when it was added.</summary>
internal sealed class FakeAiFeatureSwitches : IAiFeatureSwitches
{
    private readonly HashSet<AiFeature> _on = [];

    public FakeAiFeatureSwitches(params AiFeature[] on)
    {
        foreach (var feature in on)
        {
            _on.Add(feature);
        }
    }

    public bool AnyOn => _on.Count > 0;

    public bool IsOn(AiFeature feature) => _on.Contains(feature);

    public void Set(AiFeature feature, bool on)
    {
        if (on)
        {
            _on.Add(feature);
        }
        else
        {
            _on.Remove(feature);
        }
    }
}
