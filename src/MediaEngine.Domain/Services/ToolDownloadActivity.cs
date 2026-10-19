using System.Text.Json;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Services;

/// <summary>
/// Builds the system activity entries for tool and model downloads (started, finished, failed, checksum refused,
/// cancelled), so FFmpeg, Tesseract and the local AI models are recorded the same way. An entry names the tool, its
/// version, the host it came from and a plain reason; it never carries library data, file paths or error text.
/// </summary>
public static class ToolDownloadActivity
{
    public static SystemActivityEntry Started(string tool, string? version, string? host) =>
        Build(SystemActionType.ToolDownloadStarted, tool, version, host, null,
            $"Started downloading {Describe(tool, version)}{From(host)}.");

    public static SystemActivityEntry Finished(string tool, string? version, string? host) =>
        Build(SystemActionType.ToolDownloadFinished, tool, version, host, null,
            $"Finished downloading {Describe(tool, version)}{From(host)}.");

    public static SystemActivityEntry Failed(string tool, string? version, string? host, string reason) =>
        Build(SystemActionType.ToolDownloadFailed, tool, version, host, reason,
            $"Could not download {Describe(tool, version)}{From(host)}. {reason}");

    public static SystemActivityEntry ChecksumRefused(string tool, string? version, string? host) =>
        Build(SystemActionType.ToolDownloadChecksumRefused, tool, version, host,
            "The download did not match its recorded checksum.",
            $"Refused {Describe(tool, version)}{From(host)}: the download did not match its recorded checksum, so it was thrown away.");

    public static SystemActivityEntry Cancelled(string tool, string? version, string? host) =>
        Build(SystemActionType.ToolDownloadCancelled, tool, version, host, null,
            $"Cancelled the download of {Describe(tool, version)}{From(host)}.");

    private static SystemActivityEntry Build(
        string actionType, string tool, string? version, string? host, string? reason, string detail) =>
        new()
        {
            ActionType = actionType,
            EntityType = "Tool",
            Detail = detail,
            ChangesJson = JsonSerializer.Serialize(new
            {
                tool,
                version,
                source_host = host,
                reason,
            }),
        };

    private static string Describe(string tool, string? version) =>
        string.IsNullOrWhiteSpace(version) ? tool : $"{tool} {version}";

    private static string From(string? host) =>
        string.IsNullOrWhiteSpace(host) ? string.Empty : $" from {host}";
}
