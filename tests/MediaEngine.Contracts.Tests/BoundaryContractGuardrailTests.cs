using System.Text;
using System.Text.RegularExpressions;

namespace MediaEngine.Contracts.Tests;

/// <summary>
/// Stage 6 boundary ratchet. The fixture records only debt that existed when the consolidation
/// audit closed; deleting an entry is encouraged, while adding one requires explicit review.
/// Source locations are diagnostic only so harmless line movement does not churn the fixture.
/// </summary>
public sealed partial class BoundaryContractGuardrailTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly Lazy<BoundaryAudit> Audit = new(BuildAudit);

    private static readonly IReadOnlyDictionary<string, BoundaryClassification> ReviewedBoundaryClassifications =
        new Dictionary<string, BoundaryClassification>(StringComparer.Ordinal)
        {
            // Frozen Universe/Chronicle wire. These packet types are deliberately preserved byte-for-byte.
            ["Endpoint|frozen-universe-chronicle|src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs|Produces|List<UniverseCandidateDto>|1|MediaEngine.Api.Models.UniverseCandidateDto"] = BoundaryClassification.FrozenUniverseChronicle,
            ["Endpoint|frozen-universe-chronicle|src/MediaEngine.Api/Endpoints/LibraryEndpoints.cs|Produces|UniverseBatchAcceptResult|1|MediaEngine.Api.Models.UniverseBatchAcceptResult"] = BoundaryClassification.FrozenUniverseChronicle,
            ["Endpoint|frozen-universe-chronicle|src/MediaEngine.Api/Endpoints/SearchEndpoints.cs|Produces|SearchUniverseResult|1|MediaEngine.Domain.Models.SearchUniverseResult"] = BoundaryClassification.FrozenUniverseChronicle,
            ["Endpoint|frozen-universe-chronicle|src/MediaEngine.Api/Endpoints/UniverseGraphEndpoints.cs|Produces|IReadOnlyList<LoreDeltaResult>|1|MediaEngine.Domain.Models.LoreDeltaResult"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.LibraryOperations.cs|GetFromJsonAsync|List<UniverseCandidateViewModel>|1|MediaEngine.Web.Models.ViewDTOs.UniverseCandidateViewModel"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|GetFromJsonAsync|List<LoreDeltaResultDto>|1|MediaEngine.Web.Models.ViewDTOs.LoreDeltaResultDto"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|GetFromJsonAsync|List<NarrativeRootDto>|1|MediaEngine.Web.Models.ViewDTOs.NarrativeRootDto"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|GetFromJsonAsync|List<UniverseCharacterRaw>|1|MediaEngine.Web.Services.Integration.UniverseCharacterRaw"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|GetFromJsonAsync|List<UniverseLoreSourceViewModel>|1|MediaEngine.Web.Models.ViewDTOs.UniverseLoreSourceViewModel"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|GetFromJsonAsync|UniverseAdaptationsResponse|1|MediaEngine.Web.Models.ViewDTOs.UniverseAdaptationsResponse"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|GetFromJsonAsync|UniverseCastResponse|1|MediaEngine.Web.Models.ViewDTOs.UniverseCastResponse"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|GetFromJsonAsync|UniverseGraphResponse|1|MediaEngine.Web.Models.ViewDTOs.UniverseGraphResponse"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|GetFromJsonAsync|UniverseHealthRaw|1|MediaEngine.Web.Services.Integration.UniverseHealthRaw"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|GetFromJsonAsync|UniversePathsResponse|1|MediaEngine.Web.Models.ViewDTOs.UniversePathsResponse"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|ReadFromJsonAsync|List<UniverseLoreSourceViewModel>|1|MediaEngine.Web.Models.ViewDTOs.UniverseLoreSourceViewModel"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|ReadFromJsonAsync|List<UniverseLoreSourceViewModel>|2|MediaEngine.Web.Models.ViewDTOs.UniverseLoreSourceViewModel"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|ReadFromJsonAsync|SearchUniverseResponseDto|1|MediaEngine.Web.Models.ViewDTOs.SearchUniverseResponseDto"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|ReadFromJsonAsync|UniverseLoreEnrichmentSummaryViewModel|1|MediaEngine.Web.Models.ViewDTOs.UniverseLoreEnrichmentSummaryViewModel"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|ReadFromJsonAsync|UniverseLoreSourceViewModel|1|MediaEngine.Web.Models.ViewDTOs.UniverseLoreSourceViewModel"] = BoundaryClassification.FrozenUniverseChronicle,
            ["SignalR|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/UIOrchestratorService.cs|On|LoreDeltaDiscoveredEvent|1|MediaEngine.Web.Services.Integration.LoreDeltaDiscoveredEvent"] = BoundaryClassification.FrozenUniverseChronicle,
            ["SignalR|frozen-universe-chronicle|src/MediaEngine.Web/Services/Integration/UIOrchestratorService.cs|On|UniverseEnrichmentProgressEvent|1|MediaEngine.Web.Services.Integration.UniverseEnrichmentProgressEvent"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|misc-wire|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|GetFromJsonAsync|FamilyTreeResponse|1|MediaEngine.Web.Models.ViewDTOs.FamilyTreeResponse"] = BoundaryClassification.FrozenUniverseChronicle,
            ["WebJson|misc-wire|src/MediaEngine.Web/Services/Integration/EngineApiClient.cs|ReadFromJsonAsync|DeepEnrichResponse|1|MediaEngine.Web.Models.ViewDTOs.DeepEnrichResponse"] = BoundaryClassification.FrozenUniverseChronicle,

            // Local serialization that never crosses the Engine/Dashboard HTTP or SignalR boundary.
            ["WebJson|ingestion-operations|src/MediaEngine.Web/Models/ViewDTOs/ActivityEntryPresentation.cs|Deserialize|ActivityRichData|1|MediaEngine.Web.Models.ViewDTOs.ActivityRichData"] = BoundaryClassification.PresentationOnly,
            ["WebJson|ingestion-operations|src/MediaEngine.Web/Models/ViewDTOs/ActivityEntryPresentation.cs|Deserialize|ReviewRichData|1|MediaEngine.Web.Models.ViewDTOs.ReviewRichData"] = BoundaryClassification.PresentationOnly,
            ["WebJson|misc-wire|src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor|Deserialize|PopupCommand|1|PopupCommand"] = BoundaryClassification.PresentationOnly,
            ["WebJson|playback-reading|src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor|Deserialize|ListenPlaybackSnapshot|1|MediaEngine.Web.Services.Playback.ListenPlaybackSnapshot"] = BoundaryClassification.PresentationOnly,
            ["WebJson|playback-reading|src/MediaEngine.Web/Components/Pages/EpubReader.razor|Deserialize|ReaderSettingsDto|1|MediaEngine.Web.Models.ViewDTOs.ReaderSettingsDto"] = BoundaryClassification.PresentationOnly,

            // Explicit contract-to-presentation projections. The serialized bytes never leave this process.
            ["WebJson|ai-plugins-settings|src/MediaEngine.Web/Models/ViewDTOs/ResolvedUISettingsViewModel.cs|Deserialize|ResolvedUISettingsViewModel|1|MediaEngine.Web.Models.ViewDTOs.ResolvedUISettingsViewModel"] = BoundaryClassification.InternalProjection,
            ["WebJson|collections-display|src/MediaEngine.Web/Models/ViewDTOs/CollectionGroupViewModels.cs|Deserialize|CollectionGroupDetailViewModel|1|MediaEngine.Web.Models.ViewDTOs.CollectionGroupDetailViewModel"] = BoundaryClassification.InternalProjection,
            ["WebJson|collections-display|src/MediaEngine.Web/Models/ViewDTOs/CollectionGroupViewModels.cs|Deserialize|ContentGroupViewModel|1|MediaEngine.Web.Models.ViewDTOs.ContentGroupViewModel"] = BoundaryClassification.InternalProjection,
            ["WebJson|collections-display|src/MediaEngine.Web/Models/ViewDTOs/ManagedCollectionViewModel.cs|Deserialize|CollectionManagementCatalogViewModel|1|MediaEngine.Web.Models.ViewDTOs.CollectionManagementCatalogViewModel"] = BoundaryClassification.InternalProjection,
            ["WebJson|collections-display|src/MediaEngine.Web/Models/ViewDTOs/ManagedCollectionViewModel.cs|Deserialize|ManagedCollectionViewModel|1|MediaEngine.Web.Models.ViewDTOs.ManagedCollectionViewModel"] = BoundaryClassification.InternalProjection,
        };

    private static readonly HashSet<string> FrozenUniverseChronicleSignalRPayloads =
    [
        "LoreDeltaDiscoveredEvent",
        "UniverseEnrichmentProgressEvent",
    ];

    [Fact]
    public void RazorSourceScrubbingMasksMarkupButScansCodeBlocks()
    {
        const string source = """
            <button title="JsonSerializer.Deserialize<MarkupOnlyType>(json)">play</button>
            @using System.Text.Json
            @code {
                private object? Read(string json) => JsonSerializer.Deserialize<CodeBlockType>(json);
            }
            """;

        var scrubbed = ScrubCommentsAndLiterals(MaskRazorMarkup(source));

        Assert.Contains("JsonSerializer.Deserialize<CodeBlockType>", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain("MarkupOnlyType", scrubbed, StringComparison.Ordinal);
        Assert.Contains("System.Text.Json", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void ListenBarRazorScannerRetainsPlaybackDeserializersInsideCodeBlock()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot,
            "src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor"));
        var scrubbed = ScrubCommentsAndLiterals(MaskRazorMarkup(source));

        Assert.Contains("JsonSerializer.Deserialize<ListenPlaybackSnapshot>", scrubbed, StringComparison.Ordinal);
        Assert.Contains("JsonSerializer.Deserialize<PopupCommand>", scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointAcceptsAndProduces_ProductTypesAreContractsOrExactCategorizedDebt()
    {
        AssertCategoryMatchesFixture(BoundaryKind.Endpoint);
    }

    [Fact]
    public void WebJsonGenericTargets_AreContractsOrExactCategorizedDebt()
    {
        AssertCategoryMatchesFixture(BoundaryKind.WebJson);
    }

    [Fact]
    public void SignalRPayloads_AreContractsWithOnlyTheFrozenUniverseChronicleException()
    {
        AssertCategoryMatchesFixture(BoundaryKind.SignalR);

        var frozenActual = Audit.Value.Classified
            .Where(item => item.Classification == BoundaryClassification.FrozenUniverseChronicle
                && item.Entry.Kind == BoundaryKind.SignalR)
            .Select(item => item.Entry.TypeExpression)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(
            frozenActual.SetEquals(FrozenUniverseChronicleSignalRPayloads),
            "The Universe/Chronicle SignalR exception is frozen. Expected: "
            + string.Join(", ", FrozenUniverseChronicleSignalRPayloads.Order())
            + ". Actual: "
            + string.Join(", ", frozenActual.Order())
            + ". Move non-frozen payloads to MediaEngine.Contracts; do not expand this exception.");
    }

    [Fact]
    public void PresentationAndInternalTypes_AreNeverSilentlyExcludedFromBoundaryDebt()
    {
        var missing = Audit.Value.All
            .Where(entry => entry.Offenders.Any(IsPresentationOrInternal))
            .Where(entry => !ReviewedBoundaryClassifications.ContainsKey(entry.Key))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Presentation, ViewModel, private/internal, and *Raw types are product-owned boundary "
            + "types, not implementation details that the guardrail may skip. Missing reviewed classifications:"
            + Environment.NewLine
            + FormatEntries(missing));
    }

    [Fact]
    public void TemporaryWireDebt_IsExactCategorizedAndContainsNoContractTypes()
    {
        var fixture = LoadDebtFixture();
        var actual = Audit.Value.Debt.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

        var malformed = fixture
            .Where(line => !TryParseDebtLine(line, out _))
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(
            malformed.Count == 0,
            "Malformed TemporaryWireDebt entries: " + string.Join(", ", malformed));

        var wronglyCategorized = Audit.Value.Debt
            .Where(entry => !string.Equals(
                entry.Packet,
                Categorize(entry.RelativePath, entry.TypeExpression, entry.Offenders),
                StringComparison.Ordinal))
            .ToList();
        Assert.True(
            wronglyCategorized.Count == 0,
            "Boundary debt must remain grouped by its owning Wave 2 packet:"
            + Environment.NewLine
            + FormatEntries(wronglyCategorized));

        var contractDebt = Audit.Value.Debt
            .Where(entry => entry.Offenders.Any(definition => definition.IsContract))
            .ToList();
        Assert.True(
            contractDebt.Count == 0,
            "MediaEngine.Contracts types must never appear in TemporaryWireDebt:"
            + Environment.NewLine
            + FormatEntries(contractDebt));

        var actualClassifications = Audit.Value.Classified
            .Select(item => item.Entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        var expectedClassifications = ReviewedBoundaryClassifications.Keys.ToHashSet(StringComparer.Ordinal);
        var missingClassifications = expectedClassifications.Except(actualClassifications).Order(StringComparer.Ordinal).ToList();
        var unreviewedClassifications = actualClassifications.Except(expectedClassifications).Order(StringComparer.Ordinal).ToList();
        Assert.True(
            missingClassifications.Count == 0 && unreviewedClassifications.Count == 0,
            "Reviewed boundary classifications must be exact."
            + Environment.NewLine
            + string.Join(Environment.NewLine, unreviewedClassifications.Select(key => $"  + {key}"))
            + Environment.NewLine
            + string.Join(Environment.NewLine, missingClassifications.Select(key => $"  - {key}")));

        AssertExact(
            "all product-owned wire boundaries",
            actual,
            fixture,
            Audit.Value.Debt);
    }

    private static void AssertCategoryMatchesFixture(BoundaryKind kind)
    {
        var fixture = LoadDebtFixture()
            .Where(line => TryParseDebtLine(line, out var parsed) && parsed.Kind == kind)
            .ToHashSet(StringComparer.Ordinal);
        var entries = Audit.Value.Debt.Where(entry => entry.Kind == kind).ToList();
        var actual = entries.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

        AssertExact(kind.ToString(), actual, fixture, entries);
    }

    private static void AssertExact(
        string label,
        HashSet<string> actual,
        HashSet<string> expected,
        IReadOnlyCollection<DebtEntry> entries)
    {
        var additions = actual.Except(expected).Order(StringComparer.Ordinal).ToList();
        var stale = expected.Except(actual).Order(StringComparer.Ordinal).ToList();
        if (additions.Count == 0 && stale.Count == 0)
        {
            return;
        }

        var actualInventoryPath = WriteActualInventory();
        var entryByKey = entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var message = new StringBuilder();
        message.AppendLine($"TemporaryWireDebt mismatch for {label}.");
        if (additions.Count > 0)
        {
            message.AppendLine("New boundary debt is forbidden. Move these types to MediaEngine.Contracts:");
            foreach (var key in additions)
            {
                message.Append("  + ").Append(key);
                if (entryByKey.TryGetValue(key, out var entry))
                {
                    message.Append("  @ ").Append(entry.Location);
                }

                message.AppendLine();
            }
        }

        if (stale.Count > 0)
        {
            message.AppendLine("Stale debt entries must be deleted after the production boundary is fixed:");
            foreach (var key in stale)
            {
                message.Append("  - ").AppendLine(key);
            }
        }

        message.AppendLine("The approved fixture is tests/MediaEngine.Contracts.Tests/Fixtures/TemporaryWireDebt.txt.");
        message.Append("A complete categorized inventory was written to ").AppendLine(actualInventoryPath);
        Assert.Fail(message.ToString());
    }

    private static string WriteActualInventory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TemporaryWireDebt.actual.txt");
        var lines = new List<string>
        {
            "# Generated Stage 6 boundary inventory. Copy reviewed entries into TemporaryWireDebt.txt.",
        };

        foreach (var packet in Audit.Value.Debt.GroupBy(entry => entry.Packet).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            lines.Add(string.Empty);
            lines.Add($"# [{packet.Key}]");
            lines.AddRange(packet.Select(entry => entry.Key));
        }

        File.WriteAllLines(path, lines);
        return path;
    }

    private static BoundaryAudit BuildAudit()
    {
        var definitions = BuildTypeIndex();
        var entries = new List<DebtEntry>();
        entries.AddRange(ScanEndpointBoundaries(definitions));
        entries.AddRange(ScanWebBoundaries(definitions));

        var all = entries
            .Where(entry => entry.Offenders.Count > 0)
            .OrderBy(entry => entry.Kind)
            .ThenBy(entry => entry.Packet, StringComparer.Ordinal)
            .ThenBy(entry => entry.RelativePath, StringComparer.Ordinal)
            .ThenBy(entry => entry.Member, StringComparer.Ordinal)
            .ThenBy(entry => entry.TypeExpression, StringComparer.Ordinal)
            .ToList();

        // Identical calls in one source file are significant. Number them deterministically so
        // an added duplicate cannot hide behind set semantics.
        foreach (var group in all.GroupBy(
                     entry => entry.UnnumberedKey,
                     StringComparer.Ordinal))
        {
            var ordinal = 0;
            foreach (var entry in group.OrderBy(item => item.SourceIndex))
            {
                entry.Occurrence = ++ordinal;
            }
        }

        var classified = all
            .Where(entry => ReviewedBoundaryClassifications.TryGetValue(entry.Key, out _))
            .Select(entry => new ClassifiedBoundary(
                entry,
                ReviewedBoundaryClassifications[entry.Key]))
            .ToList();
        var debt = all
            .Where(entry => !ReviewedBoundaryClassifications.ContainsKey(entry.Key))
            .ToList();

        return new BoundaryAudit(all, debt, classified);
    }

    private static IEnumerable<DebtEntry> ScanEndpointBoundaries(TypeIndex definitions)
    {
        var root = Path.Combine(RepoRoot, "src", "MediaEngine.Api", "Endpoints");
        foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(path);
            if (!ContainsAnyGenericMethod(source, EndpointMethods()))
            {
                continue;
            }

            var context = CreateSourceContext(path, source, definitions);
            foreach (var call in FindGenericCalls(source, context.ScrubbedSource, EndpointMethods()))
            {
                yield return CreateEntry(
                    BoundaryKind.Endpoint,
                    context,
                    call,
                    definitions);
            }
        }
    }

    private static IEnumerable<DebtEntry> ScanWebBoundaries(TypeIndex definitions)
    {
        var root = Path.Combine(RepoRoot, "src", "MediaEngine.Web");
        var methods = JsonMethods().Concat(SignalRMethods()).ToHashSet(StringComparer.Ordinal);
        foreach (var path in EnumerateSourceFiles(root))
        {
            var source = File.ReadAllText(path);
            if (!ContainsAnyGenericMethod(source, methods))
            {
                continue;
            }

            var context = CreateSourceContext(path, source, definitions);
            foreach (var call in FindGenericCalls(source, context.ScrubbedSource, methods))
            {
                yield return CreateEntry(
                    call.Method == "On" ? BoundaryKind.SignalR : BoundaryKind.WebJson,
                    context,
                    call,
                    definitions);
            }
        }
    }

    private static DebtEntry CreateEntry(
        BoundaryKind kind,
        SourceContext context,
        GenericCall call,
        TypeIndex definitions)
    {
        var offenders = ResolveProductTypes(
            call.TypeExpression,
            context,
            definitions)
            .Where(definition => !definition.IsContract)
            .Distinct()
            .OrderBy(definition => definition.QualifiedName, StringComparer.Ordinal)
            .ThenBy(definition => definition.RelativePath, StringComparer.Ordinal)
            .ToList();
        var packet = Categorize(context.RelativePath, call.TypeExpression, offenders);
        return new DebtEntry(
            kind,
            packet,
            context.RelativePath,
            call.Method,
            NormalizeTypeExpression(call.TypeExpression),
            offenders,
            call.SourceIndex,
            $"{context.RelativePath}:{GetLineNumber(context.Source, call.SourceIndex)}");
    }

    private static IReadOnlyList<TypeDefinition> ResolveProductTypes(
        string expression,
        SourceContext context,
        TypeIndex index)
    {
        var result = new HashSet<TypeDefinition>();

        foreach (Match tokenMatch in TypeTokenRegex().Matches(expression))
        {
            var token = tokenMatch.Groups["token"].Value;
            var simpleName = token.Split('.').Last();
            if (IgnoredTypeTokens.Contains(simpleName) || IsGenericParameter(simpleName))
            {
                continue;
            }

            IReadOnlyList<TypeDefinition> candidates;
            if (context.Aliases.TryGetValue(simpleName, out var aliasTarget))
            {
                candidates = index.FindQualified(aliasTarget);
            }
            else if (token.Contains('.', StringComparison.Ordinal))
            {
                candidates = index.FindQualified(token);
            }
            else
            {
                candidates = index.Find(simpleName);
            }

            if (candidates.Count == 0)
            {
                if (LooksProductOwned(token, simpleName))
                {
                    var namespaceName = token.Contains('.', StringComparison.Ordinal)
                        ? token[..token.LastIndexOf('.')]
                        : "UnresolvedProductBoundary";
                    result.Add(new TypeDefinition(
                        namespaceName,
                        simpleName,
                        "unknown",
                        "<unresolved-product-type>"));
                }
                continue;
            }

            var sameFile = candidates
                .Where(candidate => string.Equals(
                    candidate.RelativePath,
                    context.RelativePath,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (sameFile.Count > 0)
            {
                result.UnionWith(sameFile);
                continue;
            }

            var sameNamespace = candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(context.Namespace)
                    && string.Equals(candidate.Namespace, context.Namespace, StringComparison.Ordinal))
                .ToList();
            if (sameNamespace.Count > 0)
            {
                result.UnionWith(sameNamespace);
                continue;
            }

            var imported = candidates
                .Where(candidate => context.ImportedNamespaces.Contains(candidate.Namespace))
                .ToList();
            if (imported.Count == 1)
            {
                result.Add(imported[0]);
                continue;
            }
            if (imported.Count > 1)
            {
                result.UnionWith(imported);
                continue;
            }

            if (candidates.Count == 1)
            {
                result.Add(candidates[0]);
            }
            else
            {
                result.UnionWith(candidates);
            }
        }

        return result.ToList();
    }

    private static SourceContext CreateSourceContext(
        string path,
        string source,
        TypeIndex index)
    {
        var isRazor = path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
        var analysisSource = isRazor ? MaskRazorMarkup(source) : source;
        var scrubbedSource = ScrubCommentsAndLiterals(analysisSource);
        var importedNamespaces = UsingRegex().Matches(scrubbedSource)
            .Select(match => match.Groups["namespace"].Value)
            .Concat(isRazor
                ? RazorUsingRegex().Matches(source).Select(match => match.Groups["namespace"].Value)
                : Enumerable.Empty<string>())
            .Concat(index.GlobalUsingsFor(path))
            .ToHashSet(StringComparer.Ordinal);
        var aliases = AliasUsingRegex().Matches(scrubbedSource)
            .ToDictionary(
                match => match.Groups["alias"].Value,
                match => match.Groups["type"].Value,
                StringComparer.Ordinal);

        return new SourceContext(
            path,
            ToRelativePath(path),
            source,
            scrubbedSource,
            NamespaceRegex().Match(scrubbedSource).Groups["namespace"].Value is { Length: > 0 } namespaceName
                ? namespaceName
                : isRazor ? RazorNamespaceRegex().Match(source).Groups["namespace"].Value : string.Empty,
            importedNamespaces,
            aliases);
    }

    private static string MaskRazorMarkup(string source)
    {
        var masked = source.Select(character => character is '\r' or '\n' ? character : ' ').ToArray();
        foreach (Match directive in RazorDirectiveRegex().Matches(source))
        {
            CopyRange(source, masked, directive.Index, directive.Length);
        }

        foreach (Match block in RazorCodeBlockRegex().Matches(source))
        {
            var openingBrace = source.IndexOf('{', block.Index, block.Length);
            if (openingBrace < 0) continue;

            // Razor code sections conventionally close the file. Preserving that complete tail
            // avoids miscounting braces in interpolated C# strings, where quotes may appear inside
            // interpolation expressions (for example ToString("MMM d")).
            var lastContentIndex = source.Length - 1;
            while (lastContentIndex >= block.Index && char.IsWhiteSpace(source[lastContentIndex])) lastContentIndex--;
            if (lastContentIndex >= openingBrace && source[lastContentIndex] == '}')
            {
                CopyRange(source, masked, block.Index, lastContentIndex - block.Index + 1);
                continue;
            }

            var blockText = source[block.Index..];
            var scrubbedBlock = ScrubCommentsAndLiterals(blockText);
            var relativeOpening = openingBrace - block.Index;
            var depth = 0;
            var closingBrace = -1;
            for (var index = relativeOpening; index < scrubbedBlock.Length; index++)
            {
                if (scrubbedBlock[index] == '{') depth++;
                else if (scrubbedBlock[index] == '}' && --depth == 0)
                {
                    closingBrace = block.Index + index;
                    break;
                }
            }

            if (closingBrace >= openingBrace)
            {
                CopyRange(source, masked, block.Index, closingBrace - block.Index + 1);
            }
        }

        return new string(masked);
    }

    private static void CopyRange(string source, char[] destination, int start, int length) =>
        source.AsSpan(start, length).CopyTo(destination.AsSpan(start, length));

    private static TypeIndex BuildTypeIndex()
    {
        var definitions = new List<TypeDefinition>();
        var srcRoot = Path.Combine(RepoRoot, "src");
        foreach (var path in EnumerateSourceFiles(srcRoot))
        {
            var source = File.ReadAllText(path);
            if (!ContainsAny(source, "class", "record", "struct", "enum", "interface"))
            {
                continue;
            }

            var namespaceName = NamespaceRegex().Match(source).Groups["namespace"].Value;
            foreach (Match match in TypeDeclarationRegex().Matches(source))
            {
                var name = match.Groups["name"].Value;
                var access = match.Groups["access"].Success
                    ? match.Groups["access"].Value
                    : "private";
                definitions.Add(new TypeDefinition(
                    namespaceName,
                    name,
                    access,
                    ToRelativePath(path)));
            }
        }

        return new TypeIndex(definitions);
    }

    private static bool ContainsAnyGenericMethod(
        string source,
        IEnumerable<string> methods) =>
        methods.Any(method => source.Contains(method, StringComparison.Ordinal));

    private static IEnumerable<GenericCall> FindGenericCalls(
        string source,
        string scrubbed,
        IReadOnlySet<string> methods)
    {
        foreach (Match match in GenericCallStartRegex().Matches(scrubbed))
        {
            var method = match.Groups["method"].Value;
            if (!methods.Contains(method))
            {
                continue;
            }

            var openingAngle = scrubbed.IndexOf('<', match.Index + match.Length - 1);
            var closingAngle = FindMatchingAngle(scrubbed, openingAngle);
            Assert.True(
                closingAngle > openingAngle,
                $"Could not parse {method}<...> at character {match.Index}.");

            var expression = source[(openingAngle + 1)..closingAngle];
            yield return new GenericCall(method, expression, match.Index);
        }
    }

    private static int FindMatchingAngle(string source, int openingIndex)
    {
        var depth = 0;
        for (var index = openingIndex; index < source.Length; index++)
        {
            if (source[index] == '<')
            {
                depth++;
            }
            else if (source[index] == '>' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static string ScrubCommentsAndLiterals(string source)
    {
        var chars = source.ToCharArray();
        var state = LexicalState.Code;
        var rawQuoteCount = 0;

        for (var index = 0; index < chars.Length; index++)
        {
            var current = chars[index];
            var next = index + 1 < chars.Length ? chars[index + 1] : '\0';

            switch (state)
            {
                case LexicalState.Code:
                    if (current == '/' && next == '/')
                    {
                        Blank(chars, index++);
                        Blank(chars, index);
                        state = LexicalState.LineComment;
                    }
                    else if (current == '/' && next == '*')
                    {
                        Blank(chars, index++);
                        Blank(chars, index);
                        state = LexicalState.BlockComment;
                    }
                    else if (current == '"')
                    {
                        rawQuoteCount = CountRun(chars, index, '"');
                        if (rawQuoteCount >= 3)
                        {
                            for (var offset = 0; offset < rawQuoteCount; offset++)
                            {
                                Blank(chars, index + offset);
                            }

                            index += rawQuoteCount - 1;
                            state = LexicalState.RawString;
                        }
                        else
                        {
                            Blank(chars, index);
                            state = (index > 0 && chars[index - 1] == '@')
                                || (index > 1 && chars[index - 2] == '@')
                                ? LexicalState.VerbatimString
                                : LexicalState.String;
                        }
                    }
                    else if (current == '\'')
                    {
                        Blank(chars, index);
                        state = LexicalState.Character;
                    }
                    break;

                case LexicalState.LineComment:
                    if (current is '\r' or '\n')
                    {
                        state = LexicalState.Code;
                    }
                    else
                    {
                        Blank(chars, index);
                    }

                    break;

                case LexicalState.BlockComment:
                    if (current == '*' && next == '/')
                    {
                        Blank(chars, index++);
                        Blank(chars, index);
                        state = LexicalState.Code;
                    }
                    else
                    {
                        Blank(chars, index);
                    }
                    break;

                case LexicalState.String:
                case LexicalState.Character:
                    var terminator = state == LexicalState.String ? '"' : '\'';
                    if (current == '\\')
                    {
                        Blank(chars, index);
                        if (index + 1 < chars.Length)
                        {
                            Blank(chars, ++index);
                        }
                    }
                    else if (current == terminator)
                    {
                        Blank(chars, index);
                        state = LexicalState.Code;
                    }
                    else if (current is not ('\r' or '\n'))
                    {
                        Blank(chars, index);
                    }
                    break;

                case LexicalState.VerbatimString:
                    if (current == '"' && next == '"')
                    {
                        Blank(chars, index++);
                        Blank(chars, index);
                    }
                    else if (current == '"')
                    {
                        Blank(chars, index);
                        state = LexicalState.Code;
                    }
                    else if (current is not ('\r' or '\n'))
                    {
                        Blank(chars, index);
                    }
                    break;

                case LexicalState.RawString:
                    if (current == '"' && CountRun(chars, index, '"') >= rawQuoteCount)
                    {
                        for (var offset = 0; offset < rawQuoteCount; offset++)
                        {
                            Blank(chars, index + offset);
                        }

                        index += rawQuoteCount - 1;
                        state = LexicalState.Code;
                    }
                    else if (current is not ('\r' or '\n'))
                    {
                        Blank(chars, index);
                    }
                    break;

            }
        }

        return new string(chars);
    }

    private static void Blank(char[] chars, int index)
    {
        if (chars[index] is not ('\r' or '\n'))
        {
            chars[index] = ' ';
        }
    }

    private static int CountRun(char[] chars, int index, char value)
    {
        var count = 0;
        while (index + count < chars.Length && chars[index + count] == value)
        {
            count++;
        }

        return count;
    }


    private static bool IsPresentationOrInternal(TypeDefinition definition) =>
        !string.Equals(definition.Access, "public", StringComparison.Ordinal)
        || definition.Name.EndsWith("Raw", StringComparison.Ordinal)
        || definition.Name.Contains("ViewModel", StringComparison.Ordinal)
        || definition.Name.Contains("Presentation", StringComparison.Ordinal)
        || definition.RelativePath.Contains(
            "/Models/ViewDTOs/",
            StringComparison.OrdinalIgnoreCase);

    private static string Categorize(
        string relativePath,
        string typeExpression,
        IReadOnlyCollection<TypeDefinition> offenders)
    {
        var text = string.Join(
            " ",
            new[]
            {
                relativePath,
                typeExpression,
                string.Join(" ", offenders.Select(item => item.QualifiedName)),
            }).ToLowerInvariant();

        if (ContainsAny(text, "universe", "chronicle", "loredelta", "narrative"))
        {
            return "frozen-universe-chronicle";
        }

        if (ContainsAny(text, "search", "canonical", "match"))
        {
            return "search-matching";
        }

        if (ContainsAny(text, "collection", "display", "shelf", "tile"))
        {
            return "collections-display";
        }

        if (ContainsAny(text, "person", "people", "profile", "favorite", "taste"))
        {
            return "people-profiles";
        }

        if (ContainsAny(text, "playback", "player", "reader", "reading", "progress", "journey", "stream", "bookmark", "highlight", "track"))
        {
            return "playback-reading";
        }

        if (ContainsAny(text, "ingestion", "operation", "activity", "batch", "folderhealth", "retagsweep", "initialsweep"))
        {
            return "ingestion-operations";
        }

        if (ContainsAny(text, "review", "curation", "libraryitem", "libraryendpoint", "mediaeditor", "metadata"))
        {
            return "curation-review";
        }

        if (ContainsAny(
                text,
                "/aien",
                "/aiend",
                ".ai.",
                "aimodel",
                "aiconfig",
                "plugin",
                "provider",
                "settings",
                "configuration"))
        {
            return "ai-plugins-settings";
        }

        return "misc-wire";
    }

    private static bool ContainsAny(string text, params string[] values) =>
        values.Any(value => text.Contains(value, StringComparison.Ordinal));

    private static string NormalizeTypeExpression(string expression) =>
        WhitespaceRegex().Replace(expression, string.Empty)
            .Replace("global::", string.Empty, StringComparison.Ordinal);

    private static HashSet<string> LoadDebtFixture()
    {
        var path = Path.Combine(
            RepoRoot,
            "tests",
            "MediaEngine.Contracts.Tests",
            "Fixtures",
            "TemporaryWireDebt.txt");
        Assert.True(File.Exists(path), $"Missing boundary debt fixture: {path}");

        return File.ReadAllLines(path)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool TryParseDebtLine(string line, out ParsedDebtLine parsed)
    {
        var parts = line.Split('|');
        if (parts.Length == 7
            && Enum.TryParse<BoundaryKind>(parts[0], out var kind)
            && int.TryParse(parts[5], out var occurrence)
            && occurrence > 0
            && parts[1].Length > 0
            && parts[2].Length > 0
            && parts[3].Length > 0
            && parts[4].Length > 0
            && parts[6].Length > 0)
        {
            parsed = new ParsedDebtLine(kind, parts[1]);
            return true;
        }

        parsed = default;
        return false;
    }

    private static string FormatEntries(IEnumerable<DebtEntry> entries) =>
        string.Join(
            Environment.NewLine,
            entries.Select(entry => $"  {entry.Key}  @ {entry.Location}"));

    private static IEnumerable<string> EnumerateSourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"));

    private static bool HasPathSegment(string path, string segment) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains(segment, StringComparer.OrdinalIgnoreCase);

    private static int GetLineNumber(string source, int index)
    {
        var line = 1;
        for (var position = 0; position < index && position < source.Length; position++)
        {
            if (source[position] == '\n')
            {
                line++;
            }
        }
        return line;
    }

    private static string ToRelativePath(string path) =>
        Path.GetRelativePath(RepoRoot, path).Replace('\\', '/');

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static bool IsGenericParameter(string name) =>
        name is "T" or "TKey" or "TValue" or "TRequest" or "TResponse" or "TReq" or "TRes";

    private static bool LooksProductOwned(string token, string simpleName) =>
        token.StartsWith("MediaEngine.", StringComparison.Ordinal)
        || token.StartsWith("Tuvima.", StringComparison.Ordinal)
        || ProductTypeSuffixes.Any(suffix => simpleName.EndsWith(suffix, StringComparison.Ordinal));

    private static IReadOnlySet<string> EndpointMethods() =>
        new HashSet<string>(["Accepts", "Produces"], StringComparer.Ordinal);

    private static IReadOnlySet<string> JsonMethods() =>
        new HashSet<string>(
            [
                "Deserialize",
                "DeserializeAsync",
                "GetFromJsonAsync",
                "PostAsJsonAsync",
                "PutAsJsonAsync",
                "PatchAsJsonAsync",
                "ReadFromJsonAsync",
                "Serialize",
                "SerializeAsync",
                "SerializeToElement",
                "WriteAsJsonAsync",
            ],
            StringComparer.Ordinal);

    private static IReadOnlySet<string> SignalRMethods() =>
        new HashSet<string>(["On"], StringComparer.Ordinal);

    private static readonly HashSet<string> IgnoredTypeTokens =
    [
        "bool", "byte", "char", "decimal", "double", "dynamic", "float", "Guid", "int",
        "long", "nint", "nuint", "object", "sbyte", "short", "string", "uint", "ulong",
        "ushort", "DateOnly", "DateTime", "DateTimeOffset", "TimeOnly", "TimeSpan", "Uri",
        "Array", "Dictionary", "Enumerable", "HashSet", "IAsyncEnumerable", "ICollection",
        "IDictionary", "IEnumerable", "IFormFile", "IList", "IReadOnlyCollection",
        "IReadOnlyDictionary", "IReadOnlyList", "JsonDocument", "JsonElement", "JsonNode",
        "JsonObject", "JsonValue", "KeyValuePair", "List", "Memory", "ReadOnlyMemory",
        "ReadOnlySpan", "Span", "Stream", "ValueTuple",
    ];

    private static readonly string[] ProductTypeSuffixes =
    [
        "Configuration",
        "Dto",
        "Entry",
        "Event",
        "Page",
        "Profile",
        "Raw",
        "Request",
        "Response",
        "Result",
        "Settings",
        "Snapshot",
        "ViewModel",
    ];

    [GeneratedRegex(
        @"(?m)^\s*namespace\s+(?<namespace>[A-Za-z_][A-Za-z0-9_.]*)\s*[;{]",
        RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceRegex();

    [GeneratedRegex(@"(?m)^\s*@(?:using|namespace)\s+[^\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex RazorDirectiveRegex();

    [GeneratedRegex(@"(?m)^\s*@using\s+(?<namespace>[A-Za-z_][A-Za-z0-9_.]*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RazorUsingRegex();

    [GeneratedRegex(@"(?m)^\s*@namespace\s+(?<namespace>[A-Za-z_][A-Za-z0-9_.]*)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RazorNamespaceRegex();

    [GeneratedRegex(@"(?m)@(?:code|functions)\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex RazorCodeBlockRegex();

    [GeneratedRegex(
        @"(?m)^\s*(?:global\s+)?using\s+(?<namespace>[A-Za-z_][A-Za-z0-9_.]*)\s*;",
        RegexOptions.CultureInvariant)]
    private static partial Regex UsingRegex();

    [GeneratedRegex(
        @"(?m)^\s*global\s+using\s+(?<namespace>[A-Za-z_][A-Za-z0-9_.]*)\s*;",
        RegexOptions.CultureInvariant)]
    private static partial Regex GlobalUsingRegex();

    [GeneratedRegex(
        @"(?m)^\s*using\s+(?<alias>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<type>[A-Za-z_][A-Za-z0-9_.:]*)\s*;",
        RegexOptions.CultureInvariant)]
    private static partial Regex AliasUsingRegex();

    [GeneratedRegex(
        @"(?m)^\s*(?<access>public|internal|private|protected)?\s*(?:(?:sealed|abstract|static|partial|readonly|ref)\s+)*(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex TypeDeclarationRegex();

    [GeneratedRegex(
        @"\.\s*(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*<",
        RegexOptions.CultureInvariant)]
    private static partial Regex GenericCallStartRegex();

    [GeneratedRegex(
        @"(?:global::)?(?<token>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)",
        RegexOptions.CultureInvariant)]
    private static partial Regex TypeTokenRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private sealed record BoundaryAudit(
        IReadOnlyList<DebtEntry> All,
        IReadOnlyList<DebtEntry> Debt,
        IReadOnlyList<ClassifiedBoundary> Classified);

    private sealed record ClassifiedBoundary(
        DebtEntry Entry,
        BoundaryClassification Classification);

    private sealed record GenericCall(string Method, string TypeExpression, int SourceIndex);

    private sealed record SourceContext(
        string Path,
        string RelativePath,
        string Source,
        string ScrubbedSource,
        string Namespace,
        IReadOnlySet<string> ImportedNamespaces,
        IReadOnlyDictionary<string, string> Aliases);

    private sealed record TypeDefinition(
        string Namespace,
        string Name,
        string Access,
        string RelativePath)
    {
        public string QualifiedName =>
            string.IsNullOrWhiteSpace(Namespace) ? Name : $"{Namespace}.{Name}";

        public bool IsContract =>
            Namespace.StartsWith("MediaEngine.Contracts", StringComparison.Ordinal)
            || RelativePath.StartsWith("src/MediaEngine.Contracts/", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class DebtEntry(
        BoundaryKind kind,
        string packet,
        string relativePath,
        string member,
        string typeExpression,
        IReadOnlyList<TypeDefinition> offenders,
        int sourceIndex,
        string location)
    {
        public BoundaryKind Kind { get; } = kind;
        public string Packet { get; } = packet;
        public string RelativePath { get; } = relativePath;
        public string Member { get; } = member;
        public string TypeExpression { get; } = typeExpression;
        public IReadOnlyList<TypeDefinition> Offenders { get; } = offenders;
        public int SourceIndex { get; } = sourceIndex;
        public string Location { get; } = location;
        public int Occurrence { get; set; }

        public string UnnumberedKey =>
            $"{Kind}|{Packet}|{RelativePath}|{Member}|{TypeExpression}|"
            + string.Join(",", Offenders.Select(item => item.QualifiedName));

        public string Key =>
            $"{Kind}|{Packet}|{RelativePath}|{Member}|{TypeExpression}|{Occurrence}|"
            + string.Join(",", Offenders.Select(item => item.QualifiedName));
    }

    private sealed class TypeIndex
    {
        private readonly Dictionary<string, List<TypeDefinition>> _byName;
        private readonly Dictionary<string, List<TypeDefinition>> _byQualifiedName;
        private readonly IReadOnlyList<string> _webGlobalUsings;
        private readonly IReadOnlyList<string> _apiGlobalUsings;

        public TypeIndex(IEnumerable<TypeDefinition> definitions)
        {
            var materialized = definitions.ToList();
            _byName = materialized
                .GroupBy(item => item.Name, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            _byQualifiedName = materialized
                .GroupBy(item => item.QualifiedName, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
            _webGlobalUsings = ReadGlobalUsings("MediaEngine.Web");
            _apiGlobalUsings = ReadGlobalUsings("MediaEngine.Api");
        }

        public IReadOnlyList<TypeDefinition> Find(string name) =>
            _byName.TryGetValue(name, out var values) ? values : [];

        public IReadOnlyList<TypeDefinition> FindQualified(string name)
        {
            var normalized = name.Replace("global::", string.Empty, StringComparison.Ordinal);
            if (_byQualifiedName.TryGetValue(normalized, out var exact))
            {
                return exact;
            }

            return _byQualifiedName
                .Where(pair => pair.Key.EndsWith($".{normalized}", StringComparison.Ordinal))
                .SelectMany(pair => pair.Value)
                .ToList();
        }

        public IEnumerable<string> GlobalUsingsFor(string sourcePath)
        {
            if (sourcePath.Contains(
                    $"{Path.DirectorySeparatorChar}MediaEngine.Web{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            {
                return _webGlobalUsings;
            }

            if (sourcePath.Contains(
                    $"{Path.DirectorySeparatorChar}MediaEngine.Api{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            {
                return _apiGlobalUsings;
            }

            return [];
        }

        private static IReadOnlyList<string> ReadGlobalUsings(string projectName)
        {
            var projectRoot = Path.Combine(RepoRoot, "src", projectName);
            return Directory.EnumerateFiles(projectRoot, "*.cs", SearchOption.AllDirectories)
                .Where(path => !HasPathSegment(path, "bin") && !HasPathSegment(path, "obj"))
                .SelectMany(path => GlobalUsingRegex().Matches(File.ReadAllText(path)))
                .Select(match => match.Groups["namespace"].Value)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
    }

    private readonly record struct ParsedDebtLine(BoundaryKind Kind, string Packet);

    private enum BoundaryKind
    {
        Endpoint,
        WebJson,
        SignalR,
    }

    private enum BoundaryClassification
    {
        FrozenUniverseChronicle,
        PresentationOnly,
        InternalProjection,
        DevelopmentOnlyWire,
    }

    private enum LexicalState
    {
        Code,
        LineComment,
        BlockComment,
        String,
        VerbatimString,
        Character,
        RawString,
    }
}
