using Dapper;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

public sealed class ViewCatalogueRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-catalogue-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly LocalAssetRepository _assets;
    private readonly ViewCatalogueRepository _catalogue;
    private readonly ViewPersonalSpaceRepository _spaces;

    public ViewCatalogueRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _assets = new LocalAssetRepository(_database);
        _catalogue = new ViewCatalogueRepository(_database);
        _spaces = new ViewPersonalSpaceRepository(_database);
    }

    [Fact]
    public async Task APrivateTagIsNeverListedSuggestedOrAttachableByAnotherPerson()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var aliceDocument = await AddDocument(alice, 'a');
        var bobDocument = await AddDocument(bob, 'b');
        var aliceViewer = alice.Viewer();
        var bobViewer = bob.Viewer();

        await _catalogue.ReplaceItemTagsAsync(aliceViewer, aliceDocument, ["Divorce papers"], []);

        var aliceTag = Assert.Single(_catalogue.GetCatalogue(aliceViewer).Tags);
        Assert.Equal("Divorce papers", aliceTag.Name);
        Assert.Equal(1, aliceTag.UsageCount);
        Assert.Contains("Divorce papers", _catalogue.GetTagSuggestions(aliceViewer, null, 20));

        // Bob sees none of it: not the list, not the type-ahead, not a search for the name, not a lookup by id.
        Assert.Empty(_catalogue.GetCatalogue(bobViewer).Tags);
        Assert.Empty(_catalogue.GetTagSuggestions(bobViewer, null, 20));
        Assert.Empty(_catalogue.GetTagSuggestions(bobViewer, "Divorce", 20));
        Assert.Null(_catalogue.FindEntry(bobViewer, ViewCatalogueKind.Tag, aliceTag.Id));
        Assert.Empty(_assets.QueryTimeline(new LocalAssetTimelineQuery(
            [bob.LibraryId], DocumentFilter: new LocalAssetDocumentFilter(TagIds: [aliceTag.Id]))).Items);

        // Guessing the id does not let Bob attach it to his own item, and he cannot touch Alice's item at all.
        await Assert.ThrowsAsync<ViewCatalogueAccessException>(() =>
            _catalogue.ReplaceItemTagsAsync(bobViewer, bobDocument, [], [aliceTag.Id]));
        await Assert.ThrowsAsync<ViewCatalogueAccessException>(() =>
            _catalogue.ReplaceItemTagsAsync(bobViewer, aliceDocument, ["Mine now"], []));
        await Assert.ThrowsAsync<ViewCatalogueAccessException>(() =>
            _catalogue.UpdateDocumentDetailsAsync(bobViewer, aliceDocument, Details(tagIds: [aliceTag.Id])));
        Assert.Empty(_assets.Find(bobDocument)!.Tags);
        Assert.Equal(["Divorce papers"], _assets.Find(aliceDocument)!.Tags);

        // Nor can Bob rename or delete it.
        await Assert.ThrowsAsync<ViewCatalogueAccessException>(() =>
            _catalogue.UpdateEntryAsync(bobViewer, ViewCatalogueKind.Tag, aliceTag.Id, "Renamed", null));
        Assert.False(await _catalogue.DeleteEntryAsync(bobViewer, ViewCatalogueKind.Tag, aliceTag.Id));
        Assert.NotNull(_catalogue.FindEntry(aliceViewer, ViewCatalogueKind.Tag, aliceTag.Id));
    }

    [Fact]
    public async Task ACorrespondentTypeAndFieldOnAPrivateDocumentStayPrivateToo()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        var field = await _catalogue.CreateFieldAsync(alice.Viewer(), "Policy number", ViewDocumentFieldKinds.Text, null);
        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), document, Details(
            correspondent: "Private Clinic", documentType: "Medical bill",
            fields: [new ViewDocumentFieldWrite(field.Id, Text: "PX-99")]));

        var bobCatalogue = _catalogue.GetCatalogue(bob.Viewer());
        Assert.Empty(bobCatalogue.Correspondents);
        Assert.Empty(bobCatalogue.DocumentTypes);
        Assert.Empty(bobCatalogue.Fields);
        var aliceCatalogue = _catalogue.GetCatalogue(alice.Viewer());
        Assert.Equal("Private Clinic", Assert.Single(aliceCatalogue.Correspondents).Name);
        Assert.Equal("Medical bill", Assert.Single(aliceCatalogue.DocumentTypes).Name);
        Assert.Equal("Policy number", Assert.Single(aliceCatalogue.Fields).Name);

        var bobDocument = await AddDocument(bob, 'b');
        await Assert.ThrowsAsync<ViewCatalogueAccessException>(() =>
            _catalogue.UpdateDocumentDetailsAsync(bob.Viewer(), bobDocument,
                Details(fields: [new ViewDocumentFieldWrite(field.Id, Text: "guess")])));
    }

    [Fact]
    public async Task ANameIsVisibleToEveryoneWhoCanReachAnItemThatCarriesIt()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        await _catalogue.ReplaceItemTagsAsync(alice.Viewer(), document, ["Family"], []);

        // Bob can reach Alice's library (a household member, or the Shared library) in one request and not in another.
        var withAlice = new ViewCatalogueViewer(bob.ProfileId, [bob.LibraryId, alice.LibraryId]);
        Assert.Equal("Family", Assert.Single(_catalogue.GetCatalogue(withAlice).Tags).Name);
        Assert.Contains("Family", _catalogue.GetTagSuggestions(withAlice, "fam", 5));
        Assert.Empty(_catalogue.GetCatalogue(bob.Viewer()).Tags);
    }

    [Fact]
    public async Task AHouseholdMembersHiddenItemNeverRevealsItsNames()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var hidden = await AddDocument(alice, 'a');
        var visible = await AddDocument(alice, 'b');
        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), hidden, Details(
            tagNames: ["Divorce lawyer"], correspondent: "Secret Firm", documentType: "Contract"));
        await _catalogue.ReplaceItemTagsAsync(alice.Viewer(), visible, ["Garden"], []);
        await _assets.SetFlagsAsync(hidden, null, true);

        // Bob can open Alice's space (a household member), but not what she hid.
        var bobSeesAlice = new ViewCatalogueViewer(bob.ProfileId, [bob.LibraryId, alice.LibraryId]);
        var catalogue = _catalogue.GetCatalogue(bobSeesAlice);
        Assert.Equal(["Garden"], catalogue.Tags.Select(tag => tag.Name).ToArray());
        Assert.Empty(catalogue.Correspondents);
        Assert.Empty(catalogue.DocumentTypes);
        Assert.DoesNotContain("Divorce lawyer", _catalogue.GetTagSuggestions(bobSeesAlice, null, 50));
        var hiddenTag = Assert.Single(_catalogue.GetCatalogue(alice.Viewer()).Tags, tag => tag.Name == "Divorce lawyer");
        Assert.Null(_catalogue.FindEntry(bobSeesAlice, ViewCatalogueKind.Tag, hiddenTag.Id));
        var bobDocument = await AddDocument(bob, 'c');
        await Assert.ThrowsAsync<ViewCatalogueAccessException>(() =>
            _catalogue.ReplaceItemTagsAsync(bobSeesAlice, bobDocument, [], [hiddenTag.Id]));

        // Alice, who owns it, still sees her own names.
        Assert.Contains(_catalogue.GetCatalogue(alice.Viewer()).Correspondents, item => item.Name == "Secret Firm");
    }

    [Fact]
    public async Task AProfileWithoutDocumentsNeverLearnsNamesThatOnlyAppearOnDocuments()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        var photo = (await _assets.UpsertAsync(new LocalAssetRegistration(
            alice.LibraryId, alice.SpaceId, alice.ProfileId, LocalAssetMediaKinds.Image, "Beach", null,
            [File(@"C:\p\beach.jpg", Hash('e'), "beach.jpg", "image/jpeg")]))).ItemId;
        await _catalogue.ReplaceItemTagsAsync(alice.Viewer(), document, ["Biopsy"], []);
        await _catalogue.ReplaceItemTagsAsync(alice.Viewer(), photo, ["Holiday"], []);

        var child = await CreatePerson();
        var withoutDocuments = new ViewCatalogueViewer(child.ProfileId, [child.LibraryId, alice.LibraryId], IncludeDocuments: false);
        var withDocuments = withoutDocuments with { IncludeDocuments = true };

        Assert.Equal(["Holiday"], _catalogue.GetCatalogue(withoutDocuments).Tags.Select(tag => tag.Name).ToArray());
        Assert.Equal(["Holiday"], _catalogue.GetTagSuggestions(withoutDocuments, null, 20).ToArray());
        Assert.Contains("Biopsy", _catalogue.GetTagSuggestions(withDocuments, null, 20));
        var biopsy = Assert.Single(_catalogue.GetCatalogue(withDocuments).Tags, tag => tag.Name == "Biopsy");
        var childPhoto = (await _assets.UpsertAsync(new LocalAssetRegistration(
            child.LibraryId, child.SpaceId, child.ProfileId, LocalAssetMediaKinds.Image, "Dog", null,
            [File(@"C:\p\dog.jpg", Hash('f'), "dog.jpg", "image/jpeg")]))).ItemId;
        await Assert.ThrowsAsync<ViewCatalogueAccessException>(() =>
            _catalogue.ReplaceItemTagsAsync(withoutDocuments, childPhoto, [], [biopsy.Id]));
    }

    [Fact]
    public async Task LeavingCustomFieldsOutKeepsTheirValuesAndAnEmptyListClearsThem()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        var field = await _catalogue.CreateFieldAsync(alice.Viewer(), "Ref", ViewDocumentFieldKinds.Text, null);
        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), document, Details(
            fields: [new ViewDocumentFieldWrite(field.Id, Text: "R-1")]));

        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), document,
            Details(correspondent: "Someone") with { Fields = null });
        Assert.Equal("R-1", Assert.Single(_assets.Find(document)!.DocumentInfo!.FieldValues!).TextValue);

        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), document, Details());
        Assert.Empty(_assets.Find(document)!.DocumentInfo!.FieldValues!);
    }

    [Fact]
    public async Task ChangingOnlyTheCaseOfAChoiceKeepsItsValuesAndRenamingKeepsTheColour()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        var viewer = alice.Viewer();
        var field = await _catalogue.CreateFieldAsync(viewer, "Stage", ViewDocumentFieldKinds.Choice, ["Paid", "Open"]);
        await _catalogue.UpdateDocumentDetailsAsync(viewer, document, Details(fields: [new ViewDocumentFieldWrite(field.Id, Text: "Paid")]));

        await _catalogue.UpdateFieldAsync(viewer, field.Id, "Stage", ["paid", "Open"]);
        Assert.Equal("paid", Assert.Single(_assets.Find(document)!.DocumentInfo!.FieldValues!).TextValue);
        await _catalogue.UpdateFieldAsync(viewer, field.Id, "Stage", ["Open"]);
        Assert.Empty(_assets.Find(document)!.DocumentInfo!.FieldValues!);

        var tag = await _catalogue.CreateEntryAsync(viewer, ViewCatalogueKind.Tag, "Blue", "#0000FF");
        var renamed = await _catalogue.UpdateEntryAsync(viewer, ViewCatalogueKind.Tag, tag.Id, "Navy", null);
        Assert.Equal("#0000FF", renamed.Colour);
        var cleared = await _catalogue.UpdateEntryAsync(viewer, ViewCatalogueKind.Tag, tag.Id, "Navy", "");
        Assert.Null(cleared.Colour);
    }

    [Fact]
    public async Task NamesThatDifferOnlyByLetterCaseInAnyScriptAreOneName()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        await _catalogue.ReplaceItemTagsAsync(alice.Viewer(), document, ["Ärzte"], []);
        var other = await AddDocument(alice, 'b');
        await _catalogue.ReplaceItemTagsAsync(alice.Viewer(), other, ["ärzte"], []);

        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.QuerySingle<int>("SELECT COUNT(*) FROM tags;"));
        await Assert.ThrowsAsync<ViewCatalogueConflictException>(() =>
            _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Tag, "ÄRZTE", null));
    }

    [Fact]
    public async Task ATagYouMadeButNeverUsedIsYoursAlone()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var tag = await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Tag, "  Someday ", "#aabbcc");

        Assert.Equal("Someday", tag.Name);
        Assert.Equal("#AABBCC", tag.Colour);
        Assert.Equal(0, tag.UsageCount);
        Assert.Single(_catalogue.GetCatalogue(alice.Viewer()).Tags);
        Assert.Empty(_catalogue.GetCatalogue(bob.Viewer()).Tags);
    }

    [Fact]
    public async Task DuplicateNamesAreRefusedIgnoringCaseWithAClearMessage()
    {
        var alice = await CreatePerson();
        await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Correspondent, "Acme Bank", null);
        var other = await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Correspondent, "Zeta", null);

        var create = await Assert.ThrowsAsync<ViewCatalogueConflictException>(() =>
            _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Correspondent, "acme BANK", null));
        Assert.Contains("already exists", create.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ViewCatalogueConflictException>(() =>
            _catalogue.UpdateEntryAsync(alice.Viewer(), ViewCatalogueKind.Correspondent, other.Id, "ACME bank", null));
        // Renaming to its own name in another case is fine.
        var renamed = await _catalogue.UpdateEntryAsync(alice.Viewer(), ViewCatalogueKind.Correspondent, other.Id, "ZETA", null);
        Assert.Equal("ZETA", renamed.Name);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Tag, "   ", null));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Tag, "ok", "red"));
    }

    [Fact]
    public async Task AttachingByNameReusesAnExistingTagAndNeverMakesADuplicate()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var first = await AddDocument(alice, 'a');
        var second = await AddDocument(bob, 'b');
        await _catalogue.ReplaceItemTagsAsync(alice.Viewer(), first, ["Taxes"], []);
        await _catalogue.ReplaceItemTagsAsync(bob.Viewer(), second, ["taxes"], []);

        using var connection = _database.CreateConnection();
        Assert.Equal(1, connection.QuerySingle<int>("SELECT COUNT(*) FROM tags;"));
        Assert.Equal(["Taxes"], _assets.Find(second)!.Tags);
        // Bob now carries it, so Bob can see it; Alice's count is only her own item.
        Assert.Equal(1, Assert.Single(_catalogue.GetCatalogue(bob.Viewer()).Tags).UsageCount);
        Assert.Equal(1, Assert.Single(_catalogue.GetCatalogue(alice.Viewer()).Tags).UsageCount);
    }

    [Fact]
    public async Task DeletingAnEntryRemovesLinksAndSearchWordsButNeverItems()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        var viewer = alice.Viewer();
        await _catalogue.UpdateDocumentDetailsAsync(viewer, document, Details(
            tagNames: ["Receipts"], correspondent: "Corner Shop", documentType: "Receipt"));
        Assert.Single(_assets.Query(new LocalAssetQuery(alice.LibraryId, Search: "Receipts")).Items);

        var tag = Assert.Single(_catalogue.GetCatalogue(viewer).Tags);
        var correspondent = Assert.Single(_catalogue.GetCatalogue(viewer).Correspondents);
        Assert.True(await _catalogue.DeleteEntryAsync(viewer, ViewCatalogueKind.Tag, tag.Id));
        Assert.True(await _catalogue.DeleteEntryAsync(viewer, ViewCatalogueKind.Correspondent, correspondent.Id));

        var item = _assets.Find(document)!;
        Assert.Empty(item.Tags);
        Assert.Null(item.DocumentInfo!.Correspondent);
        Assert.Equal("Receipt", item.DocumentInfo.DocumentType!.Name);
        Assert.Empty(_assets.Query(new LocalAssetQuery(alice.LibraryId, Search: "Receipts")).Items);
        Assert.Empty(_assets.Query(new LocalAssetQuery(alice.LibraryId, Search: "Corner")).Items);
        Assert.Single(_assets.Query(new LocalAssetQuery(alice.LibraryId, Search: "Receipt")).Items);
    }

    [Fact]
    public async Task RenamingATagUpdatesWhatSearchFinds()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        await _catalogue.ReplaceItemTagsAsync(alice.Viewer(), document, ["Insurance"], []);
        var tag = Assert.Single(_catalogue.GetCatalogue(alice.Viewer()).Tags);

        await _catalogue.UpdateEntryAsync(alice.Viewer(), ViewCatalogueKind.Tag, tag.Id, "Cover", "#112233");

        Assert.Empty(_assets.Query(new LocalAssetQuery(alice.LibraryId, Search: "Insurance")).Items);
        Assert.Single(_assets.Query(new LocalAssetQuery(alice.LibraryId, Search: "Cover")).Items);
        Assert.Equal("#112233", _assets.Find(document)!.DocumentInfo!.Tags!.Single().Colour);
    }

    [Fact]
    public async Task SearchFindsADocumentByCorrespondentTagTypeNotesArchiveNumberAndTextFieldValue()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a', documentText: "Quarterly statement of account");
        var note = await _catalogue.CreateFieldAsync(alice.Viewer(), "Account", ViewDocumentFieldKinds.Text, null);
        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), document, Details(
            tagNames: ["Banking"], correspondent: "Northwind Savings", documentType: "Statement",
            archiveNumber: "BOX-17", notes: "Check the overdraft fee", fields: [new ViewDocumentFieldWrite(note.Id, Text: "ACC-445566")]));

        foreach (var term in new[] { "Banking", "Northwind", "Statement", "BOX-17", "overdraft", "ACC-445566", "Quarterly" })
        {
            Assert.True(_assets.Query(new LocalAssetQuery(alice.LibraryId, Search: term)).Items.Count == 1, $"'{term}' should find the document.");
        }

        // Reading the document's words again rebuilds the search document without losing any of it.
        Assert.True(await _assets.SetDocumentTextAsync(document, new LocalAssetDocumentTextWrite(
            "Annual summary of fees", LocalAssetTextSources.Ocr)));
        foreach (var term in new[] { "Banking", "Northwind", "Statement", "BOX-17", "overdraft", "ACC-445566", "Annual" })
        {
            Assert.True(_assets.Query(new LocalAssetQuery(alice.LibraryId, Search: term)).Items.Count == 1, $"'{term}' should still find the document.");
        }

        Assert.Empty(_assets.Query(new LocalAssetQuery(alice.LibraryId, Search: "Quarterly")).Items);
    }

    [Fact]
    public async Task DetailsRoundTripAndFiltersCombine()
    {
        var alice = await CreatePerson();
        var viewer = alice.Viewer();
        var invoice = await AddDocument(alice, 'a');
        var letter = await AddDocument(alice, 'b');
        var plain = await AddDocument(alice, 'c');
        var amount = await _catalogue.CreateFieldAsync(viewer, "Total", ViewDocumentFieldKinds.Money, null);
        var paid = await _catalogue.CreateFieldAsync(viewer, "Paid", ViewDocumentFieldKinds.Bool, null);
        var status = await _catalogue.CreateFieldAsync(viewer, "Status", ViewDocumentFieldKinds.Choice, ["Open", "Closed"]);
        var due = await _catalogue.CreateFieldAsync(viewer, "Due", ViewDocumentFieldKinds.Date, null);

        await _catalogue.UpdateDocumentDetailsAsync(viewer, invoice, Details(
            title: "March invoice", date: new DateOnly(2026, 3, 14), correspondent: "Acme", documentType: "Invoice",
            tagNames: ["Work", "2026"], archiveNumber: "A-1", notes: "Net 30", filingState: "filed",
            fields:
            [
                new ViewDocumentFieldWrite(amount.Id, Text: "eur", Number: 120.5),
                new ViewDocumentFieldWrite(paid.Id, Flag: false),
                new ViewDocumentFieldWrite(status.Id, Text: "open"),
                new ViewDocumentFieldWrite(due.Id, Date: new DateOnly(2026, 4, 13)),
            ]));
        await _catalogue.UpdateDocumentDetailsAsync(viewer, letter, Details(
            date: new DateOnly(2026, 5, 2), correspondent: "Acme", documentType: "Letter", tagNames: ["Work"], filingState: "inbox"));

        var info = _assets.Find(invoice)!.DocumentInfo!;
        Assert.Equal(new DateOnly(2026, 3, 14), info.DocumentDate);
        Assert.Equal("Acme", info.Correspondent!.Name);
        Assert.Equal("Invoice", info.DocumentType!.Name);
        Assert.Equal("A-1", info.ArchiveNumber);
        Assert.Equal("Net 30", info.Notes);
        Assert.Equal("filed", info.FilingState);
        Assert.Equal(["2026", "Work"], info.Tags!.Select(tag => tag.Name).ToArray());
        Assert.Equal("March invoice", _assets.Find(invoice)!.Title);
        var money = Assert.Single(info.FieldValues!, value => value.Name == "Total");
        Assert.Equal(("EUR", 120.5), (money.TextValue, money.NumberValue));
        Assert.Equal("Open", Assert.Single(info.FieldValues!, value => value.Name == "Status").TextValue);
        Assert.False(Assert.Single(info.FieldValues!, value => value.Name == "Paid").BoolValue);
        Assert.Equal(new DateOnly(2026, 4, 13), Assert.Single(info.FieldValues!, value => value.Name == "Due").DateValue);

        var catalogue = _catalogue.GetCatalogue(viewer, [LocalAssetMediaKinds.Document]);
        var work = Assert.Single(catalogue.Tags, tag => tag.Name == "Work");
        var year = Assert.Single(catalogue.Tags, tag => tag.Name == "2026");
        var acme = Assert.Single(catalogue.Correspondents);
        var invoiceType = Assert.Single(catalogue.DocumentTypes, type => type.Name == "Invoice");
        Assert.Equal((2, 1, 2), (work.UsageCount, year.UsageCount, acme.UsageCount));

        Guid[] Ids(LocalAssetDocumentFilter filter) => _assets.QueryTimeline(new LocalAssetTimelineQuery(
            [alice.LibraryId], DocumentFilter: filter)).Items.Select(item => item.Id).ToArray();
        Assert.Equal(3, Ids(new LocalAssetDocumentFilter()).Length);
        Assert.Equal([invoice], Ids(new LocalAssetDocumentFilter(TagIds: [work.Id, year.Id])));
        Assert.Equal(2, Ids(new LocalAssetDocumentFilter(TagIds: [work.Id])).Length);
        Assert.Equal(2, Ids(new LocalAssetDocumentFilter(CorrespondentId: acme.Id)).Length);
        Assert.Equal([invoice], Ids(new LocalAssetDocumentFilter(DocumentTypeId: invoiceType.Id)));
        Assert.Equal([letter], Ids(new LocalAssetDocumentFilter(DateFrom: new DateOnly(2026, 4, 1), DateTo: new DateOnly(2026, 12, 31), CorrespondentId: acme.Id)));
        Assert.Equal([invoice], Ids(new LocalAssetDocumentFilter(DateTo: new DateOnly(2026, 3, 31), CorrespondentId: acme.Id)));
        Assert.Equal([letter], Ids(new LocalAssetDocumentFilter(FilingState: "inbox", CorrespondentId: acme.Id)));
        Assert.Equal([invoice], Ids(new LocalAssetDocumentFilter(Fields: [new LocalAssetFieldFilter(status.Id, "OPEN")])));
        Assert.Equal([invoice], Ids(new LocalAssetDocumentFilter(Fields: [new LocalAssetFieldFilter(amount.Id, "120.5")])));
        Assert.Equal([invoice], Ids(new LocalAssetDocumentFilter(Fields: [new LocalAssetFieldFilter(paid.Id, "false")])));
        Assert.Empty(Ids(new LocalAssetDocumentFilter(Fields: [new LocalAssetFieldFilter(status.Id, "Closed")])));
        Assert.Contains(plain, Ids(new LocalAssetDocumentFilter(DateFrom: new DateOnly(2000, 1, 1))));

        // Details cleared by an empty write.
        await _catalogue.UpdateDocumentDetailsAsync(viewer, invoice, Details());
        info = _assets.Find(invoice)!.DocumentInfo!;
        Assert.Null(info.Correspondent);
        Assert.Empty(info.Tags!);
        Assert.Empty(info.FieldValues!);
        Assert.Null(_assets.Find(invoice)!.Title);
    }

    [Fact]
    public async Task NewDocumentsWaitInTheInboxAndOlderOnesCountAsFiled()
    {
        var alice = await CreatePerson();
        var fresh = await AddDocument(alice, 'a');
        var older = await AddDocument(alice, 'b');
        var photo = (await _assets.UpsertAsync(new LocalAssetRegistration(
            alice.LibraryId, alice.SpaceId, alice.ProfileId, LocalAssetMediaKinds.Image, "Photo", null,
            [File(@"C:\p\photo.jpg", Hash('d'), "photo.jpg", "image/jpeg")]))).ItemId;
        using (var connection = _database.CreateConnection())
        {
            // A document that existed before filing did: no details row at all.
            connection.Execute("DELETE FROM doc_item_details WHERE item_id = @older;", new { older });
            Assert.Equal(0, connection.QuerySingle<int>("SELECT COUNT(*) FROM doc_item_details WHERE item_id = @photo;", new { photo }));
        }

        Assert.Equal("inbox", _assets.Find(fresh)!.DocumentInfo!.FilingState);
        Assert.Equal("filed", _assets.Find(older)!.DocumentInfo!.FilingState);
        Assert.Equal([fresh], _assets.QueryTimeline(new LocalAssetTimelineQuery(
            [alice.LibraryId], DocumentFilter: new LocalAssetDocumentFilter(FilingState: "inbox"))).Items.Select(item => item.Id).ToArray());
        Assert.Equal([older], _assets.QueryTimeline(new LocalAssetTimelineQuery(
            [alice.LibraryId], MediaKinds: [LocalAssetMediaKinds.Document],
            DocumentFilter: new LocalAssetDocumentFilter(FilingState: "filed"))).Items.Select(item => item.Id).ToArray());

        // Saving details keeps the state unless it is changed on purpose.
        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), fresh, Details(correspondent: "Someone"));
        Assert.Equal("inbox", _assets.Find(fresh)!.DocumentInfo!.FilingState);
        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), fresh, Details(filingState: "filed"));
        Assert.Equal("filed", _assets.Find(fresh)!.DocumentInfo!.FilingState);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), photo, Details()));
    }

    [Fact]
    public async Task FieldValuesAreCheckedAgainstTheirKind()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        var viewer = alice.Viewer();
        var money = await _catalogue.CreateFieldAsync(viewer, "Amount", ViewDocumentFieldKinds.Money, null);
        var choice = await _catalogue.CreateFieldAsync(viewer, "Stage", ViewDocumentFieldKinds.Choice, ["One", "Two", "one"]);
        Assert.Equal(["One", "Two"], choice.Choices);

        await Assert.ThrowsAsync<ArgumentException>(() => _catalogue.UpdateDocumentDetailsAsync(viewer, document,
            Details(fields: [new ViewDocumentFieldWrite(money.Id, Number: 5)])));
        await Assert.ThrowsAsync<ArgumentException>(() => _catalogue.UpdateDocumentDetailsAsync(viewer, document,
            Details(fields: [new ViewDocumentFieldWrite(money.Id, Text: "EURO", Number: 5)])));
        await Assert.ThrowsAsync<ArgumentException>(() => _catalogue.UpdateDocumentDetailsAsync(viewer, document,
            Details(fields: [new ViewDocumentFieldWrite(choice.Id, Text: "Three")])));
        await Assert.ThrowsAsync<ArgumentException>(() => _catalogue.CreateFieldAsync(viewer, "Bad", "colour", null));
        await Assert.ThrowsAsync<ArgumentException>(() => _catalogue.CreateFieldAsync(viewer, "Empty choice", ViewDocumentFieldKinds.Choice, []));
        await Assert.ThrowsAsync<ViewCatalogueConflictException>(() => _catalogue.CreateFieldAsync(viewer, "amount", ViewDocumentFieldKinds.Text, null));

        // Taking a choice away clears the values that used it.
        await _catalogue.UpdateDocumentDetailsAsync(viewer, document, Details(fields: [new ViewDocumentFieldWrite(choice.Id, Text: "Two")]));
        await _catalogue.UpdateFieldAsync(viewer, choice.Id, "Phase", ["One"]);
        Assert.Empty(_assets.Find(document)!.DocumentInfo!.FieldValues!);
        Assert.Equal("Phase", Assert.Single(_catalogue.GetCatalogue(viewer).Fields, field => field.Id == choice.Id).Name);
        await _catalogue.DeleteEntryAsync(viewer, ViewCatalogueKind.Field, money.Id);
        Assert.DoesNotContain(_catalogue.GetCatalogue(viewer).Fields, field => field.Id == money.Id);
    }

    [Fact]
    public async Task EveryNewCatalogueIdIsStoredAsABlob()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        var field = await _catalogue.CreateFieldAsync(alice.Viewer(), "F", ViewDocumentFieldKinds.Choice, ["x"]);
        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), document, Details(
            tagNames: ["t"], correspondent: "c", documentType: "d", fields: [new ViewDocumentFieldWrite(field.Id, Text: "x")]));

        using var connection = _database.CreateConnection();
        foreach (var (table, column) in new[]
                 {
                     ("tags", "id"), ("tags", "created_by_profile_id"), ("local_item_tag_links", "item_id"),
                     ("local_item_tag_links", "tag_id"), ("doc_correspondents", "id"), ("doc_correspondents", "created_by_profile_id"),
                     ("doc_types", "id"), ("doc_types", "created_by_profile_id"), ("doc_item_details", "item_id"),
                     ("doc_item_details", "correspondent_id"), ("doc_item_details", "doc_type_id"),
                     ("doc_field_definitions", "id"), ("doc_field_definitions", "created_by_profile_id"),
                     ("doc_field_choices", "field_id"), ("doc_field_values", "item_id"), ("doc_field_values", "field_id"),
                 })
        {
            Assert.Equal(0, connection.QuerySingle<int>(
                $"SELECT COUNT(*) FROM {table} WHERE {column} IS NOT NULL AND typeof({column}) <> 'blob';"));
            Assert.True(connection.QuerySingle<int>($"SELECT COUNT(*) FROM {table};") > 0, $"{table} should have a row.");
        }
    }

    private static ViewDocumentDetailsWrite Details(
        string? title = null, DateOnly? date = null, string? correspondent = null, string? documentType = null,
        string? archiveNumber = null, string? notes = null, string? filingState = null,
        IReadOnlyCollection<string>? tagNames = null, IReadOnlyCollection<Guid>? tagIds = null,
        IReadOnlyCollection<ViewDocumentFieldWrite>? fields = null) =>
        new(title, date, correspondent, documentType, archiveNumber, notes, filingState, tagNames ?? [], tagIds ?? [], fields ?? []);

    private sealed record Person(Guid ProfileId, Guid SpaceId, Guid LibraryId)
    {
        public ViewCatalogueViewer Viewer() => new(ProfileId, [LibraryId]);
    }

    private async Task<Person> CreatePerson()
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
        return new Person(profileId, space.Id, libraryId);
    }

    private async Task<Guid> AddDocument(Person person, char seed, string? documentText = null) =>
        (await _assets.UpsertAsync(new LocalAssetRegistration(
            person.LibraryId, person.SpaceId, person.ProfileId,
            LocalAssetMediaKinds.Document, $"Document {seed}", null,
            [File($@"C:\docs\{seed}.pdf", Hash(seed), $"{seed}.pdf", "application/pdf")],
            DocumentText: documentText))).ItemId;

    private static LocalAssetFileRegistration File(string path, string hash, string fileName, string mimeType) =>
        new(path, hash, fileName, mimeType, 1024, DateTimeOffset.UtcNow);

    private static string Hash(char character) => new(character, 64);

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        if (System.IO.File.Exists(_path))
        {
            System.IO.File.Delete(_path);
        }
    }
}
