using System.Reflection;
using MediaEngine.Api.Realtime;
using MediaEngine.Api.Security;
using MediaEngine.Application.Services;
using MediaEngine.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Every read for a viewer goes through the one access service (Vault sections spec D32, WP0). These tests fail the build when a read
/// path with a viewer skips it, and when a read path with no viewer (the outbox, background jobs, raw reads) stops keeping the Vault
/// out with the Vault SQL predicates. Three things are pinned:
/// <list type="number">
/// <item>every GET route a person can call is either checked per resource, or listed in <c>ViewerReadRouteInventory.txt</c> with how it
/// keeps hidden things out, and the file named there still uses that mechanism;</item>
/// <item>only reviewed files read the people credit records or build a viewer's catalogue, and each reviewed file shows its evidence
/// (it takes the viewer's catalogue, or it carries the Vault predicate, or it is scoped by a viewer-aware list);</item>
/// <item>the reads that return people and counts cannot be called without a viewer's catalogue, and live events that name a person are
/// judged by where the person is credited.</item>
/// </list>
/// </summary>
public sealed class ViewerAccessGuardrailTests
{
    private const string InventoryFile = "tests/MediaEngine.Api.Tests/ViewerReadRouteInventory.txt";
    private const string ApiRoot = "src/MediaEngine.Api/";

    /// <summary>The evidence each mechanism must show in the file it names: any one of these words.</summary>
    private static readonly Dictionary<string, string[]> Mechanisms = new(StringComparer.Ordinal)
    {
        ["viewer-access-service"] = ["IViewerAccessService"],
        ["display-projection"] = ["IDisplayProjectionReadService", "IViewerAccessService", "CatalogueResourceAuthorizationService", "ComposerService"],
        ["catalogue-authorization"] = ["RequireCatalogue", "CatalogueResourceAuthorizationService", "PlayerCatalogueScope"],
        ["personal-scope"] = ["PersonalScopeAuthorizer"],
        ["view-scope"] = ["ViewScopeRequest", "ViewScopeResolver", "ViewScopeKind"],
        ["vault-area"] = ["VaultGatedRoute", "VaultGateMiddleware", "VaultAccessResolver"],
        ["own-data"] = [],
        ["system"] = [],
        ["plugin"] = [],
        ["grant"] = [],
    };

