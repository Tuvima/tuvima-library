using System.Text.Json;
using MediaEngine.Domain.Configuration;
namespace MediaEngine.Providers.Tests;

internal static class RetailMatrixFixture
{
    internal static PipelineConfiguration Load()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "config", "pipelines.json"))) directory = directory.Parent;
        if (directory is null) throw new InvalidOperationException("Repository scoring fixture not found.");
        var entries = JsonSerializer.Deserialize<Dictionary<string,MediaTypePipeline>>(File.ReadAllText(Path.Combine(directory.FullName,"config","pipelines.json")))!;
        return new() { Pipelines = entries };
    }
    internal static PipelineConfiguration WithMatrices(PipelineConfiguration config)
    {
        var current = Load();
        foreach (var (media, pipeline) in config.Pipelines)
            if (pipeline.Scoring.Scopes.Count == 0 && current.Pipelines.TryGetValue(media, out var defaults)) pipeline.Scoring.Scopes = defaults.Scoring.Scopes;
        return config;
    }
}
