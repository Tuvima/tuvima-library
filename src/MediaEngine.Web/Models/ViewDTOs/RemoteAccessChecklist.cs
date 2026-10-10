using MediaEngine.Contracts.Settings;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>One plain-English line of the "can this be opened to the internet?" checklist.</summary>
public sealed record RemoteAccessChecklistItem(string Label, bool Passed, string Detail, string FixLabel, string FixHref);

/// <summary>
/// Turns the Engine's raw readiness checks into the short checklist shown next to "Who can connect". Presentation only:
/// the Engine still decides whether Anywhere is allowed.
/// </summary>
public static class RemoteAccessChecklist
{
    public const string UsersHref = "/settings/access/users";
    public const string AccountHref = "/settings/account";
    public const string PublicAddressHref = "#remote-public-address";
    public const string ConnectionHref = "#remote-connection-mode";

    public static IReadOnlyList<RemoteAccessChecklistItem> Build(RemoteAccessReadinessDto readiness)
    {
        var items = new List<RemoteAccessChecklistItem>();
        foreach (var check in readiness.Checks)
        {
            var passed = check.Status == "passed";
            switch (check.Key)
            {
                case "authentication":
                    var noAdministrator = !passed && check.Detail.StartsWith("No administrator", StringComparison.OrdinalIgnoreCase);
                    items.Add(new("An administrator can sign in", !noAdministrator,
                        noAdministrator ? check.Detail : "An administrator can sign in.", "Open Users & Access", UsersHref));
                    items.Add(new("Recovery codes saved", passed,
                        passed ? "Recovery codes are saved." : noAdministrator ? "Add an administrator who can sign in first." : check.Detail,
                        "Open Account", AccountHref));
                    break;
                case "public-address":
                    items.Add(new("Public address set", passed, check.Detail, "Set the address", PublicAddressHref));
                    break;
                case "authentication-bypass":
                    items.Add(new("Sign-in always required", passed, check.Detail, "Open Authentication", "/settings/access/authentication"));
                    break;
                default:
                    // Tailscale, HTTPS reverse proxy, router, TLS terminator and "no secure path chosen" all
                    // answer the same question for the visitor: is the way in from the internet secure?
                    items.Add(new("Secure connection", passed, check.Detail, "Choose a connection", ConnectionHref));
                    break;
            }
        }

        return items;
    }

    public static string WhoCanConnectSummary(string? whoCanConnect) => whoCanConnect switch
    {
        "this_computer" => "This computer only",
        "home_network" => "Home network",
        "anywhere" => "Anywhere",
        _ => "Unavailable",
    };
}
