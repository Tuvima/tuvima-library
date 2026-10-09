using System.Text.RegularExpressions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// There is no self sign-up: an account row can only be created by an administrator, or by the setup session for the
/// very first administrator. These tests keep it that way by listing every place that can create one.
/// </summary>
public sealed class AccountCreationGuardrailTests
{
    // Every source file that is allowed to construct an account or insert an accounts row, and why.
    private static readonly Dictionary<string, string> AllowedCreators = new(StringComparer.Ordinal)
    {
        ["src/MediaEngine.Identity/FirstPartyIdentityService.cs"] = "first administrator, reached only from the setup session",
        ["src/MediaEngine.Api/Security/AccountAccessMutationService.cs"] = "administrator-created accounts and invitations",
        ["src/MediaEngine.Api/Security/AccountPasskeyStore.cs"] = "identity-store adapter used for accounts that already exist",
        ["src/MediaEngine.Storage/AccountRepository.cs"] = "the data-store implementation behind the above",
        ["src/MediaEngine.Storage/HouseholdRepository.cs"] = "reads existing accounts back; creates none",
        ["src/MediaEngine.Storage/SchemaMigrator.cs"] = "startup data-structure rebuild",
    };

    private static readonly Regex CreatesAccount = new(
        @"new\s+Account\s*(\{|\()|\baccounts?\.InsertAsync\(|INSERT\s+INTO\s+accounts\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void OnlyTheAllowlistedFilesCanCreateAnAccount()
    {
        var root = FindRepoRoot();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            if (CreatesAccount.IsMatch(File.ReadAllText(file)) && !AllowedCreators.ContainsKey(relative))
            {
                offenders.Add(relative);
            }
        }

        Assert.True(offenders.Count == 0,
            "These files can create an account but are not on the reviewed list. A new way to create accounts must "
            + "require an administrator (or the setup session) and be added to AllowedCreators with a reason: "
            + string.Join(", ", offenders));
    }

    [Theory]
    [InlineData("src/MediaEngine.Api/Endpoints/AccountEndpoints.cs", "mutations.CreateAsync(")]
    [InlineData("src/MediaEngine.Api/Endpoints/AccountEndpoints.cs", "mutations.IssueInvitationAsync(")]
    [InlineData("src/MediaEngine.Api/Endpoints/AccountEndpoints.cs", "mutations.SetTemporaryPasswordAsync(")]
    public void AdministratorActionsThatMakeSignInsRequireAnAdministrator(string file, string call)
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), file));
        var index = source.IndexOf(call, StringComparison.Ordinal);
        Assert.True(index >= 0, $"{call} is no longer called in {file}; update this guardrail.");

        // From the call to the end of its route registration (the next Map* call), the route must demand an
        // administrator.
        var rest = source[index..];
        var next = Regex.Match(rest[call.Length..], @"\.Map(Get|Post|Put|Delete|Patch|Group)\(");
        var registration = next.Success ? rest[..(call.Length + next.Index)] : rest;
        Assert.Contains("RequireAdministratorOrApplication(", registration, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstAdministratorIsCreatedOnlyAfterTheSetupSessionIsChecked()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Api/Endpoints/SetupEndpoints.cs"));
        var callers = Regex.Matches(source, @"identity\.Bootstrap\w*AdministratorAsync\(").Count;
        Assert.Equal(2, callers);

        var handler = source[source.IndexOf("\"/administrator\"", StringComparison.Ordinal)..];
        var firstCall = handler.IndexOf("identity.Bootstrap", StringComparison.Ordinal);
        var check = handler.IndexOf("AuthorizedAsync(context, user, sessions", StringComparison.Ordinal);
        Assert.InRange(check, 0, firstCall - 1);
    }

    [Fact]
    public void InvitationAcceptingNeverCreatesAnAccountRow()
    {
        // The account exists before the invitation does; accepting only adds a password to it.
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Identity/FirstPartyIdentityService.cs"));
        var start = source.IndexOf("public async Task<SessionIssueResult> AcceptInvitationAsync", StringComparison.Ordinal);
        var end = source.IndexOf("\n    public ", start + 10, StringComparison.Ordinal);
        var body = source[start..(end > 0 ? end : source.Length)];
        Assert.DoesNotContain("new Account", body, StringComparison.Ordinal);
        Assert.DoesNotContain("InsertAsync(", body, StringComparison.Ordinal);
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
}
