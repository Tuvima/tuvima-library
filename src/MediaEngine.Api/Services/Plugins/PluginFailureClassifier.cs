using System.ComponentModel;
using System.Net;
using MediaEngine.Domain.Plugins;
using MediaEngine.Plugins;

namespace MediaEngine.Api.Services.Plugins;

/// <summary>
/// Puts an exception raised while a plugin or source was working into the fixed list of causes (Plugin architecture spec C17), so a
/// broken plugin can be told apart from the outside service, a missing tool, the file or the Library itself. Anything not recognised
/// is the plugin's own crash.
/// </summary>
public static class PluginFailureClassifier
{
    private const int DiskFullHResult = unchecked((int)0x80070070);
    private const int DiskFullLinuxErrno = 28;

    public static PluginFailureReason Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        switch (exception)
        {
            case HttpRequestException { StatusCode: { } status }:
                return status switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => PluginFailureReason.KeyRejected,
                    HttpStatusCode.TooManyRequests => PluginFailureReason.ServiceRateLimited,
                    HttpStatusCode.RequestTimeout or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => PluginFailureReason.ServiceUnreachable,
                    _ => PluginFailureReason.ServiceRefused,
                };
            case HttpRequestException:
            case TaskCanceledException:
            case TimeoutException:
                return PluginFailureReason.ServiceUnreachable;
            case PluginLimitExceededException:
                return PluginFailureReason.LimitRequests;
            case Win32Exception or FileNotFoundException when IsAboutATool(exception):
                return PluginFailureReason.ToolMissing;
            case FileNotFoundException or DirectoryNotFoundException or EndOfStreamException:
                return PluginFailureReason.ItemUnreadable;
            case NotSupportedException or InvalidDataException:
                return PluginFailureReason.ItemUnsupported;
            case IOException io when io.HResult == DiskFullHResult || io.HResult == DiskFullLinuxErrno || io.Message.Contains("No space left", StringComparison.OrdinalIgnoreCase):
                return PluginFailureReason.LibraryDisk;
            case IOException:
                return PluginFailureReason.ItemUnreadable;
            case UnauthorizedAccessException:
                return PluginFailureReason.NotAllowedPermission;
            case Microsoft.Data.Sqlite.SqliteException:
                return PluginFailureReason.LibraryDataStore;
            default:
                return PluginFailureReason.PluginCrashed;
        }
    }

    // A program that could not be started is a missing tool; a media file that could not be found is the item.
    private static bool IsAboutATool(Exception exception) =>
        exception is Win32Exception
        || (exception is FileNotFoundException { FileName: { } name } && (name.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase)
            || name.Contains("comskip", StringComparison.OrdinalIgnoreCase)
            || name.Contains("tesseract", StringComparison.OrdinalIgnoreCase)));
}