    // Files that name the people credit records or the viewer's catalogue. Each is reviewed with the mechanism that keeps the Vault, hidden
    // libraries and the content limit out. Do not add a file to get a build through: take the catalogue, add the Vault predicate, or scope the
    // read by a viewer-aware list, and say which here.
    private static readonly Dictionary<string, (string Mechanism, string Why)> ReviewedReaders = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Api/Services/ReadServices/PersonReadServices.cs"] = ("viewer-catalogue", "people and presence reads take the viewer's catalogue"),
        ["src/MediaEngine.Api/Services/ReadServices/LinkedPersonVisibility.cs"] = ("viewer-catalogue", "people reached through a link are shown only when the viewer could open them"),
        ["src/MediaEngine.Api/Endpoints/PersonEndpoints.cs"] = ("viewer-catalogue", "asks the access service for the catalogue and passes it on"),
        ["src/MediaEngine.Api/Services/Display/ContributorShelfReadService.cs"] = ("viewer-scoped", "credits are kept only for files of the authorized works list"),
        ["src/MediaEngine.Api/Services/ReadServices/CollectionBrowseReadService.cs"] = ("vault-predicate", "raw reads carry the Vault predicate; grouped reads are scoped by the viewer's work ids"),
        ["src/MediaEngine.Api/Services/ReadServices/ArtworkLibraryReadService.cs"] = ("vault-predicate", "artwork library (administrators): counts and people carry the Vault predicate"),
        ["src/MediaEngine.Api/Services/Details/Internals/DetailCompositionOrchestrator.Collections.cs"] = ("vault-predicate", "the albums offered under an artist carry the Vault predicate"),
        ["src/MediaEngine.Api/Services/ReadServices/IngestionPresentationReadService.cs"] = ("vault-predicate", "administrator operations: every count carries the Vault predicate"),
        ["src/MediaEngine.Api/Security/CatalogueResourceAuthorization.cs"] = ("resource-check", "asks which files credit a person, then authorizes each file for the caller"),
        ["src/MediaEngine.Api/Services/ArtworkAssetService.cs"] = ("administrator-only", "artwork library search, reached only from administrator routes (open item: Vault titles in related search)"),
        ["src/MediaEngine.Api/Services/Details/ContributorReadiness.cs"] = ("internal", "marks credits still updating for a detail page; reveals no name or count"),
        ["src/MediaEngine.Api/DevSupport/BaselineExport.cs"] = ("development", "development-only baseline export"),
        ["src/MediaEngine.Api/DevSupport/BaselineWait.cs"] = ("development", "development-only baseline wait"),
        ["src/MediaEngine.Api/DependencyInjection/ApiReadServiceCollectionExtensions.cs"] = ("registration", "registers the people read services"),
        ["src/MediaEngine.Providers/Workers/PersonEnrichmentWorker.cs"] = ("background", "background enrichment: no viewer, publishes only classified events"),
        ["src/MediaEngine.Domain/Contracts/IPersonRepository.cs"] = ("contract", "the repository contract"),
        ["src/MediaEngine.Domain/Contracts/IRecursiveIdentityService.cs"] = ("contract", "documents what ingestion writes"),
        ["src/MediaEngine.Application/Services/IReadServices.cs"] = ("contract", "the read service contracts; each people read takes a viewer catalogue"),
        ["src/MediaEngine.Storage/PersonRepository.cs"] = ("vault-predicate", "the presence credits carry the Vault predicate"),
        ["src/MediaEngine.Storage/PersonCreditSql.cs"] = ("owner", "the credit SQL building block"),
        ["src/MediaEngine.Storage/CollectionRuleEvaluator.cs"] = ("vault-predicate", "rule evaluation carries the Vault predicate"),
        ["src/MediaEngine.Storage/VaultLibraryRepository.cs"] = ("owner", "decides whether a person is known only from Vault files"),
        ["src/MediaEngine.Storage/Backup/BackupTableClassification.cs"] = ("owner", "every table is classified for backup by name"),
        ["src/MediaEngine.Storage/Schema/schema.snapshot.sql"] = ("owner", "the generated data structure"),
        ["src/MediaEngine.Storage/Schema/Upgrades/0001_baseline.sql"] = ("owner", "the step that created the records"),
    };

    // Files that may build a ViewerCatalogue: the access service, and the tests that stand one up.
    private static readonly HashSet<string> MayBuildACatalogue = new(StringComparer.Ordinal)
    {
        "src/MediaEngine.Api/Security/ViewerAccessService.cs",
        "src/MediaEngine.Application/Services/ViewerCatalogue.cs",
    };

    private static readonly string[] CreditRecords = ["primary_person_media_credits", "person_media_links"];

    private static readonly string[] PeopleReads =
    [
        "GetPresenceBatchAsync", "GetPresenceCreditsAsync", "GetCreditedFilesAsync", "IPersonPresenceReadService", "IPersonAssetScopeReadService",
        "PersonPresenceReadService", "PersonAssetScopeReadService", "GetPresenceAsync", "GetCanonicalPersonIdsAsync", "GetCanonicalRoleCountsAsync",
    ];

    // ── Routes ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EveryViewerReadRoute_IsCheckedPerResource_OrListedInTheInventory()
    {
        await using var app = MappedEndpointInventoryTests.BuildEndpointOnlyApplication();
        var routes = ViewerReadRoutes(app);
        var rules = ReadInventory();

        var unlisted = FindUnlistedRoutes(routes, rules);

        Assert.True(unlisted.Count == 0,
            "These GET routes can be called by a viewer and neither check the resource nor appear in " + InventoryFile
            + ". Route them through IViewerAccessService, the authorized display projection or a catalogue check, then list them with the mechanism: "
            + string.Join(", ", unlisted));
    }

    // Every route under /persons, one by one. A route that returns people, counts or the people linked to a person (aliases, group
    // members) must hand its handler the viewer's catalogue from IViewerAccessService; a route that returns no other person says why.
    // A new /persons route fails until it is added to one of these two lists.
    private static readonly string[] PersonRoutesThatAskTheViewerAccessService =
    [
        "/", "/presence", "/role-counts", "/by-collection/{collectionId:guid}", "/by-work/{workId:guid}",
        "/{id:guid}", "/{id:guid}/aliases", "/{id:guid}/library-credits", "/{id:guid}/works",
    ];

    private static readonly string[] PersonRoutesThatReturnNoOtherPerson =
    [
        "/{id:guid}/editor", "/{id:guid}/artwork", "/{id:guid}/headshot",
    ];

    [Fact]
    public void PersonRoutesAreReviewedOneByOne_AndTheLinkedPeopleOnesCallTheViewerAccessService()
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Api", "Endpoints", "PersonEndpoints.cs"));

        var problems = FindUnreviewedPersonRoutes(text);

        Assert.True(problems.Count == 0, "PersonEndpoints.cs changed without review: " + string.Join("; ", problems));
    }

    [Fact]
    public void ThePersonRouteScanner_FlagsANewRoute_AndAViewerRouteThatNeverCallsTheService()
    {
        var problems = FindUnreviewedPersonRoutes(
            "group.MapGet(\"/{id:guid}/relatives\", async () => { var x = 1; })\n"
            + "group.MapGet(\"/presence\", async (IViewerAccessService viewerAccess) => { var viewer = 1; })\n"
            + "group.MapGet(\"/{id:guid}/aliases\", async (IViewerAccessService viewerAccess) => { await viewerAccess.GetCatalogueAsync(ct); })\n");

        Assert.Contains(problems, problem => problem.Contains("/relatives", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("/presence", StringComparison.Ordinal) && problem.Contains("viewerAccess", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, problem => problem.Contains("/aliases", StringComparison.Ordinal) && problem.Contains("viewerAccess", StringComparison.Ordinal));
    }

    private static List<string> FindUnreviewedPersonRoutes(string text)
    {
        var problems = new List<string>();
        var matches = System.Text.RegularExpressions.Regex.Matches(text, "group\\.MapGet\\(\"(?<route>[^\"]*)\"");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < matches.Count; index++)
        {
            var route = matches[index].Groups["route"].Value;
            seen.Add(route);
            var end = index + 1 < matches.Count ? matches[index + 1].Index : text.Length;
            var handler = text[matches[index].Index..end];
            if (PersonRoutesThatAskTheViewerAccessService.Contains(route))
            {
                if (!handler.Contains("viewerAccess.Get", StringComparison.Ordinal))
                {
                    problems.Add($"{route} is reviewed as asking the access service but its handler never calls viewerAccess.Get...");
                }
            }
            else if (!PersonRoutesThatReturnNoOtherPerson.Contains(route))
            {
                problems.Add($"{route} is a new /persons route; review it and list it");
            }
        }

        // Only the real file maps them all; the seeded samples above map a few.
        if (matches.Count >= PersonRoutesThatAskTheViewerAccessService.Length)
        {
            foreach (var route in PersonRoutesThatAskTheViewerAccessService.Concat(PersonRoutesThatReturnNoOtherPerson).Where(route => !seen.Contains(route)))
            {
                problems.Add($"{route} is listed but no longer mapped");
            }
        }

        return problems;
    }

    [Fact]
    public async Task EveryInventoryRule_StillMatchesARoute_AndItsFileStillUsesItsMechanism()
    {
        await using var app = MappedEndpointInventoryTests.BuildEndpointOnlyApplication();
        var routes = ViewerReadRoutes(app);
        var rules = ReadInventory();
        var root = FindRepoRoot();

        var problems = FindStaleRules(routes, rules, file =>
        {
            var path = Path.Combine(root, (ApiRoot + file).Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) ? File.ReadAllText(path) : null;
        });

        Assert.True(problems.Count == 0, "The viewer read route inventory is out of date: " + string.Join("; ", problems));
    }

    [Fact]
    public void TheRouteScanner_FlagsAnUnlistedViewerRoute_AndAcceptsOneThatChecksTheResourceOrIsListed()
    {
        var rules = new List<InventoryRule> { new("/known/*", "display-projection", ["Endpoints/Known.cs"], "listed") };
        var routes = new List<RouteFact>
        {
            new("/known/list", Administrative: false, ResourceChecked: false),
            new("/brand-new/people-counts", Administrative: false, ResourceChecked: false),
            new("/checked/{id}", Administrative: false, ResourceChecked: true),
            new("/admin/anything", Administrative: true, ResourceChecked: false),
        };

        var unlisted = FindUnlistedRoutes(routes, rules);

        Assert.Equal(["/brand-new/people-counts"], unlisted);
    }

    [Fact]
    public void TheRouteScanner_FlagsARuleWithNoRoute_AUnknownMechanism_AndAFileThatLostItsMechanism()
    {
        var rules = new List<InventoryRule>
        {
            new("/gone/*", "own-data", ["Endpoints/Gone.cs"], "no route any more"),
            new("/odd", "made-up-mechanism", ["Endpoints/Odd.cs"], "unknown"),
            new("/people", "viewer-access-service", ["Endpoints/People.cs"], "claims the service"),
        };
        var routes = new List<RouteFact>
        {
            new("/odd", false, false),
            new("/people", false, false),
        };

        var problems = FindStaleRules(routes, rules, file => file == "Endpoints/People.cs" ? "no word of the service here" : "x");

        Assert.Contains(problems, problem => problem.Contains("/gone/*", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("made-up-mechanism", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("/people", StringComparison.Ordinal) && problem.Contains("IViewerAccessService", StringComparison.Ordinal));
    }

    // ── Source files ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void OnlyReviewedFiles_ReadThePeopleCreditRecords_OrTheViewerScopedPeopleReads_OrBuildACatalogue()
    {
        var offences = FindSourceOffences(ReadSourceFiles());

        Assert.True(offences.Count == 0,
            "A read of people, credits or counts must start from the viewer's catalogue (IViewerAccessService) or, with no viewer, carry the Vault SQL predicate. Fix these, do not widen the list: "
            + string.Join("; ", offences));
    }

    [Fact]
    public void TheSourceScanner_FlagsAnUnreviewedCreditRead_AUnguardedReviewedFile_AndACatalogueBuiltOutsideTheService()
    {
        var offences = FindSourceOffences(
        [
            ("src/MediaEngine.Api/Services/ReadServices/NewCountsReadService.cs", "SELECT COUNT(*) FROM primary_person_media_credits"),
            ("src/MediaEngine.Api/Services/ReadServices/PersonReadServices.cs", "SELECT 1 FROM primary_person_media_credits"),
            ("src/MediaEngine.Storage/PersonRepository.cs", "SELECT 1 FROM primary_person_media_credits"),
            ("src/MediaEngine.Api/Endpoints/SomeEndpoints.cs", "var viewer = new ViewerCatalogue(files);"),
            ("src/MediaEngine.Api/Endpoints/OtherEndpoints.cs", "await presence.GetPresenceAsync(ids, ct);"),
        ]);

        Assert.Contains(offences, offence => offence.Contains("NewCountsReadService.cs", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("PersonReadServices.cs", StringComparison.Ordinal) && offence.Contains("viewer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(offences, offence => offence.Contains("PersonRepository.cs", StringComparison.Ordinal) && offence.Contains("Vault", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("SomeEndpoints.cs", StringComparison.Ordinal) && offence.Contains("ViewerCatalogue", StringComparison.Ordinal));
        Assert.Contains(offences, offence => offence.Contains("OtherEndpoints.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSourceScanner_AcceptsTheReviewedReaders_WithTheirEvidence()
    {
        var offences = FindSourceOffences(
        [
            ("src/MediaEngine.Api/Services/ReadServices/PersonReadServices.cs", "ViewerCatalogue viewer; FROM primary_person_media_credits; GetPresenceCreditsAsync"),
            ("src/MediaEngine.Storage/PersonRepository.cs", "FROM primary_person_media_credits AND VaultSql.NotVaultAsset(\"ma.id\")"),
            ("src/MediaEngine.Api/Security/ViewerAccessService.cs", "return new ViewerCatalogue(files);"),
        ]);

        Assert.Empty(offences);
    }

    [Fact]
    public void TheRealSourceTree_IsScanned()
    {
        var files = ReadSourceFiles();

        Assert.Contains(files, file => file.Path == "src/MediaEngine.Api/Services/ReadServices/PersonReadServices.cs");
        Assert.Contains(files, file => file.Path == "src/MediaEngine.Api/Security/ViewerAccessService.cs");
        Assert.Contains(files, file => file.Path == "src/MediaEngine.Storage/PersonRepository.cs");
    }

    // ── Signatures, stages and events ─────────────────────────────────────────────────────────────

    [Fact]
    public void EveryPeopleReadService_TakesAViewerCatalogue_SoItCannotBeCalledWithoutAViewer()
    {
        var offenders = new List<string>();
        foreach (var type in new[] { typeof(IPersonPresenceReadService), typeof(IPersonAssetScopeReadService) })
        {
            foreach (var method in type.GetMethods())
            {
                if (method.GetParameters().All(parameter => parameter.ParameterType != typeof(ViewerCatalogue)))
                {
                    offenders.Add($"{type.Name}.{method.Name}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "These people reads can be called without a viewer: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheAccessStages_ApplyInTheOrderTheSpecNames()
    {
        Assert.Equal(
            [
                ViewerAccessStage.VaultAndSections, ViewerAccessStage.SharedLibrary, ViewerAccessStage.ContentLimit,
                ViewerAccessStage.DocumentsOff, ViewerAccessStage.PersonalSpaces,
            ],
            ViewerAccessStages.InOrder.Select(stage => stage.Stage).ToArray());
        Assert.All(ViewerAccessStages.InOrder, stage =>
        {
            Assert.False(string.IsNullOrWhiteSpace(stage.Decides));
            Assert.False(string.IsNullOrWhiteSpace(stage.EnforcedBy));
        });
    }

    [Fact]
    public void ALiveEventThatNamesAPerson_IsJudgedByWhereThePersonIsCredited_NotTreatedAsVaultFree()
    {
        Assert.Contains(SignalREvents.PersonEnriched, IntercomAudienceAuthorizer.ClassifiedEvents);
        Assert.DoesNotContain(SignalREvents.PersonEnriched, IntercomAudienceAuthorizer.VaultFreeEvents.Keys);
    }

    [Fact]
    public void TheViewerAccessService_IsNeverAnOptionalParameter()
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var offenders = typeof(IViewerAccessService).Assembly.GetTypes()
            .SelectMany(type => type.GetConstructors(all).Cast<MethodBase>().Concat(type.GetMethods(all)).Select(method => (type, method)))
            .Where(entry => entry.method.GetParameters().Any(parameter =>
                parameter.IsOptional && (parameter.ParameterType == typeof(IViewerAccessService) || parameter.ParameterType == typeof(ViewerCatalogue))
                // The detail page pipeline takes the catalogue as an optional argument only because its many callers pass nothing for
                // other entity types; a person page given none treats the viewer as seeing nothing (it fails closed).
                && !(parameter.Name == "viewerCatalogue" && entry.type.Name is "DetailComposerService" or "DetailCompositionOrchestrator")))
            .Select(entry => $"{entry.type.Name}.{entry.method.Name}")
            .ToList();

        Assert.True(offenders.Count == 0, "A viewer's catalogue can be skipped by leaving it out: " + string.Join(", ", offenders));
    }

    // ── Scanners ──────────────────────────────────────────────────────────────────────────────────

    private sealed record RouteFact(string Pattern, bool Administrative, bool ResourceChecked);

    private sealed record InventoryRule(string Pattern, string Mechanism, IReadOnlyList<string> Files, string Why)
    {
        public bool Matches(string route) =>
            Pattern.EndsWith('*') ? route.StartsWith(Pattern[..^1], StringComparison.Ordinal) : route == Pattern;
    }

    private static List<string> FindUnlistedRoutes(IReadOnlyList<RouteFact> routes, IReadOnlyList<InventoryRule> rules) =>
        routes
            .Where(route => !route.Administrative && !route.ResourceChecked && !rules.Any(rule => rule.Matches(route.Pattern)))
            .Select(route => route.Pattern)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    private static List<string> FindStaleRules(IReadOnlyList<RouteFact> routes, IReadOnlyList<InventoryRule> rules, Func<string, string?> readFile)
    {
        var problems = new List<string>();
        var viewerRoutes = routes.Where(route => !route.Administrative && !route.ResourceChecked).ToList();
        foreach (var rule in rules)
        {
            if (!viewerRoutes.Any(route => rule.Matches(route.Pattern)))
            {
                problems.Add($"{rule.Pattern} matches no viewer route any more");
            }

            if (!Mechanisms.TryGetValue(rule.Mechanism, out var evidence))
            {
                problems.Add($"{rule.Pattern} names the unknown mechanism {rule.Mechanism}");
                continue;
            }

            if (string.IsNullOrWhiteSpace(rule.Why))
            {
                problems.Add($"{rule.Pattern} gives no reason");
            }

            var texts = rule.Files.Select(readFile).ToList();
            if (texts.Any(text => text is null))
            {
                problems.Add($"{rule.Pattern} names a file that does not exist: {string.Join(" + ", rule.Files)}");
            }
            else if (evidence.Length > 0 && !texts.Any(text => evidence.Any(word => text!.Contains(word, StringComparison.Ordinal))))
            {
                problems.Add($"{rule.Pattern} is listed as {rule.Mechanism} but {string.Join(" + ", rule.Files)} no longer mentions {string.Join(" or ", evidence)}");
            }
        }

        return problems;
    }

    private static List<string> FindSourceOffences(IEnumerable<(string Path, string Text)> files)
    {
        var offences = new List<string>();
        foreach (var (path, text) in files)
        {
            var readsCredits = CreditRecords.Concat(PeopleReads).Any(token => text.Contains(token, StringComparison.Ordinal));
            if (readsCredits)
            {
                if (!ReviewedReaders.TryGetValue(path, out var reviewed))
                {
                    offences.Add($"{path} reads people credits or counts but is not a reviewed reader; take the viewer's catalogue from IViewerAccessService or carry the Vault predicate");
                }
                else if (reviewed.Mechanism == "viewer-catalogue" && !text.Contains("ViewerCatalogue", StringComparison.Ordinal) && !text.Contains("IViewerAccessService", StringComparison.Ordinal))
                {
                    offences.Add($"{path} is reviewed as taking the viewer's catalogue but no longer mentions it");
                }
                else if (reviewed.Mechanism == "vault-predicate" && !text.Contains("VaultSql.", StringComparison.Ordinal))
                {
                    offences.Add($"{path} is reviewed as carrying the Vault predicate but no longer mentions VaultSql");
                }
                else if (reviewed.Mechanism == "viewer-scoped"
                         && !text.Contains("IDisplayProjectionReadService", StringComparison.Ordinal) && !text.Contains("IViewerAccessService", StringComparison.Ordinal))
                {
                    offences.Add($"{path} is reviewed as scoped by a viewer-aware list but no longer mentions one");
                }
            }

            if (text.Contains("new ViewerCatalogue(", StringComparison.Ordinal) && !MayBuildACatalogue.Contains(path))
            {
                offences.Add($"{path} builds a ViewerCatalogue; only the access service may (it is the only place the rules are applied in order)");
            }
        }

        return offences;
    }

    private static List<RouteFact> ViewerReadRoutes(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => (endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Contains(HttpMethods.Get, StringComparer.OrdinalIgnoreCase))
            .Select(endpoint => new RouteFact(
                (endpoint.RoutePattern.RawText ?? string.Empty).TrimEnd('/'),
                IsAdministrative(endpoint),
                IsResourceChecked(endpoint)))
            .ToList();

    // A route only an administrator, an administrator-capable application or a service application can call is not a viewer read: its
    // Vault handling is reviewed with the administration surfaces (Ingestion, Operations, Settings).
    private static bool IsAdministrative(RouteEndpoint endpoint)
    {
        var requirements = endpoint.Metadata.OfType<AuthorizationPolicy>().SelectMany(policy => policy.Requirements);
        if (requirements.Any(requirement => requirement is EffectiveAdministratorRequirement or AdministratorOrApplicationRequirement or ApplicationPermissionRequirement))
        {
            return true;
        }

        var named = endpoint.Metadata.OfType<IAuthorizeData>().Select(data => data.Policy).ToArray();
        return named.Contains(AuthPolicies.Administrator, StringComparer.Ordinal)
            || named.Contains(AuthPolicies.AdministratorEligibility, StringComparer.Ordinal);
    }

    private static bool IsResourceChecked(RouteEndpoint endpoint) =>
        endpoint.Metadata.Any(item => item is CatalogueAssetAccessMetadata or CatalogueArtworkAccessMetadata or CatalogueEntityAccessMetadata
            or CatalogueAnyEntityAccessMetadata or CatalogueQidAccessMetadata or CatalogueCharacterPortraitAccessMetadata);

    private static List<InventoryRule> ReadInventory()
    {
        var path = Path.Combine(FindRepoRoot(), InventoryFile.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), "Missing the viewer read route inventory.");
        var rules = new List<InventoryRule>();
        foreach (var line in File.ReadAllLines(path).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')))
        {
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            Assert.True(parts.Length == 4, $"Inventory line needs four columns (route | mechanism | file | why): {line}");
            rules.Add(new InventoryRule(parts[0], parts[1], parts[2].Split('+', StringSplitOptions.TrimEntries), parts[3]));
        }

        return rules;
    }

    private static List<(string Path, string Text)> ReadSourceFiles()
    {
        var root = FindRepoRoot();
        var files = new List<(string, string)>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)
                || relative.Contains("/node_modules/", StringComparison.Ordinal)
                || relative.StartsWith("src/MediaEngine.Web/", StringComparison.Ordinal)
                || !new[] { ".cs", ".sql" }.Contains(System.IO.Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add((relative, File.ReadAllText(file)));
        }

        return files;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
