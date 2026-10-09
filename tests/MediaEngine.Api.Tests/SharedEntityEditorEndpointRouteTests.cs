using MediaEngine.Api.Endpoints;
using MediaEngine.Contracts.Universe;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Api.Tests;

public sealed class SharedEntityEditorEndpointRouteTests
{
    [Fact]
    public void SharedEditorRoutes_DeclareGraphTargetsPermissionsAndMultipartUploads()
    {
        var source = File.ReadAllText(RepoFile(@"src\MediaEngine.Api\Endpoints\SharedEntityEditorEndpoints.cs"));
        Assert.Contains("/entity-editor", source, StringComparison.Ordinal);
        Assert.Contains("/universes/{qid}/entities/{id:guid}/context", source, StringComparison.Ordinal);
        Assert.Contains("/universes/{qid}/artwork/{assetType}/upload", source, StringComparison.Ordinal);
        Assert.Contains("/universes/{qid}/entities/{id:guid}/artwork/{assetType}/upload", source, StringComparison.Ordinal);
        Assert.Contains("Accepts<IFormFile>(\"multipart/form-data\")", source, StringComparison.Ordinal);
        Assert.Contains("RequireAdministratorOrApplication(ApplicationPermissionIds.MetadataWrite)", source, StringComparison.Ordinal);
        Assert.Contains("trigger_type\"] = \"universe_sweep\"", source, StringComparison.Ordinal);
        Assert.Contains("FindWorkIdsByProvenanceQidAsync", source, StringComparison.Ordinal);
        Assert.Contains("PagedRequest.From(offset, limit, defaultLimit: 50, maxLimit: 100)", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new(\"files\"", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SharedEditorContracts_HaveStableGraphSectionsAndBreadcrumb()
    {
        var source = File.ReadAllText(RepoFile(@"src\MediaEngine.Contracts\Universe\SharedEntityEditorContracts.cs"));
        Assert.Contains("public const string Universe = \"Universe\"", source, StringComparison.Ordinal);
        Assert.Contains("public const string FictionalEntity = \"FictionalEntity\"", source, StringComparison.Ordinal);
        Assert.Contains("public const string Artwork = \"artwork\"", source, StringComparison.Ordinal);
        Assert.Contains("breadcrumb", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Files =", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RootGraphProjections_PageAuthorizedEntities_AndExposeFactQualifierProvenanceAndTime()
    {
        var source = File.ReadAllText(RepoFile(@"src\MediaEngine.Api\Endpoints\SharedEntityEditorEndpoints.cs"));

        Assert.Contains("LoadAllVisibleEntitiesAsync", source, StringComparison.Ordinal);
        Assert.Contains("const int pageSize = 100", source, StringComparison.Ordinal);
        Assert.Contains("offset >= page.Total", source, StringComparison.Ordinal);
        Assert.Contains("RelationshipSources", source, StringComparison.Ordinal);
        Assert.Contains("RelationshipTimeline", source, StringComparison.Ordinal);
        Assert.Contains("GraphQualifierType.PointInTime", source, StringComparison.Ordinal);
        Assert.Contains("GraphQualifierType.StartTime", source, StringComparison.Ordinal);
        Assert.Contains("GraphQualifierType.EndTime", source, StringComparison.Ordinal);
        Assert.Contains("GraphQualifierType.TimeIndex", source, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrWhiteSpace(row.StartTime) && string.IsNullOrWhiteSpace(row.EndTime)", source, StringComparison.Ordinal);
        Assert.Contains("? qualifierEntries", source, StringComparison.Ordinal);
        Assert.Contains(": qualifierEntries.Append", source, StringComparison.Ordinal);
        Assert.Contains("q.Provenance", source, StringComparison.Ordinal);
        Assert.Contains("q.SourceProvider", source, StringComparison.Ordinal);
        Assert.Contains("q.IsSupplemental", source, StringComparison.Ordinal);
        Assert.Contains("q.Confidence", source, StringComparison.Ordinal);
        Assert.Contains("row.ContextWorkQid", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CanonicalTimeline_ProjectsDirectEventTimesAndNarrativeIndexes()
    {
        var method = typeof(SharedEntityEditorEndpoints).GetMethod("CanonicalTimeline", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var result = (IEnumerable<SharedEntityTimelineEntryDto>)method!.Invoke(null,
        [
            new FictionalEntity { Id = Guid.NewGuid(), EntitySubType = "Event" },
            new List<CanonicalValue>
            {
                new() { Key = "point_in_time", Value = "+0019-01-01T00:00:00Z" },
                new() { Key = "start_time", Value = "+0018-01-01T00:00:00Z" },
                new() { Key = "end_time", Value = "+0020-01-01T00:00:00Z" },
                new() { Key = "time_index", Value = "battle-12" },
            },
        ])!;

        var entries = result.ToList();
        Assert.Equal(4, entries.Count);
        Assert.Contains(entries, entry => entry.value == "battle-12" && entry.start_time is null && entry.end_time is null);
        Assert.Contains(entries, entry => entry.start_time == "+0018-01-01T00:00:00Z");
        Assert.Contains(entries, entry => entry.end_time == "+0020-01-01T00:00:00Z");
    }

    [Fact]
    public void EntityCapabilities_AreSubtypeAware_AndNeverExposeFiles()
    {
        var method = typeof(SharedEntityEditorEndpoints).GetMethod("EntityCapabilities", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);

        var organization = (IReadOnlyList<SharedEntityEditorCapabilityDto>)method!.Invoke(null, ["Organization"])!;
        var @event = (IReadOnlyList<SharedEntityEditorCapabilityDto>)method.Invoke(null, ["Event"])!;
        var @object = (IReadOnlyList<SharedEntityEditorCapabilityDto>)method.Invoke(null, ["Object"])!;

        Assert.Contains(organization, capability => capability.section == SharedEntityEditorSections.Members);
        Assert.Contains(organization, capability => capability.section == SharedEntityEditorSections.Appearances);
        Assert.Contains(@event, capability => capability.section == SharedEntityEditorSections.Participants);
        Assert.Contains(@event, capability => capability.section == SharedEntityEditorSections.Appearances);
        Assert.Contains(@object, capability => capability.section == SharedEntityEditorSections.Artwork && capability.editable);
        Assert.DoesNotContain(organization.Concat(@event).Concat(@object), capability => string.Equals(capability.section, "files", StringComparison.OrdinalIgnoreCase));
    }

    private static string RepoFile(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = "")
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(sourceFile)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, relative);
    }
}
