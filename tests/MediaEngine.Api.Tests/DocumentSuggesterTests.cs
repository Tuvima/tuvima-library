using MediaEngine.Api.Services.LocalAssets.Filing;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

public sealed class DocumentSuggesterTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    /// <summary>A tiny library kept in memory: every filed document is its words plus the names it was filed under.</summary>
    private sealed class FakeCorpus : IDocumentSuggestionCorpus
    {
        public List<(Guid Id, string Text, string? Correspondent, string? Type, string[] Tags)> Filed { get; } = [];
        public DocumentAmountField? AmountField { get; set; }

        public Task<int> CountOrganisedDocumentsAsync(Guid libraryId, CancellationToken ct = default) => Task.FromResult(Filed.Count);

        public Task<IReadOnlyDictionary<string, int>> GetWordDocumentCountsAsync(
            Guid libraryId, IReadOnlyCollection<string> words, CancellationToken ct = default)
        {
            IReadOnlyDictionary<string, int> counts = words
                .Select(word => (Word: word, Count: Filed.Count(doc => Words(doc.Text).Contains(word))))
                .Where(item => item.Count > 0).ToDictionary(item => item.Word, item => item.Count);
            return Task.FromResult(counts);
        }

        public Task<IReadOnlyList<SimilarFiledDocument>> FindSimilarDocumentsAsync(
            Guid libraryId, Guid excludeItemId, IReadOnlyCollection<string> words, int take, CancellationToken ct = default)
        {
            IReadOnlyList<SimilarFiledDocument> result = Filed
                .Select(doc => (Doc: doc, Hits: words.Count(word => Words(doc.Text).Contains(word))))
                .Where(item => item.Hits > 0)
                .OrderByDescending(item => item.Hits)
                .Take(take)
                .Select(item => new SimilarFiledDocument(item.Doc.Id, item.Doc.Correspondent, item.Doc.Type, item.Doc.Tags))
                .ToList();
            return Task.FromResult(result);
        }

        public Task<DocumentAmountField?> FindAmountFieldAsync(Guid libraryId, Guid? ownerProfileId, CancellationToken ct = default) => Task.FromResult(AmountField);

        private static HashSet<string> Words(string text) => DocumentSuggester.Tokenise(text, 10_000).ToHashSet();
    }

    private static DocumentRuleSubject Subject(string? text) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), false, "Scan", "scan.pdf", @"C:\docs", text, [],
            "inbox", new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero));

    private static void AddWaterBills(FakeCorpus corpus, int count, string correspondent = "Acme Water", string type = "Bill", params string[] tags)
    {
        for (var i = 0; i < count; i++)
        {
            corpus.Filed.Add((Guid.NewGuid(),
                $"Acme Water Utility statement number {i} for the supply at Maple Street. Thank you for being a customer.",
                correspondent, type, tags));
        }
    }

    private static void AddNoise(FakeCorpus corpus, int count)
    {
        for (var i = 0; i < count; i++)
        {
            corpus.Filed.Add((Guid.NewGuid(), $"Unrelated gardening notes about tomatoes and compost batch {i}.", "Garden Club", "Note", ["Hobby"]));
        }
    }

    [Fact]
    public async Task SuggestsTheMostCommonCorrespondentTypeAndTagsWhenAtLeastTwoOfTheTopFiveAgree()
    {
        var corpus = new FakeCorpus();
        AddWaterBills(corpus, 4, tags: ["Home", "Utilities"]);
        AddNoise(corpus, 6);

        var suggestions = await new DocumentSuggester(corpus).SuggestAsync(
            Subject("Acme Water Utility statement for the supply at Maple Street. Invoice date: 2026-09-30"),
            Today, DocumentDateOrder.Unspecified, CancellationToken.None);

        Assert.Equal("Acme Water", suggestions.Correspondent);
        Assert.Equal("Bill", suggestions.DocumentType);
        Assert.Equal(["Home", "Utilities"], suggestions.Tags!.OrderBy(tag => tag).ToArray());
        Assert.Equal(new DateOnly(2026, 9, 30), suggestions.DocumentDate);
        Assert.False(suggestions.DateFromFile);
    }

    [Fact]
    public async Task SuggestsNothingWhenOnlyOneAlikeDocumentExists()
    {
        var corpus = new FakeCorpus();
        AddWaterBills(corpus, 1, tags: ["Home"]);
        AddNoise(corpus, 3);

        var suggestions = await new DocumentSuggester(corpus).SuggestAsync(
            Subject("Acme Water Utility statement for Maple Street"), Today, DocumentDateOrder.Unspecified, CancellationToken.None);

        Assert.Null(suggestions.Correspondent);
        Assert.Null(suggestions.DocumentType);
        Assert.Empty(suggestions.Tags!);
    }

    [Fact]
    public async Task DoesNotSuggestAnythingWhenTheTopDocumentsDisagree()
    {
        var corpus = new FakeCorpus();
        AddWaterBills(corpus, 1, "Acme Water", "Bill");
        AddWaterBills(corpus, 1, "River Board", "Letter");
        AddWaterBills(corpus, 1, "Tap Co", "Receipt");
        AddNoise(corpus, 4);

        var suggestions = await new DocumentSuggester(corpus).SuggestAsync(
            Subject("Acme Water Utility statement for Maple Street"), Today, DocumentDateOrder.Unspecified, CancellationToken.None);

        Assert.Null(suggestions.Correspondent);
        Assert.Null(suggestions.DocumentType);
    }

    [Fact]
    public async Task OnlyTheFiveMostAlikeDocumentsVote()
    {
        var corpus = new FakeCorpus();
        // Five close matches say "Acme Water"; two weaker ones say otherwise and must not outvote them or break a tie.
        AddWaterBills(corpus, 5, "Acme Water", "Bill");
        corpus.Filed.Add((Guid.NewGuid(), "Utility statement", "Other Co", "Letter", []));
        corpus.Filed.Add((Guid.NewGuid(), "Utility statement", "Other Co", "Letter", []));
        AddNoise(corpus, 4);

        var suggestions = await new DocumentSuggester(corpus).SuggestAsync(
            Subject("Acme Water Utility statement for the supply at Maple Street"), Today, DocumentDateOrder.Unspecified, CancellationToken.None);

        Assert.Equal("Acme Water", suggestions.Correspondent);
    }

    [Fact]
    public async Task WordsEveryDocumentSharesDoNotCountAsSimilarity()
    {
        var corpus = new FakeCorpus();
        for (var i = 0; i < 10; i++)
        {
            corpus.Filed.Add((Guid.NewGuid(), $"Dear customer please find enclosed the document dated today thank you {i}", i % 2 == 0 ? "A" : "B", "Letter", []));
        }

        var suggestions = await new DocumentSuggester(corpus).SuggestAsync(
            Subject("Dear customer please find enclosed the document dated today thank you"), Today, DocumentDateOrder.Unspecified, CancellationToken.None);

        Assert.Null(suggestions.Correspondent);
    }

    [Fact]
    public async Task FallsBackToTheFileTimeWhenTheWordsHaveNoDateAndNeedsNoCorpusForIt()
    {
        var suggestions = await new DocumentSuggester(new FakeCorpus()).SuggestAsync(
            Subject("A letter with no date in it at all"), Today, DocumentDateOrder.Unspecified, CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 9, 1), suggestions.DocumentDate);
        Assert.True(suggestions.DateFromFile);

        var photo = await new DocumentSuggester(new FakeCorpus()).SuggestAsync(
            Subject(null), Today, DocumentDateOrder.Unspecified, CancellationToken.None);
        Assert.True(photo.DateFromFile);
        Assert.Null(photo.Correspondent);
    }

    [Fact]
    public async Task AnAmountIsOnlySuggestedWhenACustomFieldNamedAmountExists()
    {
        var corpus = new FakeCorpus();
        var text = "Acme Water. Invoice date: 2026-09-30. Amount due 120.50 EUR";

        var without = await new DocumentSuggester(corpus).SuggestAsync(Subject(text), Today, DocumentDateOrder.Unspecified, CancellationToken.None);
        Assert.Null(without.Amount);

        corpus.AmountField = new DocumentAmountField(Guid.NewGuid(), "money");
        var with = await new DocumentSuggester(corpus).SuggestAsync(Subject(text), Today, DocumentDateOrder.Unspecified, CancellationToken.None);
        Assert.Equal(120.50, with.Amount);
        Assert.Equal("EUR", with.Currency);
    }

    [Fact]
    public void WordsAreLowercasedUniqueAndSkipShortOrNumericOnes()
    {
        var words = DocumentSuggester.Tokenise("The Invoice 12345 invoice, from ACME-Water Co. Straße №7 ab", 100);
        Assert.Equal(["invoice", "from", "acme", "water", "straße"], words);
        Assert.Equal(2, DocumentSuggester.Tokenise("alpha beta gamma delta", 2).Count);
        Assert.Empty(DocumentSuggester.Tokenise(null, 5));
    }
}
