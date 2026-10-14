using System.Globalization;
using Dapper;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The Documents saved-view rule fields (what a saved view can say), the Documents sort and the bulk edit's data
/// store half, against a real temporary SQLite file.
/// </summary>
public sealed class DocumentSavedViewAndBulkTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-saved-views-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly LocalAssetRepository _assets;
    private readonly ViewCatalogueRepository _catalogue;
    private readonly DocumentBulkRepository _bulk;
    private readonly ViewPersonalSpaceRepository _spaces;

    public DocumentSavedViewAndBulkTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _assets = new LocalAssetRepository(_database);
        _catalogue = new ViewCatalogueRepository(_database);
        _bulk = new DocumentBulkRepository(_database);
        _spaces = new ViewPersonalSpaceRepository(_database);
    }

    private sealed record Library(Guid ProfileId, Guid SpaceId, Guid LibraryId)
    {
        public ViewCatalogueViewer Viewer(bool includeDocuments = true) => new(ProfileId, [LibraryId], includeDocuments);
    }

    private static CollectionRuleDefinition Rule(params (string Field, string Op, string[] Values)[] conditions) => new()
    {
        Groups =
        [
            new CollectionRuleGroup
            {
                MatchMode = "all",
                JoinWithPrevious = "and",
                Conditions =
                [
                    new CollectionRulePredicate { Field = "media_type", Op = "eq", Value = "document" },
                    .. conditions.Select(condition => new CollectionRulePredicate
                    {
                        Field = condition.Field, Op = condition.Op, Values = condition.Values,
                    }),
                ],
            },
        ],
    };

    private static (string, string, string[]) C(string field, string op, params string[] values) => (field, op, values);

    private Guid[] Match(Library library, params (string Field, string Op, string[] Values)[] conditions) =>
        _assets.QueryTimeline(new LocalAssetTimelineQuery([library.LibraryId], SmartRule: Rule(conditions)))
            .Items.Select(item => item.Id).OrderBy(id => id).ToArray();

    private static Guid[] Ids(params Guid[] ids) => ids.OrderBy(id => id).ToArray();

    [Fact]
    public async Task TagsMatchAnyOrAllByIdOrName()
    {
        var alice = await CreateLibrary();
        var a = await AddDocument(alice, 'a');
        var b = await AddDocument(alice, 'b');
        var c = await AddDocument(alice, 'c');
        await Save(alice, a, tags: ["Work", "2026"]);
        await Save(alice, b, tags: ["Work"]);
        var work = Tag(alice, "Work");
        var year = Tag(alice, "2026");

        Assert.Equal(Ids(a, b), Match(alice, C("tags", "in", work.ToString("D"))));
        Assert.Equal(Ids(a), Match(alice, C("tags", "all", work.ToString("D"), year.ToString("D"))));
        Assert.Equal(Ids(a), Match(alice, C("tags", "all", "work", "2026")));
        Assert.Equal(Ids(a, b), Match(alice, C("tags", "all", "WORK")));
        Assert.Equal(Ids(c), Match(alice, C("tags", "unknown")));
        Assert.Equal(Ids(a, b), Match(alice, C("tags", "known")));
    }

    [Fact]
    public async Task CorrespondentAndTypeMatchByIdOrNameAndKnownState()
    {
        var alice = await CreateLibrary();
        var a = await AddDocument(alice, 'a');
        var b = await AddDocument(alice, 'b');
        var c = await AddDocument(alice, 'c');
        await Save(alice, a, correspondent: "Acme", documentType: "Invoice");
        await Save(alice, b, correspondent: "Globex", documentType: "Letter");
        var acme = _catalogue.GetCatalogue(alice.Viewer()).Correspondents.Single(item => item.Name == "Acme").Id;
        var invoice = _catalogue.GetCatalogue(alice.Viewer()).DocumentTypes.Single(item => item.Name == "Invoice").Id;

        Assert.Equal(Ids(a), Match(alice, C("correspondent", "eq", acme.ToString("D"))));
        Assert.Equal(Ids(a), Match(alice, C("correspondent", "eq", "acme")));
        Assert.Equal(Ids(a, b), Match(alice, C("correspondent", "in", "Acme", "Globex")));
        Assert.Equal(Ids(a), Match(alice, C("correspondent", "contains", "cm")));
        Assert.Equal(Ids(c), Match(alice, C("correspondent", "unknown")));
        Assert.Equal(Ids(a), Match(alice, C("document_type", "eq", invoice.ToString("D"))));
        Assert.Equal(Ids(b), Match(alice, C("document_type", "eq", "Letter")));
        // "is not" only counts documents that have a type at all, like every other field.
        Assert.Equal(Ids(b), Match(alice, C("document_type", "neq", "Invoice")));
    }

    [Fact]
    public async Task DocumentDateSupportsFixedRelativeAndKnownRanges()
    {
        var alice = await CreateLibrary();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var recent = await AddDocument(alice, 'a');
        var older = await AddDocument(alice, 'b');
        var lastYear = await AddDocument(alice, 'c');
        var undated = await AddDocument(alice, 'd');
        await Save(alice, recent, date: today.AddDays(-5));
        await Save(alice, older, date: today.AddDays(-60));
        await Save(alice, lastYear, date: new DateOnly(today.Year - 1, 6, 1));
        var fixedDay = Iso(today.AddDays(-60));

        Assert.Equal(Ids(older), Match(alice, C("document_date", "eq", fixedDay)));
        Assert.Equal(Ids(older), Match(alice, C("document_date", "between", Iso(today.AddDays(-90)), Iso(today.AddDays(-30)))));
        Assert.Contains(recent, Match(alice, C("document_date", "gte", Iso(today.AddDays(-10)))));
        Assert.DoesNotContain(older, Match(alice, C("document_date", "gte", Iso(today.AddDays(-10)))));
        Assert.Contains(recent, Match(alice, C("document_date", "within", "last_30_days")));
        Assert.DoesNotContain(older, Match(alice, C("document_date", "within", "last_30_days")));
        Assert.Contains(older, Match(alice, C("document_date", "within", "last_90_days")));
        Assert.Equal(Ids(lastYear), Match(alice, C("document_date", "within", "last_year")));
        // A document with no date of its own counts by the day it was added, which is today.
        Assert.Contains(undated, Match(alice, C("document_date", "within", "last_7_days")));
        Assert.Equal(Ids(recent, older, lastYear), Match(alice, C("document_date", "known")));
        Assert.Equal(Ids(undated), Match(alice, C("document_date", "unknown")));
    }

    [Fact]
    public void RelativeRangesAreWorkedOutFromTheDayTheyAreAskedOn()
    {
        var day = new DateOnly(2026, 10, 14);
        Assert.Equal((new DateOnly(2026, 9, 15), day), ViewSmartGalleryRules.ResolveRelativeRange("last_30_days", day));
        Assert.Equal((new DateOnly(2026, 1, 1), day), ViewSmartGalleryRules.ResolveRelativeRange("this_year", day));
        Assert.Equal((new DateOnly(2026, 10, 1), day), ViewSmartGalleryRules.ResolveRelativeRange("this_month", day));
        Assert.Equal((new DateOnly(2025, 1, 1), new DateOnly(2025, 12, 31)), ViewSmartGalleryRules.ResolveRelativeRange("last_year", day));
        Assert.Throws<ArgumentException>(() => ViewSmartGalleryRules.ResolveRelativeRange("tomorrow", day));
    }

    [Fact]
    public async Task FilingStateAndTextContainsNarrowTheList()
    {
        var alice = await CreateLibrary();
        var inbox = await AddDocument(alice, 'a', documentText: "Electricity bill for September");
        var filed = await AddDocument(alice, 'b', documentText: "Lease agreement");
        await Save(alice, inbox, filingState: "inbox");
        await Save(alice, filed, filingState: "filed");

        Assert.Equal(Ids(inbox), Match(alice, C("filing_state", "eq", "inbox")));
        Assert.Equal(Ids(filed), Match(alice, C("filing_state", "neq", "inbox")));
        Assert.Equal(Ids(inbox), Match(alice, C("text_contains", "contains", "electricity")));
        Assert.Equal(Ids(filed), Match(alice, C("text_contains", "contains", "LEASE")));
        Assert.Empty(Match(alice, C("text_contains", "contains", "passport")));
    }

    [Fact]
    public async Task CustomFieldsMatchEqualsContainsAndRangesByKind()
    {
        var alice = await CreateLibrary();
        var viewer = alice.Viewer();
        var amount = await _catalogue.CreateFieldAsync(viewer, "Total", ViewDocumentFieldKinds.Money, null);
        var note = await _catalogue.CreateFieldAsync(viewer, "Reference", ViewDocumentFieldKinds.Text, null);
        var status = await _catalogue.CreateFieldAsync(viewer, "Status", ViewDocumentFieldKinds.Choice, ["Open", "Closed"]);
        var paid = await _catalogue.CreateFieldAsync(viewer, "Paid", ViewDocumentFieldKinds.Bool, null);
        var due = await _catalogue.CreateFieldAsync(viewer, "Due", ViewDocumentFieldKinds.Date, null);
        var small = await AddDocument(alice, 'a');
        var large = await AddDocument(alice, 'b');
        var empty = await AddDocument(alice, 'c');
        await Save(alice, small, fields:
        [
            new ViewDocumentFieldWrite(amount.Id, Text: "eur", Number: 20), new ViewDocumentFieldWrite(note.Id, Text: "PO-1001"),
            new ViewDocumentFieldWrite(status.Id, Text: "open"), new ViewDocumentFieldWrite(paid.Id, Flag: false),
            new ViewDocumentFieldWrite(due.Id, Date: new DateOnly(2026, 4, 1)),
        ]);
        await Save(alice, large, fields:
        [
            new ViewDocumentFieldWrite(amount.Id, Text: "eur", Number: 500), new ViewDocumentFieldWrite(status.Id, Text: "closed"),
            new ViewDocumentFieldWrite(paid.Id, Flag: true), new ViewDocumentFieldWrite(due.Id, Date: new DateOnly(2026, 9, 1)),
        ]);

        string Key(Guid id) => ViewSmartGalleryRules.CustomFieldKey(id);
        Assert.Equal(Ids(large), Match(alice, C(Key(amount.Id), "gt", "100")));
        Assert.Equal(Ids(small), Match(alice, C(Key(amount.Id), "between", "10", "50")));
        Assert.Equal(Ids(small, large), Match(alice, C(Key(amount.Id), "gte", "20")));
        Assert.Equal(Ids(small), Match(alice, C(Key(amount.Id), "eq", "20")));
        Assert.Equal(Ids(small), Match(alice, C(Key(note.Id), "contains", "1001")));
        Assert.Equal(Ids(small), Match(alice, C(Key(status.Id), "eq", "OPEN")));
        Assert.Equal(Ids(large), Match(alice, C(Key(status.Id), "neq", "open")));
        Assert.Equal(Ids(large), Match(alice, C(Key(paid.Id), "eq", "true")));
        Assert.Equal(Ids(small), Match(alice, C(Key(due.Id), "lt", "2026-06-01")));
        Assert.Equal(Ids(large), Match(alice, C(Key(due.Id), "between", "2026-08-01", "2026-12-31")));
        Assert.Equal(Ids(large, empty), Match(alice, C(Key(note.Id), "unknown")));
        Assert.Equal(Ids(small), Match(alice, C(Key(note.Id), "known")));
    }

    [Fact]
    public void ARuleThatTriesToInjectSqlThroughAFieldNameIsRefused()
    {
        var rule = Rule(C("field:00000000-0000-0000-0000-000000000000 OR 1=1", "eq", "x"));
        Assert.Throws<ArgumentException>(() => LocalAssetSmartRuleSqlCompiler.Compile(rule));
        var emptyId = Rule(C(ViewSmartGalleryRules.CustomFieldPrefix + Guid.Empty.ToString("D"), "eq", "x"));
        Assert.Throws<ArgumentException>(() => LocalAssetSmartRuleSqlCompiler.Compile(emptyId));
    }

    [Fact]
    public async Task SortedListsPageByPositionWithoutRepeatsOrGaps()
    {
        var alice = await CreateLibrary();
        var a = await AddDocument(alice, 'a');
        var b = await AddDocument(alice, 'b');
        var c = await AddDocument(alice, 'c');
        var d = await AddDocument(alice, 'd');
        await Save(alice, a, title: "Zebra", correspondent: "Beta", date: new DateOnly(2026, 1, 5));
        await Save(alice, b, title: "apple", correspondent: "alpha", date: new DateOnly(2026, 3, 5));
        await Save(alice, c, title: "Mango", date: new DateOnly(2026, 2, 5));
        await Save(alice, d, title: "banana", correspondent: "Gamma", date: new DateOnly(2026, 4, 5));

        Guid[] Page(string sort, int offset, int limit) => _assets.QueryTimeline(new LocalAssetTimelineQuery(
            [alice.LibraryId], Limit: limit, DocumentFilter: new LocalAssetDocumentFilter(Sort: sort, Offset: offset)))
            .Items.Select(item => item.Id).ToArray();

        Assert.Equal([b, d, c, a], Page(ViewDocumentSorts.Title, 0, 10));
        Assert.Equal([b, d], Page(ViewDocumentSorts.Title, 0, 2));
        Assert.Equal([c, a], Page(ViewDocumentSorts.Title, 2, 2));
        Assert.Equal([d, b, c, a], Page(ViewDocumentSorts.DocumentDate, 0, 10));
        // Correspondent A to Z with the document that has none last.
        Assert.Equal([b, a, d, c], Page(ViewDocumentSorts.Correspondent, 0, 10));
        Assert.Equal(4, Page(ViewDocumentSorts.Added, 0, 10).Length);
        Assert.Throws<ArgumentException>(() => Page("chaos", 0, 10));
    }

    [Fact]
    public async Task TheRememberedSortSurvivesSavingTheScopeAndDensity()
    {
        var alice = await CreateLibrary();
        var profiles = new ViewProfileRepository(_database);

        Assert.Null((await profiles.GetPreferencesAsync(alice.ProfileId)).DocumentsSort);
        Assert.True(await profiles.SaveDocumentsSortAsync(alice.ProfileId, ViewDocumentSorts.Title));
        Assert.Equal(ViewDocumentSorts.Title, (await profiles.GetPreferencesAsync(alice.ProfileId)).DocumentsSort);

        await profiles.SavePreferencesAsync(new ViewProfilePreferences(
            alice.ProfileId, ViewScopeKind.Mine, null, ViewTimelineDensity.Compact, DateTimeOffset.UtcNow));
        var saved = await profiles.GetPreferencesAsync(alice.ProfileId);
        Assert.Equal(ViewDocumentSorts.Title, saved.DocumentsSort);
        Assert.Equal(ViewTimelineDensity.Compact, saved.TimelineDensity);

        Assert.True(await profiles.SaveDocumentsSortAsync(alice.ProfileId, null));
        Assert.Null((await profiles.GetPreferencesAsync(alice.ProfileId)).DocumentsSort);
        Assert.False(await profiles.SaveDocumentsSortAsync(Guid.NewGuid(), ViewDocumentSorts.Added));
    }

    [Fact]
    public async Task BulkTagsAddAndRemoveWithoutTouchingOtherTags()
    {
        var alice = await CreateLibrary();
        var viewer = alice.Viewer();
        var a = await AddDocument(alice, 'a');
        await Save(alice, a, tags: ["Keep"]);

        var added = await _bulk.ApplyAsync(viewer, a, new DocumentBulkChange(DocumentBulkChangeKind.AddTags, TagNames: ["Tax", "2026"]));
        Assert.Equal(DocumentBulkOutcome.Done, added.Outcome);
        Assert.Equal(["2026", "Keep", "Tax"], _assets.Find(a)!.DocumentInfo!.Tags!.Select(tag => tag.Name).OrderBy(name => name, StringComparer.Ordinal).ToArray());
        Assert.Equal(DocumentBulkOutcome.Unchanged,
            (await _bulk.ApplyAsync(viewer, a, new DocumentBulkChange(DocumentBulkChangeKind.AddTags, TagNames: ["tax"]))).Outcome);

        var tax = Tag(alice, "Tax");
        Assert.Equal(DocumentBulkOutcome.Done,
            (await _bulk.ApplyAsync(viewer, a, new DocumentBulkChange(DocumentBulkChangeKind.RemoveTags, TagIds: [tax]))).Outcome);
        Assert.DoesNotContain("Tax", _assets.Find(a)!.DocumentInfo!.Tags!.Select(tag => tag.Name));
        Assert.Contains("Keep", _assets.Find(a)!.DocumentInfo!.Tags!.Select(tag => tag.Name));
        Assert.Equal(DocumentBulkOutcome.Unchanged,
            (await _bulk.ApplyAsync(viewer, a, new DocumentBulkChange(DocumentBulkChangeKind.RemoveTags, TagIds: [tax]))).Outcome);
    }

    [Fact]
    public async Task BulkSetCorrespondentTypeAndDateRecordThePersonAsTheSource()
    {
        var alice = await CreateLibrary();
        var viewer = alice.Viewer();
        var a = await AddDocument(alice, 'a');

        Assert.Equal(DocumentBulkOutcome.Done,
            (await _bulk.ApplyAsync(viewer, a, new DocumentBulkChange(DocumentBulkChangeKind.SetCorrespondent, Name: "Acme"))).Outcome);
        Assert.Equal(DocumentBulkOutcome.Done,
            (await _bulk.ApplyAsync(viewer, a, new DocumentBulkChange(DocumentBulkChangeKind.SetDocumentType, Name: "Invoice"))).Outcome);
        Assert.Equal(DocumentBulkOutcome.Done,
            (await _bulk.ApplyAsync(viewer, a, new DocumentBulkChange(DocumentBulkChangeKind.SetDocumentDate, Date: new DateOnly(2026, 2, 3)))).Outcome);

        var info = _assets.Find(a)!.DocumentInfo!;
        Assert.Equal(("Acme", "Invoice", new DateOnly(2026, 2, 3)), (info.Correspondent!.Name, info.DocumentType!.Name, info.DocumentDate));
        Assert.Equal("person", Source(a, "correspondent"));
        Assert.Equal("person", Source(a, "document_type"));

        Assert.Equal(DocumentBulkOutcome.Unchanged,
            (await _bulk.ApplyAsync(viewer, a, new DocumentBulkChange(DocumentBulkChangeKind.SetCorrespondent, Name: "acme"))).Outcome);
        Assert.Equal(DocumentBulkOutcome.Done,
            (await _bulk.ApplyAsync(viewer, a, new DocumentBulkChange(DocumentBulkChangeKind.SetCorrespondent, Name: null))).Outcome);
        Assert.Null(_assets.Find(a)!.DocumentInfo!.Correspondent);
        Assert.Equal(DocumentBulkOutcome.Done,
            (await _bulk.ApplyAsync(viewer, a, new DocumentBulkChange(DocumentBulkChangeKind.SetDocumentDate, Date: null))).Outcome);
        Assert.Null(_assets.Find(a)!.DocumentInfo!.DocumentDate);
    }

    [Fact]
    public async Task BulkNeverReachesAnotherPersonsDocumentsOrNonDocuments()
    {
        var alice = await CreateLibrary();
        var bob = await CreateLibrary();
        var bobs = await AddDocument(bob, 'b');
        var photo = (await _assets.UpsertAsync(new LocalAssetRegistration(
            alice.LibraryId, alice.SpaceId, alice.ProfileId, LocalAssetMediaKinds.Image, "Photo", null,
            [new LocalAssetFileRegistration(@"C:\p\1.jpg", Hash('e'), "1.jpg", "image/jpeg", 1024, DateTimeOffset.UtcNow)]))).ItemId;
        var change = new DocumentBulkChange(DocumentBulkChangeKind.AddTags, TagNames: ["Sneaky"]);

        Assert.Equal(DocumentBulkOutcome.Refused, (await _bulk.ApplyAsync(alice.Viewer(), bobs, change)).Outcome);
        Assert.Equal(DocumentBulkOutcome.Refused, (await _bulk.ApplyAsync(alice.Viewer(), Guid.NewGuid(), change)).Outcome);
        Assert.Equal(DocumentBulkOutcome.Refused, (await _bulk.ApplyAsync(alice.Viewer(), photo, change)).Outcome);
        Assert.Equal(DocumentBulkOutcome.Refused, (await _bulk.ApplyAsync(alice.Viewer(includeDocuments: false), await AddDocument(alice, 'a'), change)).Outcome);
        Assert.Empty(_assets.Find(bobs)!.Tags);
        Assert.DoesNotContain(_catalogue.GetCatalogue(bob.Viewer()).Tags, tag => tag.Name == "Sneaky");

        var described = _bulk.Describe(alice.Viewer(), [bobs, photo]);
        Assert.False(described.Single(subject => subject.ItemId == bobs).Reachable);
        Assert.False(described.Single(subject => subject.ItemId == photo).IsDocument);
    }

    private Guid Tag(Library library, string name) =>
        _catalogue.GetCatalogue(library.Viewer()).Tags.Single(tag => tag.Name == name).Id;

    private string? Source(Guid itemId, string slot)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<string?>(
            "SELECT set_by FROM doc_item_field_sources WHERE item_id = @itemId AND slot = @slot;", new { itemId, slot });
    }

    private Task Save(
        Library library, Guid itemId, string? title = null, DateOnly? date = null, string? correspondent = null,
        string? documentType = null, string? filingState = null, IReadOnlyCollection<string>? tags = null,
        IReadOnlyCollection<ViewDocumentFieldWrite>? fields = null) =>
        _catalogue.UpdateDocumentDetailsAsync(library.Viewer(), itemId, new ViewDocumentDetailsWrite(
            title, date, correspondent, documentType, null, null, filingState, tags ?? [], [], fields ?? []));

    private async Task<Library> CreateLibrary()
    {
        var profileId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
                VALUES (@profileId, @name, '#7C4DFF', 'RestrictedProfile', @now);
                """, new { profileId, name = $"Profile {profileId:N}", now = DateTimeOffset.UtcNow });
        }

        var space = await _spaces.CreateAsync(profileId, libraryId);
        return new Library(profileId, space.Id, libraryId);
    }

    private async Task<Guid> AddDocument(Library library, char seed, string? documentText = null) =>
        (await _assets.UpsertAsync(new LocalAssetRegistration(
            library.LibraryId, library.SpaceId, library.ProfileId,
            LocalAssetMediaKinds.Document, $"Document {seed}", null,
            [new LocalAssetFileRegistration($@"C:\docs\{seed}.pdf", Hash(seed), $"{seed}.pdf", "application/pdf", 1024, DateTimeOffset.UtcNow)],
            DocumentText: documentText))).ItemId;

    private static string Hash(char character) => new(character, 64);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
