using System.Net;
using System.Text;
using Bunit;
using MediaEngine.Web.Components.Pages;
using MediaEngine.Web.Services.Integration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class ViewDocumentRulesPageTests
{
    [Fact]
    public void ThePageIsRoutedAtTheDocumentsRulesAddressSoEveryoneWithDocumentsCanReachIt()
    {
        var route = typeof(ViewDocumentRulesPage).GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), false)
            .Cast<Microsoft.AspNetCore.Components.RouteAttribute>().Single();
        Assert.Equal("/view/documents/rules", route.Template);
    }

    [Fact]
    public void TheDocumentsPageLinksToItButTheTelevisionPageDoesNot()
    {
        var root = FindRepoRoot();
        var page = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Pages", "ViewDocumentsPage.razor"));
        Assert.Contains("Href=\"/view/documents/rules\"", page, StringComparison.Ordinal);
        Assert.Contains("@if (!Device.IsTv)", page, StringComparison.Ordinal);
        var rules = File.ReadAllText(Path.Combine(root, "src", "MediaEngine.Web", "Components", "Pages", "ViewDocumentRulesPage.razor"));
        Assert.Contains("Device.IsTv", rules, StringComparison.Ordinal);
        Assert.Contains("ReadOnly=\"@Device.IsPhone\"", rules, StringComparison.Ordinal);
        Assert.Contains("CanViewDocuments == false", rules, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root.");
    }
}
