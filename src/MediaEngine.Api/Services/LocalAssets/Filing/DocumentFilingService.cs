using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.LocalAssets.Filing;

public enum DocumentFilingStatus
{
    /// <summary>Rules were run (and suggestions stored if the document is in the Inbox).</summary>
    Applied,

    /// <summary>The document is gone, in the bin, or not a document any more.</summary>
    Missing,
}

public sealed record DocumentFilingOutcome(DocumentFilingStatus Status, string Summary);

/// <summary>
/// Runs a document through the auto-filing rules and, when it is still waiting in the Inbox, works out suggestions
/// for it. Safe to run any number of times: rules only fill values nobody chose, and suggestions are recomputed from
/// the document's current words. Nothing here logs document text, only counts.
/// </summary>
public sealed class DocumentFilingService(
    IDocumentFilingRepository repository,
    ILogger<DocumentFilingService> logger,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<DocumentFilingOutcome> ProcessAsync(Guid itemId, CancellationToken ct)
    {
        var subject = await repository.GetSubjectAsync(itemId, ct).ConfigureAwait(false);
        if (subject is null)
        {
            return new DocumentFilingOutcome(DocumentFilingStatus.Missing, "The document is no longer there.");
        }

        var rules = await repository.GetApplicableRulesAsync(subject, ct).ConfigureAwait(false);
        var plan = DocumentRuleEngine.Plan(rules, subject);
        var outcome = await repository.ApplyPlanAsync(itemId, plan, ct).ConfigureAwait(false);
        var summary = $"{rules.Count} rule(s) checked" + (outcome.Changed ? ", document updated" : string.Empty)
            + (outcome.Filed && plan.FileNow ? ", filed" : string.Empty) + ".";

        if (!outcome.Filed)
        {
            var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
            var suggestions = await new DocumentSuggester(repository)
                .SuggestAsync(subject, today, DocumentDateOrder.Unspecified, ct).ConfigureAwait(false);
            await repository.SaveSuggestionsAsync(itemId, suggestions, ct).ConfigureAwait(false);
            logger.LogDebug(
                "Worked out Inbox suggestions for document {FileName}: correspondent {HasCorrespondent}, type {HasType}, {Tags} tag(s).",
                subject.FileName, suggestions.Correspondent is not null, suggestions.DocumentType is not null, suggestions.Tags?.Count ?? 0);
            summary += " Suggestions stored.";
        }

        return new DocumentFilingOutcome(DocumentFilingStatus.Applied, summary);
    }
}
