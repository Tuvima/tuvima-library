using System.Text;

namespace MediaEngine.Contracts.Tests;

/// <summary>
/// Shared approved-fixture comparison for the Contracts snapshot tests.
/// On a mismatch the actual output is written next to the approved file as
/// <c>&lt;name&gt;.received.txt</c> so accepting a deliberate contract change is a
/// plain file copy (received over approved). On a match any stale received file
/// is removed. The older environment-variable update mode still works.
/// </summary>
internal static class SnapshotFixture
{
    public static void Verify(string fileName, string actual, string updateEnvironmentVariable)
    {
        var approvedPath = GetFixturePath(fileName);
        var receivedPath = GetReceivedPath(approvedPath);

        if (string.Equals(
                Environment.GetEnvironmentVariable(updateEnvironmentVariable),
                "1",
                StringComparison.Ordinal))
        {
            File.WriteAllText(approvedPath, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        var expected = File.ReadAllText(approvedPath).ReplaceLineEndings("\n");
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            if (File.Exists(receivedPath))
            {
                File.Delete(receivedPath);
            }

            return;
        }

        File.WriteAllText(receivedPath, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        Assert.Fail(
            $"Snapshot mismatch for '{Path.GetFileName(approvedPath)}'.{Environment.NewLine}" +
            $"  approved: {approvedPath}{Environment.NewLine}" +
            $"  received: {receivedPath}{Environment.NewLine}" +
            "If the change is intended, copy received over approved to accept it " +
            "(or run tools/Update-ContractFixtures.ps1), then review the diff.");
    }

    private static string GetReceivedPath(string approvedPath)
    {
        const string approvedSuffix = ".approved.txt";
        var stem = approvedPath.EndsWith(approvedSuffix, StringComparison.Ordinal)
            ? approvedPath[..^approvedSuffix.Length]
            : approvedPath;
        return stem + ".received.txt";
    }

    private static string GetFixturePath(string fileName) =>
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "Fixtures",
            fileName));
}
