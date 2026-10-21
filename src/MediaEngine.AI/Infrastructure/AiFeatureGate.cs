using MediaEngine.AI.Configuration;
using MediaEngine.Domain.Enums;

namespace MediaEngine.AI.Infrastructure;

public enum AiFeature
{
    SmartLabeling,
    TypeLogic,
    SeriesAlignment,
    VibeTags,
    DescriptionIntelligence,
}

/// <summary>Central preflight gate used before scans, leases, or model loads.</summary>
public sealed class AiFeatureGate
{
    private readonly IAiFeatureSwitches _switches;
    private readonly ModelInventory _inventory;
    private readonly AiModelSelectionAdvisor _advisor;

    public AiFeatureGate(IAiFeatureSwitches switches, ModelInventory inventory, AiModelSelectionAdvisor advisor)
    {
        _switches = switches;
        _inventory = inventory;
        _advisor = advisor;
    }

    public bool IsEnabled(AiFeature feature) => _switches.IsOn(feature);

    public bool CanExecute(AiFeature feature, AiModelRole role)
    {
        if (!IsEnabled(feature))
        {
            return false;
        }

        // Work only runs when the model may be used here and the native runtime really loads.
        var decision = _advisor.GetDecision(role);
        if (!decision.CanEnable || !decision.RuntimeReady)
        {
            return false;
        }

        return _inventory.GetState(role) is AiModelState.Ready or AiModelState.Loaded;
    }
}
