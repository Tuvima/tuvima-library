using MediaEngine.Storage.Backup;

namespace MediaEngine.Storage.Tests;

/// <summary>Finds the checked-out public docs (the tuvima-docs repository) for the drift checks.</summary>
internal static class PublicDocsCheckout
{
    public const string Variable = "TUVIMA_DOCS_DIR";

    private const string PagesFolder = "content/products/library";

    /// <summary>The folder holding the product pages, or null when the docs repository is not checked out next to this one.</summary>
    public static string? FindPages()
    {
        var configured = Environment.GetEnvironmentVariable(Variable);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            candidates.Add(configured);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory?.Parent is not null)
        {
            candidates.Add(Path.Combine(directory.Parent.FullName, "tuvima-docs"));
        }

        return candidates
            .Select(candidate => Path.Combine(candidate, PagesFolder))
            .FirstOrDefault(Directory.Exists);
    }

    public static string ReadPage(string relativePath)
    {
        var root = FindPages() ?? throw new DirectoryNotFoundException($"Set {Variable} to the tuvima-docs checkout.");
        var path = Path.Combine(root, relativePath);
        Assert.True(File.Exists(path), $"The public docs page {relativePath} is missing from the tuvima-docs repository. Update the page, or this check if the page moved.");
        return File.ReadAllText(path);
    }
}

/// <summary>Runs only when the public docs are checked out (CI does this); otherwise skipped with a clear message.</summary>
public sealed class PublicDocsFactAttribute : FactAttribute
{
    public PublicDocsFactAttribute()
    {
        if (PublicDocsCheckout.FindPages() is null)
        {
            Skip = $"Check out Tuvima/tuvima-docs next to this repository, or set {PublicDocsCheckout.Variable} to it, to run the public docs drift checks.";
        }
    }
}

/// <summary>
/// Keeps public pages in tuvima-docs from quietly drifting away from the app. These checks lived next to the pages
/// before the docs moved to their own repository; they read the pages from a checkout of that repository.
/// </summary>
public sealed class PublicDocsDriftTests
{
    [PublicDocsFact]
    public void BackupAndRestorePage_ListsEveryClassifiedTable()
    {
        var doc = PublicDocsCheckout.ReadPage("architecture/backup-and-restore.md");

        var missing = BackupTableClassification.Rules.Keys
            .Where(table => !doc.Contains($"`{table}`", StringComparison.Ordinal))
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "tuvima-docs architecture/backup-and-restore.md does not list these classified tables in backticks: " + string.Join(", ", missing));
    }

    [PublicDocsFact]
    public void PipelinePages_DescribeMusicBrainzFirstMusicIdentity()
    {
        var providerGuide = PublicDocsCheckout.ReadPage("guides/configuring-providers.md");
        var architecture = PublicDocsCheckout.ReadPage("architecture/ingestion-identity-enrichment-pipeline.md");
        var mediaTypes = PublicDocsCheckout.ReadPage("reference/media-types.md");

        Assert.Contains("MusicBrainz tries identifiers and staged text searches first", providerGuide, StringComparison.Ordinal);
        Assert.Contains("For music, Stage 1 is a bounded configured flow", architecture, StringComparison.Ordinal);
        Assert.Contains("MusicBrainz - Stage 1 configured identifier lookup", mediaTypes, StringComparison.Ordinal);
        Assert.Contains("Apple API - Stage 1 enrichment or fallback identity", mediaTypes, StringComparison.Ordinal);
    }

    [PublicDocsFact]
    public void PresentationRules_PutDevicesUnderSecurity()
    {
        var presentationRules = PublicDocsCheckout.ReadPage("product/presentation-rules.md");

        Assert.DoesNotContain("Administration > Devices", presentationRules, StringComparison.Ordinal);
        Assert.Contains("/settings/security/devices", presentationRules, StringComparison.Ordinal);
    }

    [PublicDocsFact]
    public void AttributionsPage_CreditsExifTool()
    {
        var attributions = PublicDocsCheckout.ReadPage("policies/attributions.md");

        Assert.Contains("| ExifTool |", attributions, StringComparison.Ordinal);
        Assert.Contains("Perl Artistic License or GNU General Public License", attributions, StringComparison.Ordinal);
        Assert.Contains("Strawberry Perl", attributions, StringComparison.Ordinal);
    }
}
