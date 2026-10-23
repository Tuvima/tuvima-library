using Dapper;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

public sealed class DocumentFilingRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-doc-filing-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly LocalAssetRepository _assets;
    private readonly ViewCatalogueRepository _catalogue;
    private readonly ViewPersonalSpaceRepository _spaces;
    private readonly DocumentFilingRepository _filing;
    private readonly DocumentRuleRepository _rules;

    public DocumentFilingRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _assets = new LocalAssetRepository(_database);
        _catalogue = new ViewCatalogueRepository(_database);
        _spaces = new ViewPersonalSpaceRepository(_database);
        _filing = new DocumentFilingRepository(_database);
        _rules = new DocumentRuleRepository(_database);
    }

    // ------------------------------------------------------------------ rules

    [Fact]
    public async Task ARuleBelongsToItsCreatorAndNobodyElseCanReadChangeOrderOrDeleteIt()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var created = await _rules.CreateAsync(NewRule(alice, "Alice's rule", ("filename_contains", "bill")));
        var second = await _rules.CreateAsync(NewRule(alice, "Second", ("filename_contains", "tax")));

        Assert.Equal([created.Id, second.Id], (await _rules.ListOwnedAsync(alice.ProfileId)).Select(rule => rule.Id).ToArray());
        Assert.Empty(await _rules.ListOwnedAsync(bob.ProfileId));
        Assert.Null(await _rules.FindOwnedAsync(bob.ProfileId, created.Id));
        Assert.Null(await _rules.ReplaceAsync(created with { OwnerProfileId = bob.ProfileId, Name = "Hijacked" }));
        Assert.False(await _rules.DeleteOwnedAsync(bob.ProfileId, created.Id));
        Assert.False(await _rules.ReorderOwnedAsync(bob.ProfileId, [created.Id]));
        Assert.Equal("Alice's rule", (await _rules.FindOwnedAsync(alice.ProfileId, created.Id))!.Name);

        // A new order must name every rule once: a partial or repeated list would leave gaps and repeats.
        Assert.False(await _rules.ReorderOwnedAsync(alice.ProfileId, [second.Id]));
        Assert.False(await _rules.ReorderOwnedAsync(alice.ProfileId, [second.Id, second.Id]));
        Assert.Equal([created.Id, second.Id], (await _rules.ListOwnedAsync(alice.ProfileId)).Select(rule => rule.Id).ToArray());

        // Alice can reorder, replace and delete her own.
        Assert.True(await _rules.ReorderOwnedAsync(alice.ProfileId, [second.Id, created.Id]));
        Assert.Equal([second.Id, created.Id], (await _rules.ListOwnedAsync(alice.ProfileId)).Select(rule => rule.Id).ToArray());
        var replaced = await _rules.ReplaceAsync(created with { Name = "Renamed", Enabled = false });
        Assert.Equal("Renamed", replaced!.Name);
        Assert.False(replaced.Enabled);
        Assert.True(await _rules.DeleteOwnedAsync(alice.ProfileId, created.Id));
        Assert.Single(await _rules.ListOwnedAsync(alice.ProfileId));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM doc_rule_conditions WHERE rule_id = @id", created.Id));
    }

    [Fact]
    public async Task APersonalRuleOnlyCoversItsOwnersPersonalSpace()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var aliceDocument = await AddDocument(alice, 'a');
        var bobDocument = await AddDocument(bob, 'b');
        var tag = (await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Tag, "Bills", null)).Id;
        var rule = await _rules.CreateAsync(NewRule(alice, "Bills", ("filename_contains", "pdf"),
            actions: [new DocumentRuleAction(ViewDocumentRuleActionKinds.AddTag, tag, null)]));

        var aliceRules = await _filing.GetApplicableRulesAsync((await _filing.GetSubjectAsync(aliceDocument))!);
        var bobRules = await _filing.GetApplicableRulesAsync((await _filing.GetSubjectAsync(bobDocument))!);

        Assert.Equal(rule.Id, Assert.Single(aliceRules).Id);
        Assert.Empty(bobRules);
        // Disabled rules are not applied.
        await _rules.ReplaceAsync(rule with { Enabled = false });
        Assert.Empty(await _filing.GetApplicableRulesAsync((await _filing.GetSubjectAsync(aliceDocument))!));
    }

    [Fact]
    public async Task ASharedLibraryRuleOnlyCountsWhileItsMakerIsAnEnabledHouseholdAdministratorOfThatHousehold()
    {
        var household = Guid.NewGuid();
        var admin = await CreatePerson(household: household, role: "StandardUser");
        var member = await CreatePerson(household: household, role: "StandardUser");
        var child = await CreatePerson(household: household, role: "RestrictedProfile");
        var outsider = await CreatePerson(household: Guid.NewGuid(), role: "StandardUser");
        MakeAccount(admin.ProfileId, household, householdAdmin: true);
        MakeAccount(member.ProfileId, household, householdAdmin: false);
        MakeAccount(child.ProfileId, household, householdAdmin: true);
        MakeAccount(outsider.ProfileId, outsider.Household!.Value, householdAdmin: true);
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(household);
        var sharedDocument = await AddSharedDocument(shared.LibraryId, 's');
        var tag = (await _catalogue.CreateEntryAsync(admin.Viewer(), ViewCatalogueKind.Tag, "Household", null)).Id;
        DocumentRuleAction[] actions = [new DocumentRuleAction(ViewDocumentRuleActionKinds.AddTag, tag, null)];
        var byAdmin = await _rules.CreateAsync(NewRule(admin, "Admin", ("filename_contains", "pdf"), shared: true, actions: actions));
        await _rules.CreateAsync(NewRule(member, "Member", ("filename_contains", "pdf"), shared: true, actions: actions));
        await _rules.CreateAsync(NewRule(child, "Child", ("filename_contains", "pdf"), shared: true, actions: actions));
        await _rules.CreateAsync(NewRule(outsider, "Outsider", ("filename_contains", "pdf"), shared: true, actions: actions));

        var subject = (await _filing.GetSubjectAsync(sharedDocument))!;
        Assert.True(subject.IsShared);
        Assert.Equal(byAdmin.Id, Assert.Single(await _filing.GetApplicableRulesAsync(subject)).Id);

        // Demoted: the rule stops applying without being deleted.
        using (var connection = _database.CreateConnection())
        {
            // The household administrator role is the profile's: taking it away stops the rule applying.
            connection.Execute("UPDATE profiles SET is_household_admin = 0 WHERE id = @profileId;", new { profileId = admin.ProfileId });
        }

        Assert.Empty(await _filing.GetApplicableRulesAsync(subject));
        // Back as an administrator it applies again; with its sign-in switched off it stops once more.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET is_household_admin = 1 WHERE id = @profileId;", new { profileId = admin.ProfileId });
        }

        Assert.Equal(byAdmin.Id, Assert.Single(await _filing.GetApplicableRulesAsync(subject)).Id);
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE accounts SET is_enabled = 0 WHERE profile_id = @profileId;", new { profileId = admin.ProfileId });
        }

        Assert.Empty(await _filing.GetApplicableRulesAsync(subject));
        // A shared rule never reaches a Personal Space, and a personal rule never reaches the Shared Library.
        var personal = await AddDocument(admin, 'p');
        Assert.Empty(await _filing.GetApplicableRulesAsync((await _filing.GetSubjectAsync(personal))!));
        await _rules.CreateAsync(NewRule(admin, "Personal", ("filename_contains", "pdf"), actions: actions));
        Assert.Empty(await _filing.GetApplicableRulesAsync(subject));
    }

    [Fact]
    public async Task RemovingATagCorrespondentTypeOrFieldRemovesTheRuleActionsThatPointedAtIt()
    {
        var alice = await CreatePerson();
        var tag = await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Tag, "Temp", null);
        var correspondent = await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Correspondent, "Temp Co", null);
        var rule = await _rules.CreateAsync(NewRule(alice, "Temp", ("filename_contains", "x"), actions:
        [
            new DocumentRuleAction(ViewDocumentRuleActionKinds.AddTag, tag.Id, null),
            new DocumentRuleAction(ViewDocumentRuleActionKinds.SetCorrespondent, correspondent.Id, null),
        ]));

        await _catalogue.DeleteEntryAsync(alice.Viewer(), ViewCatalogueKind.Tag, tag.Id);

        var action = Assert.Single((await _rules.FindOwnedAsync(alice.ProfileId, rule.Id))!.Actions);
        Assert.Equal(ViewDocumentRuleActionKinds.SetCorrespondent, action.Kind);
    }

    // ------------------------------------------------------------ applying rules

    [Fact]
    public async Task ApplyingAPlanAddsTagsFillsEmptyValuesAndIsIdempotent()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        var tag = (await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Tag, "Bills", null)).Id;
        var correspondent = (await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Correspondent, "City Water", null)).Id;
        var type = (await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.DocumentType, "Bill", null)).Id;
        var plan = new DocumentRulePlan([tag], correspondent, type, [], false);

        var first = await _filing.ApplyPlanAsync(document, plan);
        var second = await _filing.ApplyPlanAsync(document, plan);

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        var info = _assets.Find(document)!.DocumentInfo!;
        Assert.Equal("City Water", info.Correspondent!.Name);
        Assert.Equal("Bill", info.DocumentType!.Name);
        Assert.Equal(["Bills"], info.Tags!.Select(item => item.Name).ToArray());
        // It was in the Inbox and stays there: filing is only done when a rule says so.
        Assert.Equal(ViewDocumentFilingStates.Inbox, info.FilingState);
        // The rule's values are searchable straight away.
        Assert.Single(_assets.Query(new LocalAssetQuery(alice.LibraryId, Search: "City Water", Limit: 5)).Items);
    }

    [Fact]
    public async Task AValueAPersonChoseIsNeverOverwrittenNotEvenOneTheyClearedOrOneFromBeforeRulesExisted()
    {
        var alice = await CreatePerson();
        var chosen = await AddDocument(alice, 'a');
        var cleared = await AddDocument(alice, 'b');
        var legacy = await AddDocument(alice, 'c');
        var rulesCorrespondent = (await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Correspondent, "Rule Co", null)).Id;
        var rulesType = (await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.DocumentType, "Rule Type", null)).Id;
        var plan = new DocumentRulePlan([], rulesCorrespondent, rulesType, [], false);

        // Chosen by hand in the editor.
        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), chosen, Details(correspondent: "My Choice"));
        // The person had a correspondent and then cleared it in the editor: that is a choice too.
        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), cleared, Details(correspondent: "Temporary"));
        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), cleared, Details(title: "Mine"));
        // A value written before rules (and provenance) existed has no record of who set it, so it counts as the person's.
        using (var connection = _database.CreateConnection())
        {
            var oldCorrespondent = Guid.NewGuid();
            connection.Execute("INSERT INTO doc_correspondents (id, name, created_at) VALUES (@oldCorrespondent, 'Old Co', @now);",
                new { oldCorrespondent, now = DateTimeOffset.UtcNow });
            connection.Execute("UPDATE doc_item_details SET correspondent_id = @oldCorrespondent WHERE item_id = @legacy;",
                new { oldCorrespondent, legacy });
        }

        await _filing.ApplyPlanAsync(chosen, plan);
        await _filing.ApplyPlanAsync(cleared, plan);
        await _filing.ApplyPlanAsync(legacy, plan);

        Assert.Equal("My Choice", _assets.Find(chosen)!.DocumentInfo!.Correspondent!.Name);
        // Only the correspondent was chosen; the type was empty, so a rule may fill it.
        Assert.Equal("Rule Type", _assets.Find(chosen)!.DocumentInfo!.DocumentType!.Name);
        Assert.Null(_assets.Find(cleared)!.DocumentInfo!.Correspondent);
        // The type was never set by the person (empty before and after), so a rule may still fill it.
        Assert.Equal("Rule Type", _assets.Find(cleared)!.DocumentInfo!.DocumentType!.Name);
        Assert.Equal("Old Co", _assets.Find(legacy)!.DocumentInfo!.Correspondent!.Name);
        // The legacy document had no type, so a rule may fill that gap.
        Assert.Equal("Rule Type", _assets.Find(legacy)!.DocumentInfo!.DocumentType!.Name);
    }

    [Fact]
    public async Task AValueARuleSetMayBeReplacedByARuleButOnceAPersonSavesItIsTheirs()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        var first = (await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Correspondent, "First Co", null)).Id;
        var second = (await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Correspondent, "Second Co", null)).Id;

        await _filing.ApplyPlanAsync(document, new DocumentRulePlan([], first, null, [], false));
        await _filing.ApplyPlanAsync(document, new DocumentRulePlan([], second, null, [], false));
        Assert.Equal("Second Co", _assets.Find(document)!.DocumentInfo!.Correspondent!.Name);

        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), document, Details(correspondent: "Second Co"));
        await _filing.ApplyPlanAsync(document, new DocumentRulePlan([], first, null, [], false));
        Assert.Equal("Second Co", _assets.Find(document)!.DocumentInfo!.Correspondent!.Name);
    }

    [Fact]
    public async Task CustomFieldValuesFollowTheFieldKindAndAreSkippedWhenTheyNoLongerFit()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        var text = (await _catalogue.CreateFieldAsync(alice.Viewer(), "Reference", ViewDocumentFieldKinds.Text, null)).Id;
        var money = (await _catalogue.CreateFieldAsync(alice.Viewer(), "Cost", ViewDocumentFieldKinds.Money, null)).Id;
        var choice = (await _catalogue.CreateFieldAsync(alice.Viewer(), "Status", ViewDocumentFieldKinds.Choice, ["Open", "Closed"])).Id;
        var number = (await _catalogue.CreateFieldAsync(alice.Viewer(), "Count", ViewDocumentFieldKinds.Number, null)).Id;

        var outcome = await _filing.ApplyPlanAsync(document, new DocumentRulePlan([], null, null,
        [
            new DocumentRuleFieldValue(text, "R-1"),
            new DocumentRuleFieldValue(money, "12.50 eur"),
            new DocumentRuleFieldValue(choice, "closed"),
            new DocumentRuleFieldValue(number, "not a number"),
        ], false));

        Assert.True(outcome.Changed);
        var values = _assets.Find(document)!.DocumentInfo!.FieldValues!.ToDictionary(value => value.Name);
        Assert.Equal("R-1", values["Reference"].TextValue);
        Assert.Equal(12.5, values["Cost"].NumberValue);
        Assert.Equal("EUR", values["Cost"].TextValue);
        Assert.Equal("Closed", values["Status"].TextValue);
        Assert.DoesNotContain("Count", values.Keys);

        // A person's field value is theirs.
        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), document, Details(
            fields: [new ViewDocumentFieldWrite(text, Text: "Mine")]));
        await _filing.ApplyPlanAsync(document, new DocumentRulePlan([], null, null, [new DocumentRuleFieldValue(text, "R-2")], false));
        Assert.Equal("Mine", _assets.Find(document)!.DocumentInfo!.FieldValues!.Single(value => value.Name == "Reference").TextValue);
    }

    [Fact]
    public async Task FilingAutomaticallyFilesAnInboxDocumentOnArrivalAndNeverMovesAFiledOneBack()
    {
        var alice = await CreatePerson();
        var inbox = await AddDocument(alice, 'a');
        await _filing.SaveSuggestionsAsync(inbox, new ViewDocumentSuggestionsDto(Correspondent: "Guess"));
        Assert.NotNull(_assets.Find(inbox)!.DocumentInfo!.Suggestions);

        var outcome = await _filing.ApplyPlanAsync(inbox, new DocumentRulePlan([], null, null, [], true));

        Assert.True(outcome.Filed);
        var info = _assets.Find(inbox)!.DocumentInfo!;
        Assert.Equal(ViewDocumentFilingStates.Filed, info.FilingState);
        Assert.Null(info.Suggestions);
        Assert.False((await _filing.ApplyPlanAsync(inbox, new DocumentRulePlan([], null, null, [], true))).Changed);
    }

    [Fact]
    public async Task ADocumentFromBeforeFilingExistedStaysFiledWhenARuleTagsIt()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM doc_item_details WHERE item_id = @document;", new { document });
        }

        var tag = (await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Tag, "Old", null)).Id;
        var outcome = await _filing.ApplyPlanAsync(document, new DocumentRulePlan([tag], null, null, [], false));

        Assert.True(outcome.Changed);
        Assert.True(outcome.Filed);
        Assert.Equal(ViewDocumentFilingStates.Filed, _assets.Find(document)!.DocumentInfo!.FilingState);
        Assert.Equal(0, _filing.CountInbox(alice.Viewer()));
    }

    [Fact]
    public async Task OnlyDocumentsAreEverTouchedByRules()
    {
        var alice = await CreatePerson();
        var photo = (await _assets.UpsertAsync(new LocalAssetRegistration(
            alice.LibraryId, alice.SpaceId, alice.ProfileId, LocalAssetMediaKinds.Image, "Photo", null,
            [File(@"C:\photos\a.jpg", Hash('z'), "a.jpg", "image/jpeg")]))).ItemId;
        var tag = (await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Tag, "Nope", null)).Id;

        Assert.Null(await _filing.GetSubjectAsync(photo));
        var outcome = await _filing.ApplyPlanAsync(photo, new DocumentRulePlan([tag], null, null, [], true));
        Assert.False(outcome.Changed);
        Assert.Empty(_assets.Find(photo)!.Tags);
    }

    // -------------------------------------------------------------- try-out

    [Fact]
    public async Task ATryOutOnlyEverSeesDocumentsThePersonCanOpen()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var visible = await AddDocument(alice, 'a');
        var hiddenByHousemate = await AddDocument(bob, 'b');
        await _assets.SetFlagsAsync(hiddenByHousemate, null, true);

        // Alice can reach her own library and (as a housemate) Bob's: yet Bob's hidden item stays out of reach.
        var wide = new ViewCatalogueViewer(alice.ProfileId, [alice.LibraryId, bob.LibraryId]);
        Assert.Equal([visible], (await _filing.GetSubjectsPageAsync(wide, alice.LibraryId, 0, 50)).Select(item => item.ItemId).ToArray());
        Assert.Empty(await _filing.GetSubjectsPageAsync(wide, bob.LibraryId, 0, 50));
        // A library she cannot reach yields nothing, even when named.
        Assert.Empty(await _filing.GetSubjectsPageAsync(alice.Viewer(), bob.LibraryId, 0, 50));
        // A profile without Documents sees nothing.
        Assert.Empty(await _filing.GetSubjectsPageAsync(
            new ViewCatalogueViewer(alice.ProfileId, [alice.LibraryId], IncludeDocuments: false), alice.LibraryId, 0, 50));
        // The owner of a hidden item still sees their own.
        Assert.Single(await _filing.GetSubjectsPageAsync(
            new ViewCatalogueViewer(bob.ProfileId, [bob.LibraryId]), bob.LibraryId, 0, 50));
    }

    // ----------------------------------------------------------------- inbox

    [Fact]
    public async Task TheInboxCountsWhatTheListShowsAndNothingElse()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var first = await AddDocument(alice, 'a');
        await AddDocument(alice, 'b');
        var hidden = await AddDocument(alice, 'c');
        var trashed = await AddDocument(alice, 'd');
        await AddDocument(bob, 'e');
        await _assets.SetFlagsAsync(hidden, null, true);
        await _assets.SetLifecycleStateAsync(trashed, LocalAssetLifecycleState.Trashed);

        Assert.Equal(2, _filing.CountInbox(alice.Viewer()));
        Assert.Equal(2, _assets.QueryTimeline(new LocalAssetTimelineQuery(
            [alice.LibraryId], MediaKinds: [LocalAssetMediaKinds.Document],
            DocumentFilter: new LocalAssetDocumentFilter(FilingState: "inbox"))).Items.Count);
        Assert.Equal(3, _filing.CountInbox(new ViewCatalogueViewer(alice.ProfileId, [alice.LibraryId, bob.LibraryId])));
        Assert.Equal(0, _filing.CountInbox(new ViewCatalogueViewer(alice.ProfileId, [alice.LibraryId], IncludeDocuments: false)));
        Assert.Equal(0, _filing.CountInbox(new ViewCatalogueViewer(alice.ProfileId, [])));

        await _filing.FileFromInboxAsync(alice.Viewer(), first, new FileInboxWrite(false, null, null, null, null));
        Assert.Equal(1, _filing.CountInbox(alice.Viewer()));
    }

    [Fact]
    public async Task FilingFromTheInboxWithoutChangesJustMarksItFiledAndRepeatingItIsHarmless()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        await _filing.SaveSuggestionsAsync(document, new ViewDocumentSuggestionsDto(
            new DateOnly(2026, 3, 1), false, "Guess Co", "Bill", ["Guess"]));

        Assert.True(await _filing.FileFromInboxAsync(alice.Viewer(), document, new FileInboxWrite(false, null, null, null, null)));
        Assert.True(await _filing.FileFromInboxAsync(alice.Viewer(), document, new FileInboxWrite(true, null, null, null, null)));

        var info = _assets.Find(document)!.DocumentInfo!;
        Assert.Equal(ViewDocumentFilingStates.Filed, info.FilingState);
        Assert.Null(info.Correspondent);
        Assert.Null(info.DocumentType);
        Assert.Empty(info.Tags!);
        Assert.Null(info.DocumentDate);
        Assert.Null(info.Suggestions);
    }

    [Fact]
    public async Task AcceptingSuggestionsWritesThemAsThePersonsOwnAndKeepsWhatIsAlreadySet()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        var existing = (await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.DocumentType, "Contract", null)).Id;
        var amount = (await _catalogue.CreateFieldAsync(alice.Viewer(), "Amount", ViewDocumentFieldKinds.Money, null)).Id;
        await _filing.ApplyPlanAsync(document, new DocumentRulePlan([], null, existing, [], false));
        await _filing.SaveSuggestionsAsync(document, new ViewDocumentSuggestionsDto(
            new DateOnly(2026, 3, 1), false, "City Water", "Bill", ["Home", "Utilities"], 120.5, "EUR"));

        Assert.True(await _filing.FileFromInboxAsync(alice.Viewer(), document, new FileInboxWrite(true, null, null, null, null)));

        var info = _assets.Find(document)!.DocumentInfo!;
        Assert.Equal(ViewDocumentFilingStates.Filed, info.FilingState);
        Assert.Equal(new DateOnly(2026, 3, 1), info.DocumentDate);
        Assert.Equal("City Water", info.Correspondent!.Name);
        // The rule's type was already there, so the suggested "Bill" is not applied over it.
        Assert.Equal("Contract", info.DocumentType!.Name);
        Assert.Equal(["Home", "Utilities"], info.Tags!.Select(tag => tag.Name).OrderBy(name => name).ToArray());
        var value = _assets.Find(document)!.DocumentInfo!.FieldValues!.Single();
        Assert.Equal(amount, value.FieldId);
        Assert.Equal(120.5, value.NumberValue);
        Assert.Equal("EUR", value.TextValue);
        Assert.Null(info.Suggestions);

        // Accepting made them the person's: a rule can no longer change the correspondent.
        var other = (await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Correspondent, "Other", null)).Id;
        Assert.False((await _filing.ApplyPlanAsync(document, new DocumentRulePlan([], other, null, [], false))).Changed);
    }

    [Fact]
    public async Task APersonsOwnEditsWinOverTheSuggestionAndAFileDateIsNeverWrittenAsTheDocumentDate()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        await _filing.SaveSuggestionsAsync(document, new ViewDocumentSuggestionsDto(
            new DateOnly(2026, 9, 1), true, "Guess Co", "Guess Type", ["Guess"]));

        await _filing.FileFromInboxAsync(alice.Viewer(), document,
            new FileInboxWrite(true, new DateOnly(2025, 12, 24), "Real Co", null, ["Real"]));

        var info = _assets.Find(document)!.DocumentInfo!;
        Assert.Equal(new DateOnly(2025, 12, 24), info.DocumentDate);
        Assert.Equal("Real Co", info.Correspondent!.Name);
        Assert.Equal("Guess Type", info.DocumentType!.Name);
        Assert.Equal(["Real"], info.Tags!.Select(tag => tag.Name).ToArray());

        var second = await AddDocument(alice, 'b');
        await _filing.SaveSuggestionsAsync(second, new ViewDocumentSuggestionsDto(new DateOnly(2026, 9, 1), true));
        await _filing.FileFromInboxAsync(alice.Viewer(), second, new FileInboxWrite(true, null, null, null, null));
        Assert.Null(_assets.Find(second)!.DocumentInfo!.DocumentDate);
    }

    [Fact]
    public async Task NobodyCanFileSomeoneElsesDocumentOrOneOutsideTheirLibraries()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var aliceDocument = await AddDocument(alice, 'a');
        var photo = (await _assets.UpsertAsync(new LocalAssetRegistration(
            bob.LibraryId, bob.SpaceId, bob.ProfileId, LocalAssetMediaKinds.Image, "Photo", null,
            [File(@"C:\p\z.jpg", Hash('y'), "z.jpg", "image/jpeg")]))).ItemId;

        Assert.False(await _filing.FileFromInboxAsync(bob.Viewer(), aliceDocument, new FileInboxWrite(true, null, "Sneaky", null, ["Sneaky"])));
        Assert.False(await _filing.FileFromInboxAsync(bob.Viewer(), photo, new FileInboxWrite(false, null, null, null, null)));
        Assert.False(await _filing.FileFromInboxAsync(
            new ViewCatalogueViewer(alice.ProfileId, [alice.LibraryId], IncludeDocuments: false), aliceDocument,
            new FileInboxWrite(false, null, null, null, null)));
        Assert.False(await _filing.FileFromInboxAsync(alice.Viewer(), Guid.NewGuid(), new FileInboxWrite(false, null, null, null, null)));
        Assert.Equal(ViewDocumentFilingStates.Inbox, _assets.Find(aliceDocument)!.DocumentInfo!.FilingState);
        Assert.Empty(_catalogue.GetCatalogue(bob.Viewer()).Correspondents);
    }

    [Fact]
    public async Task SavingTheEditorByHandClearsSuggestionsOnceTheDocumentIsFiled()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        await _filing.SaveSuggestionsAsync(document, new ViewDocumentSuggestionsDto(Correspondent: "Guess"));

        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), document, Details(title: "Still waiting", state: "inbox"));
        Assert.NotNull(_assets.Find(document)!.DocumentInfo!.Suggestions);

        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), document, Details(title: "Done", state: "filed"));
        Assert.Null(_assets.Find(document)!.DocumentInfo!.Suggestions);
        Assert.Equal(0, _filing.CountInbox(alice.Viewer()));
    }

    // ---------------------------------------------------------------- corpus

    [Fact]
    public async Task TheCorpusOnlyLearnsFromFiledOrganisedDocumentsOfTheSameLibrary()
    {
        var alice = await CreatePerson();
        var bob = await CreatePerson();
        var billA = await AddDocument(alice, 'a', "Acme Water statement for Maple Street");
        var billB = await AddDocument(alice, 'b', "Acme Water statement number two for Maple Street");
        var inbox = await AddDocument(alice, 'c', "Acme Water statement waiting in the inbox");
        await AddDocument(alice, 'd', "Garden compost notes only");
        var bobs = await AddDocument(bob, 'e', "Acme Water statement from a stranger");
        foreach (var (document, who, correspondent) in new[] { (billA, alice, "Acme Water"), (billB, alice, "Acme Water"), (bobs, bob, "Private Firm") })
        {
            await _catalogue.UpdateDocumentDetailsAsync(who.Viewer(), document, Details(
                correspondent: correspondent, documentType: "Bill", tagNames: ["Home"], state: "filed"));
        }

        Assert.Equal(2, await _filing.CountOrganisedDocumentsAsync(alice.LibraryId));
        var counts = await _filing.GetWordDocumentCountsAsync(alice.LibraryId, ["acme", "maple", "compost", "inbox", "zebra", "bad\"word"]);
        Assert.Equal(2, counts["acme"]);
        Assert.Equal(2, counts["maple"]);
        Assert.DoesNotContain("compost", counts.Keys);
        Assert.DoesNotContain("inbox", counts.Keys);
        Assert.DoesNotContain("zebra", counts.Keys);

        var similar = await _filing.FindSimilarDocumentsAsync(alice.LibraryId, inbox, ["acme", "maple", "statement"], 5);
        Assert.Equal(new[] { billA, billB }.Order(), similar.Select(item => item.ItemId).Order());
        Assert.All(similar, item =>
        {
            Assert.Equal("Acme Water", item.Correspondent);
            Assert.Equal("Bill", item.DocumentType);
            Assert.Equal(["Home"], item.Tags);
        });
        // Nothing from Bob's library ever shows up in Alice's suggestions.
        Assert.DoesNotContain(similar, item => item.ItemId == bobs || item.Correspondent == "Private Firm");
        // A document is never its own neighbour.
        Assert.DoesNotContain(await _filing.FindSimilarDocumentsAsync(alice.LibraryId, billA, ["acme"], 5), item => item.ItemId == billA);
        Assert.Empty(await _filing.FindSimilarDocumentsAsync(alice.LibraryId, inbox, ["zebra"], 5));
    }

    [Fact]
    public async Task TheAmountFieldIsOnlyFoundByItsNameAndKind()
    {
        var alice = await CreatePerson();
        Assert.Null(await _filing.FindAmountFieldAsync(alice.LibraryId, alice.ProfileId));
        await _catalogue.CreateFieldAsync(alice.Viewer(), "Amount", ViewDocumentFieldKinds.Text, null);
        Assert.Null(await _filing.FindAmountFieldAsync(alice.LibraryId, alice.ProfileId));
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM doc_field_definitions;");
        }

        var money = await _catalogue.CreateFieldAsync(alice.Viewer(), "amount", ViewDocumentFieldKinds.Money, null);
        var found = await _filing.FindAmountFieldAsync(alice.LibraryId, alice.ProfileId);
        Assert.Equal(money.Id, found!.FieldId);
        Assert.Equal(ViewDocumentFieldKinds.Money, found.Kind);

        // Somebody else's Amount field is invisible to Alice until it is used on a document she can see.
        var bob = await CreatePerson();
        Assert.Null(await _filing.FindAmountFieldAsync(bob.LibraryId, bob.ProfileId));
        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), await AddDocument(alice, 'r'), Details(
            fields: [new ViewDocumentFieldWrite(money.Id, Text: "EUR", Number: 5)]));
        Assert.Null(await _filing.FindAmountFieldAsync(bob.LibraryId, bob.ProfileId));
        Assert.NotNull(await _filing.FindAmountFieldAsync(alice.LibraryId, alice.ProfileId));
    }

    [Fact]
    public async Task FilingTakesExactlyWhatThePersonWasShownEvenIfTheStoredSuggestionsChangedMeanwhile()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        var amount = (await _catalogue.CreateFieldAsync(alice.Viewer(), "Amount", ViewDocumentFieldKinds.Money, null)).Id;
        // The card showed a date only; then text recognition finished and stored more.
        await _filing.SaveSuggestionsAsync(document, new ViewDocumentSuggestionsDto(
            new DateOnly(2026, 3, 1), false, "City Water", "Bill", ["Home"], 120.5, "EUR"));
        var shown = new ViewDocumentSuggestionsDto(new DateOnly(2026, 3, 1), false, Tags: ["Kept"], Amount: 42.1, Currency: "EUR");

        await _filing.FileFromInboxAsync(alice.Viewer(), document, new FileInboxWrite(true, null, null, null, null, shown));

        var info = _assets.Find(document)!.DocumentInfo!;
        Assert.Null(info.Correspondent);
        Assert.Null(info.DocumentType);
        Assert.Equal(["Kept"], info.Tags!.Select(tag => tag.Name).ToArray());
        var value = Assert.Single(info.FieldValues!);
        Assert.Equal(amount, value.FieldId);
        Assert.Equal(42.1, value.NumberValue);
        Assert.Equal(new DateOnly(2026, 3, 1), info.DocumentDate);
    }

    [Fact]
    public async Task EditingOnlyTheTitleBeforeRulesHaveRunDoesNotStopThemFillingTheRestIn()
    {
        var alice = await CreatePerson();
        var document = await AddDocument(alice, 'a');
        var correspondent = (await _catalogue.CreateEntryAsync(alice.Viewer(), ViewCatalogueKind.Correspondent, "Rule Co", null)).Id;
        var field = (await _catalogue.CreateFieldAsync(alice.Viewer(), "Reference", ViewDocumentFieldKinds.Text, null)).Id;

        await _catalogue.UpdateDocumentDetailsAsync(alice.Viewer(), document, Details(
            title: "Better title", state: "inbox", fields: [new ViewDocumentFieldWrite(field)]));
        await _filing.ApplyPlanAsync(document, new DocumentRulePlan([], correspondent, null, [new DocumentRuleFieldValue(field, "R-9")], false));

        var info = _assets.Find(document)!.DocumentInfo!;
        Assert.Equal("Rule Co", info.Correspondent!.Name);
        Assert.Equal("R-9", Assert.Single(info.FieldValues!).TextValue);
    }

    [Fact]
    public async Task AFreshDataStoreHasTheSuggestionsColumnAndTheFilingTablesUseBlobIds()
    {
        using var check = _database.CreateConnection();
        Assert.True(HasColumn(check, "doc_item_details", "suggestions_json"));
        foreach (var (table, column) in new[] { ("doc_rules", "id"), ("doc_rules", "owner_profile_id"), ("doc_rule_conditions", "rule_id"),
                     ("doc_rule_actions", "rule_id"), ("doc_rule_actions", "ref_id"), ("doc_item_field_sources", "item_id") })
        {
            Assert.Equal("BLOB", check.QuerySingle<string>(
                "SELECT type FROM pragma_table_info(@table) WHERE name = @column;", new { table, column }));
        }

        await Task.CompletedTask;
    }

    // --------------------------------------------------------------- helpers

    private static bool HasColumn(Microsoft.Data.Sqlite.SqliteConnection connection, string table, string column) =>
        connection.ExecuteScalar<long>("SELECT COUNT(*) FROM pragma_table_info(@table) WHERE name = @column;", new { table, column }) > 0;

    private int Count(string sql, Guid id)
    {
        using var connection = _database.CreateConnection();
        return connection.ExecuteScalar<int>(sql, new { id });
    }

    private static DocumentRule NewRule(
        Person owner, string name, (string Kind, string Value) condition, bool shared = false, DocumentRuleAction[]? actions = null) =>
        new(Guid.Empty, owner.ProfileId, shared ? null : owner.SpaceId, name, true, "all", 0, false,
            [new DocumentRuleCondition(condition.Kind, condition.Value)], actions ?? []);

    private static ViewDocumentDetailsWrite Details(
        string? title = null, string? correspondent = null, string? documentType = null,
        IReadOnlyCollection<string>? tagNames = null, string? state = null,
        IReadOnlyCollection<ViewDocumentFieldWrite>? fields = null) =>
        new(title, null, correspondent, documentType, null, null, state, tagNames ?? [], [], fields);

    private sealed record Person(Guid ProfileId, Guid SpaceId, Guid LibraryId, Guid? Household = null)
    {
        public ViewCatalogueViewer Viewer() => new(ProfileId, [LibraryId]);
    }

    private async Task<Person> CreatePerson(Guid? household = null, string role = "StandardUser")
    {
        var profileId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            if (household is { } householdId)
            {
                connection.Execute("INSERT OR IGNORE INTO households (id, name, created_at) VALUES (@householdId, 'Home', @now);",
                    new { householdId, now = DateTimeOffset.UtcNow });
            }

            connection.Execute("""
                INSERT INTO profiles (id, display_name, avatar_color, role, created_at, household_id)
                VALUES (@profileId, @name, '#7C4DFF', @role, @now, @household);
                """, new { profileId, name = $"Profile {profileId:N}", role, now = DateTimeOffset.UtcNow, household });
        }

        var space = await _spaces.CreateAsync(profileId, libraryId);
        return new Person(profileId, space.Id, libraryId, household);
    }

    private Guid MakeAccount(Guid profileId, Guid household, bool householdAdmin)
    {
        var account = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO accounts (id, is_enabled, is_administrator, authorization_version, created_at, updated_at, household_id, household_admin, profile_id)
            VALUES (@account, 1, 0, 1, @now, @now, @household, @householdAdmin, @profileId);
            INSERT INTO account_profile_grants (account_id, profile_id, is_default, is_enabled, admin_enabled, authorization_version, granted_at)
            VALUES (@account, @profileId, 1, 1, 0, 1, @now);
            UPDATE profiles SET is_household_admin = @householdAdmin WHERE id = @profileId;
            """, new { account, profileId, household, householdAdmin = householdAdmin ? 1 : 0, now = DateTimeOffset.UtcNow });
        return account;
    }

    private async Task<Guid> AddDocument(Person person, char seed, string? text = null) =>
        (await _assets.UpsertAsync(new LocalAssetRegistration(
            person.LibraryId, person.SpaceId, person.ProfileId,
            LocalAssetMediaKinds.Document, $"Document {seed}", null,
            [File($@"C:\docs\{person.ProfileId:N}\{seed}.pdf", Hash(seed), $"{seed}.pdf", "application/pdf")],
            DocumentText: text))).ItemId;

    private async Task<Guid> AddSharedDocument(Guid libraryId, char seed)
    {
        var sourceId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO view_sources
                    (id, scope_kind, personal_space_id, library_id, source_type, name, source_key, storage_mode,
                     relative_path, external_path, include_subdirectories, enabled, created_at, updated_at)
                VALUES (@sourceId, 'shared', NULL, @libraryId, 'folder', 'Shared folder', @sourceKey, 'managed',
                        'Shared/Folders/test', NULL, 1, 1, @now, @now);
                """, new { sourceId, libraryId, sourceKey = $"shared:{sourceId:N}", now = DateTimeOffset.UtcNow });
        }

        return (await _assets.UpsertAsync(new LocalAssetRegistration(
            libraryId, null, null, LocalAssetMediaKinds.Document, $"Shared {seed}", null,
            [File($@"C:\shared\{seed}.pdf", Hash(seed), $"{seed}.pdf", "application/pdf") with { SourceId = sourceId }],
            ScopeKind: LocalAssetScopeKinds.Shared))).ItemId;
    }

    private static LocalAssetFileRegistration File(string path, string hash, string fileName, string mimeType) =>
        new(path, hash, fileName, mimeType, 1024, DateTimeOffset.UtcNow);

    private static string Hash(char character) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(character.ToString()))).ToLowerInvariant();

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
