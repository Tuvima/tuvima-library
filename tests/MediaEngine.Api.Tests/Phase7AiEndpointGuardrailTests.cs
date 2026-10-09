namespace MediaEngine.Api.Tests;

public sealed class Phase7AiEndpointGuardrailTests
{
    [Fact]
    public void AiEndpoints_ParseEveryConfiguredRoleAndDeclareTypedPermissions()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Api/Endpoints/AiEndpoints.cs");
        var roleSource = ReadRepoFile(@"src/MediaEngine.Domain/Enums/AiModelRole.cs");

        Assert.Contains("TryParseModelRole", source, StringComparison.Ordinal);
        Assert.Contains("AiModelDefinitions.ToRoleKey", source, StringComparison.Ordinal);
        Assert.Contains("Enum.GetValues<AiModelRole>().Select(ToRoleKey)", source, StringComparison.Ordinal);
        Assert.Contains("TextFast", roleSource, StringComparison.Ordinal);
        Assert.Contains("TextQuality", roleSource, StringComparison.Ordinal);
        Assert.Contains("TextScholar", roleSource, StringComparison.Ordinal);
        Assert.Contains("TextCjk", roleSource, StringComparison.Ordinal);
        Assert.Contains("Audio", roleSource, StringComparison.Ordinal);
        // Actual mapped metadata and live denial behavior are covered by
        // MappedEndpointInventoryTests and AccessAuthorityIntegrationTests.
        Assert.Contains("RequireAdministratorOrApplication(ApplicationPermissionIds.AiStatusRead)", source, StringComparison.Ordinal);
        Assert.Contains("RequireAdministratorOrApplication(ApplicationPermissionIds.AiManage)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AiConfigSave_ValidatesBeforePersisting()
    {
        var endpoint = ReadRepoFile(@"src/MediaEngine.Api/Endpoints/AiEndpoints.cs");
        var store = ReadRepoFile(@"src/MediaEngine.Api/Services/AiConfigurationService.cs");

        Assert.Contains("configurationStore.Save", endpoint, StringComparison.Ordinal);
        Assert.Contains("ValidationProblem", endpoint, StringComparison.Ordinal);
        Assert.Contains("AiSettingsValidator.Validate", store, StringComparison.Ordinal);
        Assert.Contains("CronExpression.Parse", store, StringComparison.Ordinal);
        Assert.Contains("Advanced requires a successful benchmark", store, StringComparison.Ordinal);
    }

    [Fact]
    public void AiModelStatus_ExposesSelectedRuntimeFactsWithoutExperimentEndpoints()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Api/Endpoints/AiEndpoints.cs");

        Assert.DoesNotContain("/benchmark/suites", source, StringComparison.Ordinal);
        Assert.Contains("SelectionRationale", source, StringComparison.Ordinal);
        Assert.Contains("ValidationWarnings", source, StringComparison.Ordinal);
        Assert.Contains("Capabilities", source, StringComparison.Ordinal);
        Assert.Contains("DiskStatus", source, StringComparison.Ordinal);
        Assert.Contains("MemoryEnvelopeMB", source, StringComparison.Ordinal);
        Assert.Contains("ChecksumStatus", source, StringComparison.Ordinal);
        Assert.Contains("ModelInventory inventory", source, StringComparison.Ordinal);
        Assert.Contains("inventory.GetModelPath(status.Role)", source, StringComparison.Ordinal);
        Assert.Equal(0, CountOccurrences(source, "configLoader.LoadAi<AiSettings>() ?? new AiSettings()"));
        Assert.DoesNotContain("?? new AiSettings()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AiOperations_ReturnTypedProblemsWithoutRawExceptionDetails()
    {
        var source = ReadRepoFile(@"src/MediaEngine.Api/Endpoints/AiEndpoints.cs");

        Assert.Contains("unknown-model-role", source, StringComparison.Ordinal);
        Assert.DoesNotContain("evaluation-failed", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ex.Message}", source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string pattern) =>
        value.Split(pattern, StringSplitOptions.None).Length - 1;

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            relativePath)));
}
