using System.Text.RegularExpressions;
using MediaEngine.Domain.Configuration;

namespace MediaEngine.Ingestion.Videos;

/// <summary>
/// Decides whether a folder looks like phone or camera footage (a DCIM folder, or mostly camera-style names such as
/// IMG_1234 or MOV_0042), so adding it can gently suggest View. It only suggests; the person always chooses.
/// </summary>
public static class VideoPhoneFootageDetector
{
    private const int MaxFilesLooked = 500;

    public static bool Looks(string folder, VideoPhoneFootageConfiguration rules)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (!Directory.Exists(root))
            {
                return false;
            }

            var folderNames = new HashSet<string>(rules.FolderNames, StringComparer.OrdinalIgnoreCase);
            var patterns = rules.NamePatterns
                .Select(pattern => new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)))
                .ToList();

            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint };
            var videos = 0;
            var cameraStyle = 0;
            var inCameraFolder = false;
            foreach (var file in Directory.EnumerateFiles(root, "*", options).Where(VideoFolderLayout.IsVideoFile).Take(MaxFilesLooked))
            {
                videos++;
                var parent = Path.GetDirectoryName(file);
                while (parent is not null && parent.Length >= root.Length)
                {
                    if (folderNames.Contains(Path.GetFileName(parent)))
                    {
                        inCameraFolder = true;
                    }

                    parent = Path.GetDirectoryName(parent);
                }

                var stem = Path.GetFileNameWithoutExtension(file);
                if (patterns.Any(pattern => IsMatch(pattern, stem)))
                {
                    cameraStyle++;
                }
            }

            return videos > 0 && (inCameraFolder || cameraStyle >= Math.Ceiling(videos * rules.MinShare));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or ArgumentException)
        {
            // A folder that cannot be read simply gets no hint; the choice is still the person's.
            System.Diagnostics.Trace.TraceWarning($"Phone footage check skipped: {exception.Message}");
            return false;
        }
    }

    private static bool IsMatch(Regex pattern, string text)
    {
        try
        {
            return pattern.IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
