using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Wording bugs and duplicate facts in Settings (settings-alignment D10 to D12 and D15): no screen says an account without a password
/// "works only on this computer" (it works on the whole home network), the numeric address and QR code live on General only,
/// the "Remote access is off" notice belongs to Home network only, and the Computer capacity card keeps its words on readable lines.
/// </summary>
public sealed partial class SettingsWordingAndDuplicateFactsGuardrailTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string WebRoot = Path.Combine(RepoRoot, "src", "MediaEngine.Web");
    private static readonly string SettingsRoot = Path.Combine(WebRoot, "Components", "Settings");

    [GeneratedRegex(@"works\s+only\s+on\s+this\s+computer|only\s+works\s+on\s+this\s+computer|only\s+this\s+computer\s+is\s+(the\s+)?local", RegexOptions.IgnoreCase)]
    private static partial Regex BannedLocalWording();

    private static IEnumerable<string> SettingsSources() =>
        Directory.EnumerateFiles(SettingsRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Order(StringComparer.Ordinal);

    private static string Read(params string[] relativeToWeb) => File.ReadAllText(Path.Combine([WebRoot, .. relativeToWeb]));

    [Fact]
    public void NoSettingsScreenOrString_SaysAnAccountWorksOnlyOnThisComputer()
    {
        var found = new List<string>();
        foreach (var path in SettingsSources())
        {
            foreach (Match match in BannedLocalWording().Matches(File.ReadAllText(path)))
            {
                found.Add($"{Path.GetRelativePath(RepoRoot, path)}: \"{match.Value}\"");
            }
        }

        var resources = XDocument.Load(Path.Combine(WebRoot, "Resources", "SharedStrings.resx"));
        foreach (var data in resources.Root!.Elements("data"))
        {
            var value = data.Element("value")?.Value ?? string.Empty;
            if (BannedLocalWording().IsMatch(value))
            {
                found.Add($"SharedStrings.resx {data.Attribute("name")?.Value}: \"{value}\"");
            }
        }

        Assert.True(found.Count == 0,
            "An account without a password works on the whole home network and needs one only for outside access; say that instead:\n"
            + string.Join('\n', found));
    }

    [Fact]
    public void TheNumericAddressAndTheQrCode_AreDrawnOnGeneralOnly()
    {
        var offenders = SettingsSources()
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}General{Path.DirectorySeparatorChar}"))
            // Three QR codes that are not the server address: an invitation link, the authenticator-app set-up code, and the
            // Tailscale download page that the away-from-home flow shows so a phone can get the app (spec remote-access WP4), and the On
            // panel's QR of the away-from-home address (not the home address, which stays on General) so a phone off the home network can
            // try it (spec remote-access D6 and WP6).
            .Where(path => Path.GetFileName(path) is not ("InvitationShare.razor" or "SecuritySettingsPage.TwoStep.cs" or "RemoteAccessTailscaleSteps.razor" or "RemoteAccessOnPanel.razor"))
            .Where(path =>
            {
                var text = File.ReadAllText(path);
                return text.Contains("QrCodeSvg", StringComparison.Ordinal)
                    || text.Contains("data-qr-payload", StringComparison.Ordinal)
                    || text.Contains("GetNetworkAppAddressesAsync", StringComparison.Ordinal);
            })
            .Select(path => Path.GetRelativePath(RepoRoot, path))
            .ToList();

        Assert.True(offenders.Count == 0, "The address and its QR code are edited and shown only on General:\n" + string.Join('\n', offenders));
        Assert.Contains("data-qr-payload", Read("Components", "Settings", "General", "GeneralNameAddressPanel.razor"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("LocalNetworkSettingsPanel.razor")]
    [InlineData("SystemSummaryPage.razor")]
    public void HomeNetworkAndSystemHealth_ShowTheAddressAsReadOnlyTextWithALinkToGeneral(string file)
    {
        var source = Read("Components", "Settings", file);

        Assert.Contains("href=\"/settings/general\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<AppTextField", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AppCopyButton", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRemoteAccessOffNotice_IsGone_AndNoPageDrawsIt()
    {
        // Spec remote-access D7: home working is the normal state, so no permanent "Remote access is off" bubble; the Remote access
        // page's start page says where away-from-home access stands.
        var drawn = Directory.EnumerateFiles(Path.Combine(WebRoot, "Components"), "*.razor", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(path => File.ReadAllText(path).Contains("RemoteAccessOffNotice", StringComparison.Ordinal))
            .Select(path => Path.GetFileName(path))
            .ToList();
        Assert.Empty(drawn);
        Assert.False(File.Exists(Path.Combine(WebRoot, "Components", "Settings", "Frame", "RemoteAccessOffNotice.razor")));
    }

    [Fact]
    public void TheComputerCapacityCard_KeepsItsWordsAtLeastTwentyCharactersWide_AndTheNumbersOnTheirOwnRow()
    {
        var css = Read("Components", "Settings", "ComputerCapacityPanel.razor.css");

        // The words column can never be squeezed narrower than 20 characters by the numbers or the button beside it.
        Assert.Matches(@"\.computer-capacity\s*\{[^}]*grid-template-columns:\s*auto\s+minmax\(20ch,\s*1fr\)\s+auto;", css);
        Assert.Matches(@"\.computer-capacity__stats\s*\{[^}]*grid-column:\s*1\s*/\s*-1;[^}]*grid-row:\s*2;", css);
        Assert.Matches(@"\.computer-capacity__words\s*\{[^}]*grid-column:\s*2;", css);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
