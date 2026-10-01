namespace MediaEngine.Web.Tests;

public sealed class EditorDetailsReadFirstTests
{
    [Fact]
    public void DetailsRendersOneScopeAwareReadFirstInspector()
    {
        var root = FindRepositoryRoot();
        var source = Read(root, "src", "MediaEngine.Web", "Components", "MediaEditor", "SharedMediaEditorShell.razor");
        var markup = ExtractDetailsBody(source);
        var presentation = Read(root, "src", "MediaEngine.Web", "Components", "MediaEditor", "SharedMediaEditorShell.DetailsPresentation.cs");

        Assert.Contains("sme-details-inspector__grid", markup, StringComparison.Ordinal);
        Assert.Contains("DetailsHeading", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailsPrimaryFacts", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("sme-details-fact-strip", markup, StringComparison.Ordinal);
        Assert.Contains("DetailsSecondaryFields", markup, StringComparison.Ordinal);
        Assert.Contains("DetailsSynopsis", markup, StringComparison.Ordinal);
        Assert.Contains("DetailsFiles", markup, StringComparison.Ordinal);
        Assert.Contains("DetailsLibrarySummary", markup, StringComparison.Ordinal);
        Assert.Contains("DetailsTechnicalFacts", markup, StringComparison.Ordinal);
        Assert.Contains("DetailsImageUrl", markup, StringComparison.Ordinal);
        Assert.Contains("DetailsImageSrcSet", markup, StringComparison.Ordinal);
        Assert.Contains("DetailsImageSizes", markup, StringComparison.Ordinal);
        Assert.Contains("DetailsMetadataWritebackState", markup, StringComparison.Ordinal);
        Assert.Contains("DetailsImageUrl =>", presentation, StringComparison.Ordinal);
        Assert.Contains("EditorMediaType == \"TV\" && scope.ScopeId == \"series\"", presentation, StringComparison.Ordinal);
        Assert.Contains("GetExactScopeArtworkVariant(scope, \"Background\")", presentation, StringComparison.Ordinal);
        Assert.Contains("EditorMediaType == \"Music\" && scope.ScopeId == \"track\"", presentation, StringComparison.Ordinal);
        Assert.Contains("GetExactScopeArtworkVariant(GetScopeById(scope.ArtworkOwnerScopeId), \"CoverArt\")", presentation, StringComparison.Ordinal);
        Assert.DoesNotContain("Edit file tools", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("_editorContext?.FileMetadataSyncStatus", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginDetailsEdit", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineEditsSaveOnlyTheSelectedOverrideAndProtectDraftsDuringNavigation()
    {
        var root = FindRepositoryRoot();
        var code = Read(root, "src", "MediaEngine.Web", "Components", "MediaEditor", "SharedMediaEditorShell.razor.cs");
        var editing = Read(root, "src", "MediaEngine.Web", "Components", "MediaEditor", "SharedMediaEditorShell.DetailsEditing.cs");

        Assert.Contains("HasPendingDetailsInlineEdit", code, StringComparison.Ordinal);
        Assert.Contains("HasPendingNavigationChanges && ActiveScope is not null", code, StringComparison.Ordinal);
        Assert.Contains("GetItemHistoryWithStatusAsync(ActiveScope.FieldEntityId)", code, StringComparison.Ordinal);
        Assert.Contains("SaveItemDisplayOverridesAsync", editing, StringComparison.Ordinal);
        Assert.Contains("[overrideKey] = value", editing, StringComparison.Ordinal);
        Assert.DoesNotContain("SaveAsyncCore", editing, StringComparison.Ordinal);
        Assert.Contains("SaveProfileEditorPreferencesAsync", editing, StringComparison.Ordinal);
        Assert.Contains("ClearDetailsInlineEdit();", code, StringComparison.Ordinal);
        Assert.Contains("HasPendingDetailsInlineEdit", editing, StringComparison.Ordinal);
    }

    private static string Read(string root, params string[] parts) => File.ReadAllText(Path.Combine(root, Path.Combine(parts)));

    private static string ExtractDetailsBody(string source)
    {
        const string startMarker = "else if (_activeTab == \"details\")";
        const string endMarker = "else if (_activeTab == \"artwork\")";
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        var end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Could not isolate the Details branch.");
        return source[start..end];
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
