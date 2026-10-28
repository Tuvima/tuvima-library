namespace MediaEngine.Identity.Contracts;

/// <summary>Whether first-run setup is still being walked through (the Engine answers from its stored setup progress).</summary>
public interface IFirstRunSetupState
{
    bool IsInProgress { get; }
}
