using MediaEngine.Contracts.Setup;

namespace MediaEngine.Web.Components.Setup;

/// <summary>
/// How the optional Vault step sits in first-run setup (setup fixes D3). Once it has been offered to a person it stays in the step list for the
/// rest of setup, so Back and the step list reach it again: after Not now it shows the offer again, and after it was turned on it shows
/// "Your Vault is on" and nothing to decide. A person it is never offered to (see <see cref="SetupVaultEligibility"/>) never sees it.
/// </summary>
internal static class SetupVaultStepRules
{
    public const string Key = "vault";

    /// <summary>The steps Back and Continue walk: the Vault is among them whenever it was offered, whatever was answered.</summary>
    public static string[] NavigableSteps(bool vaultOffered) =>
        SetupWorkflow.StepKeys.Where(key => key != Key || vaultOffered).ToArray();

    /// <summary>The step before <paramref name="current"/> in the walk, or the first step when there is none.</summary>
    public static string Previous(string current, bool vaultOffered)
    {
        var steps = NavigableSteps(vaultOffered);
        var index = Math.Max(0, Array.IndexOf(steps, current));
        return steps[Math.Max(0, index - 1)];
    }

    /// <summary>
    /// True when the step opens on "Your Vault is on" rather than the offer: it was passed, or the Vault is already on for this person
    /// (turned on a moment ago and the person came back before pressing Continue). A skipped step is not "on".
    /// </summary>
    public static bool IsAlreadyOn(string recordedState, bool vaultAvailable) => recordedState == "passed" || vaultAvailable;

    /// <summary>
    /// True when leaving the step should record an answer. Continue from a revisited step records nothing; only a real change does
    /// (Not now after Not now, or Continue after it was already passed, is not a change).
    /// </summary>
    public static bool NeedsDecision(string recordedState, bool turnedOn) => recordedState != (turnedOn ? "passed" : "deferred");

    /// <summary>True once the answer is on record, so the person must be found again after a reload.</summary>
    public static bool WasAnswered(string recordedState) => recordedState is "passed" or "deferred";
}
