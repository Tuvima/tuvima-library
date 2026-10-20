using MediaEngine.AI.Configuration;
using MediaEngine.AI.Infrastructure;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services.LocalAi;

/// <summary>
/// The machine's rules for starting opportunistic text-model work: after first-run setup, in a quiet period with no one
/// using the app, and when CPU, memory and transcoding pressure allow. Library background work is turned away when a rule
/// fails; plugin work waits until the rules pass.
/// </summary>
public interface IAiAdmissionRules
{
    /// <summary>Null when opportunistic work may start now; otherwise the short reason it may not.</summary>
    string? WhyNotNow(AiModelRole role);

    /// <summary>Raised when someone starts using the app. Running Library background work steps aside.</summary>
    event Action? InteractiveTrafficStarted;
}

/// <summary>The real rules, read from first-run state, recent app traffic and the resource monitor.</summary>
public sealed class AiAdmissionRules : IAiAdmissionRules, IDisposable
{
    private readonly AiSettings _settings;
    private readonly OnboardingActivationGate _onboarding;
    private readonly InteractiveRequestTracker _interactiveRequests;
    private readonly ResourceMonitorService _resources;
    private int _disposed;

    public AiAdmissionRules(
        AiSettings settings,
        OnboardingActivationGate onboarding,
        InteractiveRequestTracker interactiveRequests,
        ResourceMonitorService resources)
    {
        _settings = settings;
        _onboarding = onboarding;
        _interactiveRequests = interactiveRequests;
        _resources = resources;
        _interactiveRequests.RequestStarted += OnInteractiveRequestStarted;
    }

    public event Action? InteractiveTrafficStarted;

    public string? WhyNotNow(AiModelRole role)
    {
        if (!_onboarding.IsComplete)
        {
            return "onboarding-incomplete";
        }

        var quietPeriod = TimeSpan.FromSeconds(_settings.BackgroundQuietSeconds);
        if (_interactiveRequests.HasPressure(quietPeriod))
        {
            return "interactive-traffic";
        }

        var modelSize = _settings.Models.GetByRole(role).SizeMB;
        var recommendation = _resources.CanLoadModel(modelSize);
        return recommendation.CanLoad ? null : recommendation.Reason;
    }

    private void OnInteractiveRequestStarted() => InteractiveTrafficStarted?.Invoke();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _interactiveRequests.RequestStarted -= OnInteractiveRequestStarted;
        }
    }
}
