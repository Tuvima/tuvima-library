using MediaEngine.Web.Components.Settings;

namespace MediaEngine.Web.Tests;

public sealed class ProviderManagementStateTests
{
    [Fact]
    public void ProviderSettings_UsesSharedOnboardingWithoutProviderSpecificInstructions()
    {
        var page = File.ReadAllText(FindRepoFile(
            "src", "MediaEngine.Web", "Components", "Settings", "MetadataSettingsPage.razor"));
        var dialog = File.ReadAllText(FindRepoFile(
            "src", "MediaEngine.Web", "Components", "Shared", "Providers", "ProviderOnboardingDialog.razor"));

        Assert.Contains("OpenProviderDialogAsync", page, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Steps", dialog, StringComparison.Ordinal);
        Assert.Contains("KeyLink", dialog, StringComparison.Ordinal);
        Assert.Contains("Onboarding.Troubleshooting", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("TMDB API Key", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Comic Vine API Key", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TMDB API Key", dialog, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Comic Vine API Key", dialog, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EditorDraft_DoesNotSendCredentialsThroughProviderConfiguration()
    {
        var item = new ProviderManagementItem
        {
            Key = "tmdb",
            HasKey = true,
            RequiresKey = true,
            Endpoints = new Dictionary<string, string> { ["api"] = "https://example.test" },
        };

        var draft = ProviderEditorDraft.From(item);
        var update = draft.ToUpdate("api");

        Assert.NotNull(update);
        Assert.DoesNotContain("Credential", string.Join(' ', typeof(ProviderEditorDraft).GetMembers().Select(member => member.Name)), StringComparison.Ordinal);
    }

    [Fact]
    public void SharedCredentialForm_RendersOnlyUserSuppliedFields()
    {
        var form = File.ReadAllText(FindRepoFile(
            "src", "MediaEngine.Web", "Components", "Shared", "Providers", "ProviderCredentialForm.razor"));
        var dialog = File.ReadAllText(FindRepoFile(
            "src", "MediaEngine.Web", "Components", "Shared", "Providers", "ProviderOnboardingDialog.razor"));

        Assert.Contains("IsUserSupplied", form, StringComparison.Ordinal);
        Assert.Contains("user_supplied", form, StringComparison.Ordinal);
        Assert.Contains("user_supplied", dialog, StringComparison.Ordinal);
        Assert.DoesNotContain("application_managed", form, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorDraft_FingerprintTracksAdvancedAndConnectionChanges()
    {
        var draft = ProviderEditorDraft.From(new ProviderManagementItem { Key = "tmdb" });
        var baseline = draft.Fingerprint();

        draft.MaxConcurrency++;

        Assert.NotEqual(baseline, draft.Fingerprint());
    }

    private static string FindRepoFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(string.Join(Path.DirectorySeparatorChar, segments));
    }
}
