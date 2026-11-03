using MediaEngine.Contracts.Editing;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// Reads the Engine's follow-up answers for the items a save matched as the one line of named steps the saved ribbon shows (universal
/// editor v3, D7). Pure, so the rules can be tested without a page.
/// </summary>
public static class EditorFollowUpView
{
    /// <summary>How the steps of several items read as one: a step is failed if it failed on any item, running if it is running on any, waiting if any still waits, else done.</summary>
    public static IReadOnlyList<EditorFollowUpStepDto> Combine(IEnumerable<EditorFollowUpDto> followUps)
    {
        ArgumentNullException.ThrowIfNull(followUps);
        var steps = new List<EditorFollowUpStepDto>();
        foreach (var group in followUps.SelectMany(followUp => followUp.Steps).GroupBy(step => step.Key, StringComparer.Ordinal))
        {
            var states = group.Select(step => step.State).ToList();
            steps.Add(new EditorFollowUpStepDto
            {
                Key = group.Key,
                Label = group.First().Label,
                State = states.Contains(EditorFollowUpStates.Failed, StringComparer.Ordinal) ? EditorFollowUpStates.Failed
                    : states.Contains(EditorFollowUpStates.Running, StringComparer.Ordinal) ? EditorFollowUpStates.Running
                    : states.Contains(EditorFollowUpStates.Waiting, StringComparer.Ordinal) ? EditorFollowUpStates.Waiting
                    : EditorFollowUpStates.Done,
                Reason = group.FirstOrDefault(step => step.State == EditorFollowUpStates.Failed)?.Reason,
            });
        }

        return steps;
    }

    /// <summary>True when nothing more will happen: every step is done, or one failed.</summary>
    public static bool IsFinished(IReadOnlyList<EditorFollowUpStepDto> steps) =>
        steps.Count == 0
        || steps.Any(step => step.State == EditorFollowUpStates.Failed)
        || steps.All(step => step.State == EditorFollowUpStates.Done);

    /// <summary>The sentence under an amber step: why it stopped, and that the item is waiting in Needs Review.</summary>
    public static string? FailureLine(IReadOnlyList<EditorFollowUpStepDto> steps) =>
        steps.FirstOrDefault(step => step.State == EditorFollowUpStates.Failed) is { } failed
            ? $"{failed.Label}: {(string.IsNullOrWhiteSpace(failed.Reason) ? "it did not finish." : failed.Reason)}"
            : null;
}
