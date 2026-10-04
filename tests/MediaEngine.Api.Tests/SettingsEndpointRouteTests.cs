using System.IO;

namespace MediaEngine.Api.Tests;

public sealed class SettingsEndpointRouteTests
{
    [Fact]
    public void SystemEndpoints_ExposeNonDestructiveRestoreValidation()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\Endpoints\SystemEndpoints.cs"));

        Assert.Contains("MapPost(\"/system/backups/validate\"", source, StringComparison.Ordinal);
        Assert.Contains("backups.ValidateRestore(request.FileName)", source, StringComparison.Ordinal);
        Assert.Contains("WithName(\"ValidateBackupRestore\")", source, StringComparison.Ordinal);
        var validationStart = source.IndexOf("MapPost(\"/system/backups/validate\"", StringComparison.Ordinal);
        var restoreStart = source.IndexOf("MapPost(\"/system/backups/restore\"", StringComparison.Ordinal);
        Assert.True(restoreStart > validationStart);
        var validation = source[validationStart..restoreStart];
        Assert.Contains("RequireAdministratorOrApplication(ApplicationPermissionIds.BackupRead)", validation, StringComparison.Ordinal);
        Assert.DoesNotContain("backups.ScheduleRestore", validation, StringComparison.Ordinal);
    }

    [Fact]
    public void OrganizationTemplatePreview_IsReadOnlyEndpointBesideExplicitSave()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\Endpoints\SettingsEndpoints.cs"));

        Assert.Contains("grp.MapPost(\"/organization-template/preview\"", source, StringComparison.Ordinal);
        Assert.Contains(".WithName(\"PreviewOrganizationTemplate\")", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapPut(\"/organization-template\"", source, StringComparison.Ordinal);
        Assert.Contains("configLoader.SaveCore(core);", source, StringComparison.Ordinal);

        var previewStart = source.IndexOf("grp.MapPost(\"/organization-template/preview\"", StringComparison.Ordinal);
        var putStart = source.IndexOf("grp.MapPut(\"/organization-template\"", StringComparison.Ordinal);
        Assert.True(previewStart >= 0);
        Assert.True(putStart > previewStart);
        Assert.DoesNotContain("SaveCore", source[previewStart..putStart], StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileEndpoints_ExposePlaybackSettingsRoutes()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\Endpoints\ProfileEndpoints.cs"));

        Assert.Contains("MapGet(\"/{id:guid}/settings/playback\"", source, StringComparison.Ordinal);
        Assert.Contains("MapPut(\"/{id:guid}/settings/playback\"", source, StringComparison.Ordinal);
        Assert.Contains("IUserPlaybackSettingsService", source, StringComparison.Ordinal);
        Assert.Contains("GetOrCreateDefaultsAsync", source, StringComparison.Ordinal);
        Assert.Contains("UpdateAsync(id, request", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsEndpoints_ExposePhase6AdminControlRoutes()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\Endpoints\SettingsEndpoints.cs"));

        Assert.DoesNotContain("grp.MapGet(\"/folders\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("grp.MapPut(\"/folders\"", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapGet(\"/libraries\"", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapPut(\"/libraries\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("grp.MapGet(\"/incoming-sources\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("grp.MapPut(\"/incoming-sources\"", source, StringComparison.Ordinal);
        Assert.Contains("configLoader.SaveLibraries", source, StringComparison.Ordinal);
        Assert.Contains("JsonConfigValidator.Validate", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryFolderResolver.ValidateNoOverlap", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SourcePaths", source, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryRoot = library", source, StringComparison.Ordinal);
        Assert.DoesNotContain("IntakeMode = library", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadOnly = library", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapPost(\"/test-path\"", source, StringComparison.Ordinal);
        Assert.Contains("HasRead", source, StringComparison.Ordinal);
        Assert.Contains("HasWrite", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapGet(\"/providers\"", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapGet(\"/providers/health\"", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapPost(\"/providers/{name}/test\"", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapPost(\"/providers/{name}/credentials/test\"", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapPut(\"/providers/{name}/credentials\"", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapDelete(\"/providers/{name}/credentials\"", source, StringComparison.Ordinal);
        Assert.Contains("Restart the Engine after changing provider configuration", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new ConfigDrivenAdapter", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapPut(\"/providers/{name}/config\"", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapGet(\"/pipelines\"", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapGet(\"/pipelines/defaults\"", source, StringComparison.Ordinal);
        Assert.Contains("pipeline-priority-defaults", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapPut(\"/pipelines\"", source, StringComparison.Ordinal);
        Assert.Contains("grp.MapGet(\"/media-types\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderSettingsWrites_KeepCredentialsOutOfProviderManifests()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\Endpoints\SettingsEndpoints.cs"));

        Assert.DoesNotContain("SaveProviderSecrets", source, StringComparison.Ordinal);
        Assert.DoesNotContain("request.ApiKey", source, StringComparison.Ordinal);
        Assert.Contains("http.ApiKey = null;", source, StringComparison.Ordinal);
        Assert.Contains("http.Username = null;", source, StringComparison.Ordinal);
        Assert.Contains("http.Password = null;", source, StringComparison.Ordinal);
        Assert.Contains("SaveProviderManifest(configLoader, provider, connectionChecks);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderSettingsStatus_DoesNotRunLiveExternalProbesOnPageLoad()
    {
        var source = File.ReadAllText(GetRepoFilePath(@"src\MediaEngine.Api\Endpoints\SettingsEndpoints.cs"));
        var start = source.IndexOf("grp.MapGet(\"/providers\", async", StringComparison.Ordinal);
        var end = source.IndexOf(".WithName(\"GetProviderStatus\")", StringComparison.Ordinal);

        Assert.True(start >= 0);
        Assert.True(end > start);
        var endpoint = source[start..end];

        Assert.Contains("healthRepo.GetAllAsync", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("IHttpClientFactory", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("SendAsync", endpoint, StringComparison.Ordinal);
        Assert.DoesNotContain("GetBaseUrlForProvider", endpoint, StringComparison.Ordinal);
    }

    private static string GetRepoFilePath(string relativePath) =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath));
}
