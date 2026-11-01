using System.Reflection;
using System.Text.Json.Serialization;
using MediaEngine.Contracts.Collections;

namespace MediaEngine.Contracts.Tests;

public sealed class LegacyCollectionContractTests
{
    [Fact]
    public void Work_detail_contract_keeps_complete_nested_wire_graph()
    {
        AssertJsonFields<WorkDetailDto>(
            "id", "collection_id", "parent_work_id", "media_type", "work_kind", "ordinal",
            "is_catalog_only", "wikidata_qid", "canonical_values", "editions");
        AssertJsonFields<EditionDto>(
            "id", "work_id", "format_label", "wikidata_qid", "canonical_values", "assets");
        AssertJsonFields<EditionAssetDto>(
            "id", "edition_id", "file_path_root", "status", "canonical_values");
        AssertJsonFields<CanonicalValueDto>("key", "value", "winning_provider_id", "last_scored_at");
    }

    [Fact]
    public void Library_work_feed_contract_preserves_existing_camel_case_wire()
    {
        AssertJsonFields<LibraryWorkListItemDto>(
            "id", "collectionId", "rootWorkId", "mediaType", "workKind", "ordinal",
            "wikidataQid", "assetId", "createdAt", "coverUrl", "backgroundUrl",
            "bannerUrl", "heroUrl", "logoUrl", "canonicalValues");
    }

    [Fact]
    public void Collection_mutation_contracts_keep_all_rule_and_setting_fields()
    {
        AssertJsonFields<CollectionCreateRequest>(
            "name", "description", "visibility", "icon_name", "collection_type", "rule_definition",
            "membership_mode", "primary_area", "owner_kind", "audience", "selected_profile_ids",
            "sort_field", "sort_direction", "secondary_sort_field", "secondary_sort_direction", "display_limit", "placements", "work_ids",
            "min_items", "max_items", "season_from", "season_to", "show_on");
        AssertJsonFields<CollectionUpdateRequest>(
            "name", "description", "visibility", "icon_name", "rule_definition",
            "membership_mode", "primary_area", "owner_kind", "audience", "selected_profile_ids",
            "sort_field", "sort_direction", "secondary_sort_field", "secondary_sort_direction", "is_enabled", "is_featured", "work_ids",
            "min_items", "max_items", "season_from", "season_to", "show_on");
        AssertJsonFields<CollectionRuleDefinitionDto>("version", "groups");
        AssertJsonFields<CollectionRuleGroupDto>("id", "join_with_previous", "match_mode", "conditions");
        AssertJsonFields<CollectionRulePredicateDto>("display_value", "display_values", "field", "op", "value", "values");
    }

    [Fact]
    public void Series_manifest_contract_keeps_scope_totals_and_identity_fields()
    {
        AssertJsonFields<SeriesManifestViewDto>(
            "collection_id", "series_qid", "series_label", "last_hydrated_at", "container_kind",
            "expected_total", "expected_total_kind", "expected_total_source",
            "expected_total_confidence", "total_count", "owned_count", "missing_count",
            "provisional_count", "ambiguous_count", "supplementary_count",
            "collected_content_count", "unpositioned_count", "authoritative_totals_by_container",
            "warnings", "items");
        AssertJsonFields<SeriesManifestItemDto>(
            "id", "item_qid", "series_qid", "item_label", "item_description", "media_type",
            "media_kind", "instance_of_qids", "raw_ordinal", "parsed_ordinal", "ordinal_scope_qid",
            "sort_order", "publication_date", "duration", "parent_collection_qid",
            "parent_collection_label", "is_collection", "is_expanded_from_collection",
            "membership_scope", "order_source", "ownership_state", "linked_work_id");
    }

    [Fact]
    public void Pack_contracts_keep_the_wire_names_the_pack_file_and_the_pack_step_share()
    {
        AssertJsonFields<CollectionPackFileDto>("format", "version", "name", "description", "creates_set", "collections");
        AssertJsonFields<CollectionPackFileEntryDto>(
            "name", "description", "icon_name", "primary_area", "rule_definition", "sort_field", "sort_direction",
            "secondary_sort_field", "secondary_sort_direction", "min_items", "max_items", "season_from", "season_to", "artwork_hint");
        AssertJsonFields<CollectionPackSummaryDto>(
            "id", "name", "description", "icon_name", "creates_set", "would_create_count", "uses_per_profile_fields", "collections");
        AssertJsonFields<CollectionPackEntryDto>("name", "description", "item_count", "media_type_counts", "season_text");
        AssertJsonFields<CollectionPackCreateRequest>(
            "pack_ids", "file_content", "media_types", "owner_kind", "audience", "selected_profile_ids", "show_on");
        AssertJsonFields<CollectionPackCreateResponse>("sets", "collections", "skipped");
        AssertJsonFields<CollectionPackExportResponse>("file", "omitted");
    }

    [Fact]
    public void Pack_file_round_trips_and_names_itself()
    {
        var file = new CollectionPackFileDto
        {
            Name = "Decades",
            Collections = [new CollectionPackFileEntryDto { Name = "1990s", SeasonFrom = "10-01", SeasonTo = "11-01" }],
        };

        var json = global::System.Text.Json.JsonSerializer.Serialize(file);
        var back = global::System.Text.Json.JsonSerializer.Deserialize<CollectionPackFileDto>(json)!;

        Assert.Contains("\"format\":\"tuvima-collection-pack\"", json, StringComparison.Ordinal);
        Assert.Equal("1990s", back.Collections.Single().Name);
        Assert.Equal("10-01", back.Collections.Single().SeasonFrom);
        Assert.Equal("stack", back.Collections.Single().ArtworkHint);
    }

    private static void AssertJsonFields<T>(params string[] expected)
    {
        var actual = typeof(T)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected.OrderBy(name => name, StringComparer.Ordinal), actual);
    }
}
