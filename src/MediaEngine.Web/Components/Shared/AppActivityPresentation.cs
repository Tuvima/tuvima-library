namespace MediaEngine.Web.Components.Shared;

public readonly record struct AppActivityDescriptor(string Label, string IconKey, string AccentColor);

/// <summary>Shared semantic treatment for system activity wherever it is summarized.</summary>
public static class AppActivityPresentation
{
    public static AppActivityDescriptor For(string? actionType, string? entityType = null)
    {
        var value = $"{actionType} {entityType}".ToLowerInvariant();

        if (ContainsAny(value, "clean", "purge", "delete", "remove"))
        {
            return new("Cleanup", AppIcons.Cleanup, "var(--tl-status-success)");
        }
        if (ContainsAny(value, "metadata", "reconcil", "canonical", "enrich", "hydrat", "provider", "artwork"))
        {
            return new("Metadata", AppIcons.Metadata, "var(--tl-status-info)");
        }
        if (ContainsAny(value, "maintenance", "prun", "vacuum", "repair"))
        {
            return new("Maintenance", AppIcons.Maintenance, "var(--tl-accent-primary)");
        }
        if (ContainsAny(value, "backup", "restore", "recovery"))
        {
            return new("Backup", AppIcons.Backup, "var(--tl-media-audiobooks)");
        }
        if (ContainsAny(value, "review", "provisional", "reject", "warning", "fail", "error"))
        {
            return new("Review", AppIcons.Review, "var(--tl-status-warning)");
        }
        if (ContainsAny(value, "ingest", "import", "scan", "mediaadded", "file"))
        {
            return new("Ingestion", AppIcons.Ingestion, "var(--tl-media-books)");
        }
        if (ContainsAny(value, "server", "startup", "started", "system"))
        {
            return new("System", AppIcons.System, "var(--tl-status-success)");
        }

        return new("Library", AppIcons.Activity, "var(--tl-text-secondary)");
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(value.Contains);
}
