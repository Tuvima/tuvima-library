using MediaEngine.Contracts.System;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace MediaEngine.Api.Services;

public sealed class StartupReadinessService(HealthCheckService healthChecks)
{
    /// <summary>
    /// Runs the readiness checks. With a <paramref name="category"/> (a check tag such as "storage"), only that category's
    /// checks run, so a screen that shows only folder problems never waits for the data store integrity scan.
    /// </summary>
    public async Task<StartupReadinessResponse> GetAsync(string? category = null, CancellationToken ct = default)
    {
        var report = await healthChecks.CheckHealthAsync(
            registration => registration.Tags.Contains("readiness")
                            && (string.IsNullOrWhiteSpace(category) || registration.Tags.Contains(category.Trim().ToLowerInvariant())),
            ct).ConfigureAwait(false);

        var checks = report.Entries
            .OrderBy(entry => Category(entry.Value))
            .ThenBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new StartupReadinessCheckResponse
            {
                Name = entry.Key,
                Category = Category(entry.Value),
                Status = entry.Value.Status switch
                {
                    HealthStatus.Healthy => "ready",
                    HealthStatus.Degraded => "degraded",
                    _ => "not_ready",
                },
                Required = IsRequired(entry.Value),
                Description = entry.Value.Description,
                Data = entry.Value.Data.ToDictionary(
                    item => item.Key,
                    item => item.Value?.ToString() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase),
            })
            .ToList();

        var requiredBlocked = checks.Any(check => check.Required && check.Status == "not_ready");
        var degraded = checks.Any(check => check.Status != "ready");
        return new StartupReadinessResponse
        {
            Status = requiredBlocked ? "not_ready" : degraded ? "degraded" : "ready",
            CheckedAt = DateTimeOffset.UtcNow,
            Checks = checks,
        };
    }

    private static string Category(HealthReportEntry entry) =>
        entry.Data.TryGetValue("category", out var category)
            ? category?.ToString() ?? "system"
            : "system";

    private static bool IsRequired(HealthReportEntry entry) =>
        entry.Data.TryGetValue("required", out var required)
        && required is true;
}
