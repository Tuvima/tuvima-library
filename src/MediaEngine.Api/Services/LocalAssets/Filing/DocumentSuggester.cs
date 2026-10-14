using System.Text;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Services.LocalAssets.Filing;

/// <summary>
/// Works out what a document waiting in the Inbox probably is, without any AI: its date and amount come from its
/// words, and its correspondent, type and tags come from earlier documents in the same library that share its rarest
/// words. A name is only suggested when at least two of the five most alike filed documents agree on it.
/// </summary>
public sealed class DocumentSuggester(IDocumentSuggestionCorpus corpus)
{
    /// <summary>How many different words of the document are checked for rarity.</summary>
    public const int CandidateWords = 120;

    /// <summary>How many of the rarest shared words are used to find alike documents.</summary>
    public const int RareWords = 10;

    public const int AlikeDocuments = 5;
    public const int MinimumAgreement = 2;
    public const int MaximumSuggestedTags = 5;

    private const int MinimumWordLength = 4;
    private const int MaximumWordLength = 30;

    /// <summary>
    /// The lowercase words of the text, once each in order of first appearance. Only letters and digits count; words
    /// that are all digits, or very short, are left out.
    /// </summary>
    public static IReadOnlyList<string> Tokenise(string? text, int limit)
    {
        var words = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            return words;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = new StringBuilder();
        void Flush()
        {
            if (current.Length is >= MinimumWordLength and <= MaximumWordLength)
            {
                var word = current.ToString();
                if (word.Any(char.IsLetter) && seen.Add(word))
                {
                    words.Add(word);
                }
            }

            current.Clear();
        }

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(char.ToLowerInvariant(character));
            }
            else
            {
                Flush();
            }

            if (words.Count >= limit)
            {
                return words;
            }
        }

        Flush();
        return words;
    }

    public async Task<ViewDocumentSuggestionsDto> SuggestAsync(
        DocumentRuleSubject subject, DateOnly today, DocumentDateOrder dateOrder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(subject);
        var detected = DocumentDateDetector.Detect(subject.Text, today, dateOrder);
        var date = detected ?? DateOnly.FromDateTime(subject.FileTime.DateTime);

        double? amount = null;
        string? currency = null;
        if (subject.Text is not null && await corpus.FindAmountFieldAsync(subject.LibraryId, subject.OwnerProfileId, ct).ConfigureAwait(false) is not null
            && DocumentAmountDetector.Detect(subject.Text) is { } found)
        {
            amount = found.Amount;
            currency = found.Currency;
        }

        var (correspondent, type, tags) = await VoteAsync(subject, ct).ConfigureAwait(false);
        return new ViewDocumentSuggestionsDto(date, detected is null, correspondent, type, tags, amount, currency);
    }

    private async Task<(string? Correspondent, string? Type, IReadOnlyList<string> Tags)> VoteAsync(
        DocumentRuleSubject subject, CancellationToken ct)
    {
        var none = (Correspondent: (string?)null, Type: (string?)null, Tags: (IReadOnlyList<string>)[]);
        var words = Tokenise(subject.Text, CandidateWords);
        if (words.Count == 0)
        {
            return none;
        }

        var organised = await corpus.CountOrganisedDocumentsAsync(subject.LibraryId, ct).ConfigureAwait(false);
        if (organised < MinimumAgreement)
        {
            return none;
        }

        var counts = await corpus.GetWordDocumentCountsAsync(subject.LibraryId, words, ct).ConfigureAwait(false);
        // A word is useful when some documents share it: in only one it identifies nothing a vote can use, and in most
        // of the library (greetings, boilerplate) it says nothing about this document.
        var ceiling = Math.Max(AlikeDocuments, organised * 6 / 10);
        var rare = words
            .Where(word => counts.TryGetValue(word, out var count) && count >= MinimumAgreement && count <= ceiling)
            .Select((word, index) => (Word: word, Count: counts[word], Index: index))
            .OrderBy(item => item.Count).ThenBy(item => item.Index)
            .Take(RareWords).Select(item => item.Word).ToList();
        if (rare.Count == 0)
        {
            return none;
        }

        var alike = await corpus.FindSimilarDocumentsAsync(subject.LibraryId, subject.ItemId, rare, AlikeDocuments, ct)
            .ConfigureAwait(false);
        return (
            MostAgreed(alike.Select(document => document.Correspondent)),
            MostAgreed(alike.Select(document => document.DocumentType)),
            AgreedTags(alike));
    }

    /// <summary>The name at least two documents share; on a tie, the one the more alike documents carry.</summary>
    private static string? MostAgreed(IEnumerable<string?> names)
    {
        var groups = new List<(string Name, int Count)>();
        foreach (var name in names.Where(name => !string.IsNullOrWhiteSpace(name)))
        {
            var index = groups.FindIndex(group => string.Equals(group.Name, name, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                groups.Add((name!, 1));
            }
            else
            {
                groups[index] = (groups[index].Name, groups[index].Count + 1);
            }
        }

        var best = groups.Where(group => group.Count >= MinimumAgreement).OrderByDescending(group => group.Count).Select(group => group.Name);
        return best.FirstOrDefault();
    }

    private static IReadOnlyList<string> AgreedTags(IReadOnlyList<SimilarFiledDocument> alike)
    {
        var groups = new List<(string Name, int Count)>();
        foreach (var tag in alike.SelectMany(document => document.Tags.Distinct(StringComparer.OrdinalIgnoreCase)))
        {
            var index = groups.FindIndex(group => string.Equals(group.Name, tag, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                groups.Add((tag, 1));
            }
            else
            {
                groups[index] = (groups[index].Name, groups[index].Count + 1);
            }
        }

        return groups
            .Where(group => group.Count >= MinimumAgreement)
            .OrderByDescending(group => group.Count)
            .Take(MaximumSuggestedTags)
            .Select(group => group.Name).ToList();
    }
}
