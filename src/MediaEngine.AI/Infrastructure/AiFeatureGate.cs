using MediaEngine.AI.Configuration;
using MediaEngine.Domain.Enums;

namespace MediaEngine.AI.Infrastructure;

public enum AiFeature
{
    SmartLabeling,
    TypeLogic,
    SeriesAlignment,
    VibeTags,
    Tldr,
    DescriptionIntelligence,
}

/// <summary>Central preflight gate used before scans, leases, or model loads.</summary>
public sealed class AiFeatureGate
{
    private readonly AiSettings _settings;
    private readonly ModelInventory _inventory;
    private readonly AiModelSelectionAdvisor _advisor;

    public AiFeatureGate(AiSettings settings, ModelInventory inventory, AiModelSelectionAdvisor advisor)
    {
        _settings = settings;
        _inventory = inventory;
        _advisor = advisor;
    }

    public bool IsEnabled(AiFeature feature) => feature switch
    {
        AiFeature.SmartLabeling => _settings.Features.SmartLabeling,
        AiFeature.TypeLogic => _settings.Features.TypeLogic,
        AiFeature.SeriesAlignment => _settings.Features.SeriesAlignment,
        AiFeature.VibeTags => _settings.Features.VibeTags,
        AiFeature.Tldr => _settings.Features.Tldr,
        AiFeature.DescriptionIntelligence => _settings.Features.DescriptionIntelligence,
        _ => false,
    };

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
