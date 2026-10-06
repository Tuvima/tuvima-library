namespace MediaEngine.Api.Tests;

public sealed class DockerfileGuardrailTests
{
    [Fact]
    public void Dockerfile_CopiesExplicitProjectImportsBeforeRestore()
    {
        var root = FindRepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));
        var beforeRestore = dockerfile[..dockerfile.IndexOf("RUN dotnet restore", StringComparison.Ordinal)];
        var copies = System.Text.RegularExpressions.Regex.Matches(beforeRestore, @"(?m)^COPY\s+(\S+)\s+(\S+)\s*$")
            .Select(match => (Source: match.Groups[1].Value.TrimEnd('/'), Destination: match.Groups[2].Value.TrimEnd('/'))).ToArray();
        var projects = System.Text.RegularExpressions.Regex.Matches(dockerfile, @"dotnet\s+(?:restore|publish)\s+(\S+\.csproj)")
            .Select(match => match.Groups[1].Value).Distinct();
        foreach (var project in projects)
        {
            var projectPath = Path.Combine(root, project);
            var document = System.Xml.Linq.XDocument.Load(projectPath);
            foreach (var import in document.Descendants().Where(element => element.Name.LocalName == "Import"))
            {
                var value = import.Attribute("Project")?.Value;
                if (value is null || value.Contains("$(", StringComparison.Ordinal))
                {
                    continue;
                }

                var importedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, value));
                Assert.True(File.Exists(importedPath), $"Missing project import: {importedPath}");
                var relative = Path.GetRelativePath(root, importedPath).Replace('\\', '/');
                Assert.Contains(copies, copy =>
                    (relative == copy.Source || relative.StartsWith(copy.Source + "/", StringComparison.Ordinal))
                    && (relative == copy.Destination || relative.StartsWith(copy.Destination + "/", StringComparison.Ordinal)));
            }
        }
    }

    [Fact]
    public void Dockerfile_CopiesReferencedProjectsBeforeRestore()
    {
        var repoRoot = FindRepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(repoRoot, "Dockerfile"));

        var restoreIndex = dockerfile.IndexOf("RUN dotnet restore", StringComparison.Ordinal);
        Assert.True(restoreIndex > 0, "Dockerfile should restore after copying project files.");

        string[] requiredProjects =
        [
            "src/MediaEngine.Contracts/MediaEngine.Contracts.csproj",
            "src/MediaEngine.Domain/MediaEngine.Domain.csproj",
            "src/MediaEngine.Storage/MediaEngine.Storage.csproj",
            "src/MediaEngine.Intelligence/MediaEngine.Intelligence.csproj",
            "src/MediaEngine.Processors/MediaEngine.Processors.csproj",
            "src/MediaEngine.Providers/MediaEngine.Providers.csproj",
            "src/MediaEngine.Ingestion/MediaEngine.Ingestion.csproj",
            "src/MediaEngine.Identity/MediaEngine.Identity.csproj",
            "src/MediaEngine.Admin/MediaEngine.Admin.csproj",
            "src/MediaEngine.AI/MediaEngine.AI.csproj",
            "src/MediaEngine.Plugins/MediaEngine.Plugins.csproj",
            "src/MediaEngine.Plugin.CommercialSkip/MediaEngine.Plugin.CommercialSkip.csproj",
            "src/MediaEngine.Plugin.FandomLore/MediaEngine.Plugin.FandomLore.csproj",
            "src/MediaEngine.Plugin.MediaSegments/MediaEngine.Plugin.MediaSegments.csproj",
            "src/MediaEngine.Api/MediaEngine.Api.csproj",
            "src/MediaEngine.Web/MediaEngine.Web.csproj",
        ];

        foreach (var project in requiredProjects)
        {
            var copyIndex = dockerfile.IndexOf(project, StringComparison.Ordinal);
            Assert.True(copyIndex >= 0, $"Dockerfile does not copy {project}.");
            Assert.True(copyIndex < restoreIndex, $"{project} must be copied before restore.");
        }

        Assert.Contains("global.json", dockerfile);
        Assert.Contains("nuget.config", dockerfile);
    }

    [Fact]
    public void Entrypoint_UsesReadinessLoopInsteadOfFixedSleep()
    {
        var repoRoot = FindRepoRoot();
        var entrypoint = File.ReadAllText(Path.Combine(repoRoot, "docker-entrypoint.sh"));

        Assert.DoesNotContain("sleep 3", entrypoint);
        Assert.Contains("Waiting for Engine liveness", entrypoint);
        Assert.Contains("http://127.0.0.1:61495/health/live", entrypoint);
        Assert.Contains("Engine exited before becoming live", entrypoint);
        Assert.Contains("ASPNETCORE_URLS=\"http://127.0.0.1:61495\"", entrypoint);
        Assert.DoesNotContain("ASPNETCORE_URLS=\"http://+:61495\"", entrypoint);
    }

    [Fact]
    public void TailscalePresetUsesOfficialSidecarPrivateServeAndDeploymentSecret()
    {
        var repoRoot = FindRepoRoot();
        var overlay = File.ReadAllText(Path.Combine(repoRoot, "deploy", "tailscale", "docker-compose.tailscale.yml"));
        var serve = File.ReadAllText(Path.Combine(repoRoot, "deploy", "tailscale", "config", "serve.json"));
        var daemon = File.ReadAllText(Path.Combine(repoRoot, "deploy", "tailscale", "config", "tailscaled.hujson"));

        Assert.Contains("tailscale/tailscale:stable", overlay);
        Assert.Contains("network_mode: service:tuvima", overlay);
        Assert.Contains("TS_SERVE_CONFIG", overlay);
        Assert.Contains("tailscale_auth_key", overlay);
        Assert.Contains("file:/run/secrets/tailscale_auth_key", daemon);
        Assert.Contains("http://127.0.0.1:5016", serve);
        Assert.Contains("\"AllowFunnel\"", serve);
        Assert.Contains("false", serve);
        Assert.DoesNotContain("61495", serve);
    }

    [Fact]
    public void ContainerFirstRun_SeedsPlatformSpecificLibraryPaths()
    {
        var repoRoot = FindRepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(repoRoot, "Dockerfile"));
        var entrypoint = File.ReadAllText(Path.Combine(repoRoot, "docker-entrypoint.sh"));
        var core = File.ReadAllText(Path.Combine(repoRoot, "docker", "config", "core.json"));
        var libraries = File.ReadAllText(Path.Combine(repoRoot, "docker", "config", "libraries.json"));

        Assert.Contains("COPY --from=build /src/config/ ./default-config/", dockerfile);
        Assert.Contains("COPY docker/config/ ./docker-config/", dockerfile);
        Assert.Contains("cp -a /app/default-config/. /config/", entrypoint);
        Assert.Contains("cp -a /app/docker-config/. /config/", entrypoint);
        Assert.DoesNotContain("TUVIMA_WATCH_FOLDER", entrypoint);
        Assert.Contains("\"library_root\": \"/library\"", core);
        Assert.Contains("\"data_root\": \"/artwork-cache\"", core);
        Assert.Contains("\"schema_version\": \"6.0\"", libraries);
        Assert.DoesNotContain("\"path\": \"/watch\"", libraries);
        Assert.Contains("\"path\": \"/library/Books\"", libraries);
        Assert.Contains("\"view_storage\"", libraries);
        Assert.DoesNotContain("\"kind\": \"photos\"", libraries);
        Assert.DoesNotContain("\"kind\": \"personal\"", libraries);
        Assert.DoesNotContain(@"C:\temp", core);
        Assert.DoesNotContain(@"C:\temp", libraries);
    }

    [Fact]
    public void DockerBuildContext_IncludesCopySourcesButExcludesPrivateConfiguration()
    {
        var repoRoot = FindRepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(repoRoot, "Dockerfile"));
        var dockerignore = File.ReadAllText(Path.Combine(repoRoot, ".dockerignore"));

        foreach (var rawLine in File.ReadLines(Path.Combine(repoRoot, "Dockerfile")))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("COPY ", StringComparison.Ordinal) || line.Contains("--from=", StringComparison.Ordinal))
            {
                continue;
            }

            var tokens = System.Text.RegularExpressions.Regex.Matches(line, "\\\"[^\\\"]+\\\"|\\S+")
                .Select(match => match.Value.Trim('"'))
                .ToArray();
            foreach (var source in tokens.Skip(1).SkipLast(1).Where(token => !token.StartsWith("--", StringComparison.Ordinal)))
            {
                var fullPath = Path.Combine(repoRoot, source.TrimEnd('/', '\\'));
                Assert.True(Path.Exists(fullPath), $"Dockerfile COPY source does not exist: {source}");
            }
        }

        Assert.Contains("COPY config/ config/", dockerfile);
        Assert.DoesNotContain("\nconfig/\n", dockerignore.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("config/secrets/", dockerignore);
        Assert.Contains("config/backups/", dockerignore);
        Assert.Contains("config/**/*.bak", dockerignore);
    }

    [Fact]
    public void RuntimeImage_ProvidesMediaToolsPersistentStateAndNonRootApplications()
    {
        var repoRoot = FindRepoRoot();
        var dockerfile = File.ReadAllText(Path.Combine(repoRoot, "Dockerfile"));
        var entrypoint = File.ReadAllText(Path.Combine(repoRoot, "docker-entrypoint.sh"));
        var compose = File.ReadAllText(Path.Combine(repoRoot, "docker-compose.yml"));
        var transcoding = File.ReadAllText(Path.Combine(repoRoot, "docker", "config", "transcoding.json"));

        Assert.Contains("ffmpeg", dockerfile);
        Assert.Contains("ffprobe -version", dockerfile);
        Assert.Contains("target_rid=\"linux-x64\"", dockerfile);
        Assert.Contains("target_rid=\"linux-arm64\"", dockerfile);
        Assert.Contains("TuvimaContainerBuild=true", dockerfile);
        Assert.Contains("libfontconfig1", dockerfile);
        Assert.Contains("libgomp1", dockerfile);
        Assert.Contains("\"ffmpeg_binary_path\": \"/usr/bin/ffmpeg\"", transcoding);
        Assert.Contains("\"ffprobe_binary_path\": \"/usr/bin/ffprobe\"", transcoding);
        Assert.Contains("EXPOSE 5016", dockerfile);
        Assert.DoesNotContain("EXPOSE 61495", dockerfile);
        Assert.DoesNotContain("61495:61495", compose);
        foreach (var path in new[] { "/config", "/db", "/models", "/artwork-cache", "/backups", "/transcode" })
        {
            Assert.Contains(path, dockerfile);
            Assert.Contains(path, compose);
        }

        Assert.Contains("TUVIMA_UID", entrypoint);
        Assert.Contains("TUVIMA_GID", entrypoint);
        Assert.Contains("TUVIMA_UMASK", entrypoint);
        Assert.Contains("exec gosu", entrypoint);
        Assert.Contains("Application startup refused to continue as root", entrypoint);
        Assert.Contains("dotnet publish src/MediaEngine.Admin/MediaEngine.Admin.csproj", dockerfile);
        Assert.Contains("COPY --from=build /app/admin", dockerfile);
        Assert.Contains("HEALTHCHECK", dockerfile);
        Assert.Contains("61495/health/live", dockerfile);
        Assert.Contains("5016/health/live", dockerfile);
        Assert.False(File.Exists(Path.Combine(repoRoot, "docker", "entrypoint.sh")));
    }

    [Fact]
    public void PublishWorkflow_BuildsAndSmokesAmd64AndArm64()
    {
        var repoRoot = FindRepoRoot();
        var workflow = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "docker-publish.yml"));
        var smoke = File.ReadAllText(Path.Combine(repoRoot, "tests", "container", "smoke.sh"));

        Assert.Contains("docker/setup-qemu-action@v3", workflow);
        Assert.Contains("docker/setup-buildx-action@v3", workflow);
        Assert.Contains("linux/amd64", workflow);
        Assert.Contains("linux/arm64", workflow);
        Assert.Contains("tests/container/smoke.sh", workflow);
        Assert.Contains("Container Audio", smoke);
        Assert.Contains("Container Video", smoke);
        Assert.Contains("docker restart", smoke);
        Assert.Contains("ffmpegAvailable", smoke);
        Assert.Contains("llama_cpu", smoke);
    }

    [Fact]
    public void DeploymentDefinitions_KeepDashboardOnlyAndExposeTheSamePersistentContract()
    {
        var repoRoot = FindRepoRoot();
        var compose = File.ReadAllText(Path.Combine(repoRoot, "docker-compose.yml"));
        var unraid = File.ReadAllText(Path.Combine(repoRoot, "unraid-template.xml"));

        Assert.Contains("TZ: \"Etc/UTC\"", compose);
        Assert.Contains("healthcheck:", compose);
        Assert.DoesNotContain("61495:61495", compose);
        Assert.DoesNotContain("Name=\"Engine API Port\"", unraid);
        Assert.Contains("Target=\"5016\"", unraid);

        foreach (var path in new[] { "/config", "/db", "/models", "/artwork-cache", "/backups", "/transcode" })
        {
            Assert.Contains(path, compose);
            Assert.Contains($"Target=\"{path}\"", unraid);
        }

        foreach (var variable in new[] { "TUVIMA_UID", "TUVIMA_GID", "TUVIMA_UMASK", "TZ", "TUVIMA_CONTAINER_NETWORK_MODE" })
        {
            Assert.Contains(variable, compose);
            Assert.Contains($"Target=\"{variable}\"", unraid);
        }
    }

    [Fact]
    public void PublishWorkflow_EmitsImmutableTagsSbomSignaturesAndProvenance()
    {
        var repoRoot = FindRepoRoot();
        var workflow = File.ReadAllText(Path.Combine(repoRoot, ".github", "workflows", "docker-publish.yml"));

        Assert.Contains("type=sha,format=long", workflow);
        Assert.Contains("sbom: true", workflow);
        Assert.Contains("provenance: mode=max", workflow);
        Assert.Contains("sigstore/cosign-installer", workflow);
        Assert.Contains("cosign sign --yes", workflow);
        Assert.Contains("actions/attest-build-provenance", workflow);
        Assert.Contains("attestations: write", workflow);
        Assert.Contains("id-token: write", workflow);
    }

    [Fact]
    public void ContainerDefaults_FormAValidSecretFreeConfigurationDirectory()
    {
        var repoRoot = FindRepoRoot();
        var sourceConfig = Path.Combine(repoRoot, "config");
        var dockerConfig = Path.Combine(repoRoot, "docker", "config");
        var temporaryConfig = Path.Combine(Path.GetTempPath(), $"tuvima-container-config-{Guid.NewGuid():N}");
        try
        {
            CopyDistributableFiles(sourceConfig, temporaryConfig);
            CopyDistributableFiles(dockerConfig, temporaryConfig);

            // The seed is intentionally Linux-specific. Translate only its
            // absolute roots when this schema guard runs on a Windows host.
            if (OperatingSystem.IsWindows())
            {
                var librariesPath = Path.Combine(temporaryConfig, "libraries.json");
                var json = File.ReadAllText(librariesPath);
                var windowsLibrary = Path.Combine(temporaryConfig, "library").Replace("\\", "\\\\");
                File.WriteAllText(
                    librariesPath,
                    json.Replace("/library", windowsLibrary, StringComparison.Ordinal));
            }

            var loader = new MediaEngine.Storage.ConfigurationDirectoryLoader(temporaryConfig);
            var core = loader.LoadCore();
            var libraries = loader.LoadLibraries();
            var transcoding = loader.LoadTranscoding();

            Assert.Equal("/artwork-cache", core.DataRoot);
            Assert.All(libraries.Libraries, library => Assert.Equal("catalogued", library.Kind));
            Assert.Equal("media", libraries.ViewStorage.StorageLocationId);
            Assert.Equal("/usr/bin/ffmpeg", transcoding.FfmpegBinaryPath);
            Assert.Equal("/usr/bin/ffprobe", transcoding.FfprobeBinaryPath);
            var secretsDirectory = Path.Combine(temporaryConfig, "secrets");
            Assert.True(Directory.Exists(secretsDirectory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(secretsDirectory));
            Assert.False(Directory.Exists(Path.Combine(temporaryConfig, "backups")));
            Assert.Empty(Directory.EnumerateFiles(temporaryConfig, "*.bak", SearchOption.AllDirectories));
        }
        finally
        {
            if (Directory.Exists(temporaryConfig))
            {
                Directory.Delete(temporaryConfig, recursive: true);
            }
        }
    }

    [Fact]
    public void NuGetConfig_UsesPortablePublicPackageSource()
    {
        var repoRoot = FindRepoRoot();
        var nugetConfig = File.ReadAllText(Path.Combine(repoRoot, "nuget.config"));

        Assert.Contains("https://api.nuget.org/v3/index.json", nugetConfig);
        Assert.DoesNotContain("tuvima-wikidata-local", nugetConfig);
        Assert.DoesNotContain(@"C:\Users\", nugetConfig);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }

    private static void CopyDistributableFiles(string sourceRoot, string destinationRoot)
    {
        foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceRoot, source).Replace('\\', '/');
            if (relative.StartsWith("secrets/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("backups/", StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                || relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relative, "ui/library-preferences.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var destination = Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
    }
}
