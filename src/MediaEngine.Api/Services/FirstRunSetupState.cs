using MediaEngine.Identity.Contracts;
using MediaEngine.Storage;

namespace MediaEngine.Api.Services;

/// <summary>Answers from the stored setup progress: setup counts as running until its last step is done.</summary>
public sealed class FirstRunSetupState(OnboardingRepository repository) : IFirstRunSetupState
{
    public bool IsInProgress => repository.Get().State != "complete";
}
