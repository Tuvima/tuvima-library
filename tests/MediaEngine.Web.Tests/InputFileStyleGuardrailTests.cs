using System.Text.RegularExpressions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A file input is the root element of the framework's InputFile component, so a component's own scoped style sheet never reaches it
/// and the native "Choose Files" box shows. Every class given to an InputFile must be styled by a global style sheet (wwwroot) or a ::deep rule.
/// </summary>
public sealed class InputFileStyleGuardrailTests
{
    [Fact]
    public void EveryInputFileClass_IsStyledGloballyOrDeep()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "MediaEngine.slnx")))
        {
            root = Path.GetDirectoryName(root);
        }

        Assert.NotNull(root);
        var web = Path.Combine(root!, "src", "MediaEngine.Web");
        var global = string.Join('\n', Directory.EnumerateFiles(Path.Combine(web, "wwwroot"), "*.css", SearchOption.AllDirectories).Select(File.ReadAllText));
        var deep = string.Join('\n', Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor.css", SearchOption.AllDirectories).Select(File.ReadAllText));
        var missing = new List<string>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(web, "Components"), "*.razor", SearchOption.AllDirectories))
        {
            foreach (Match tag in Regex.Matches(File.ReadAllText(path), @"<InputFile\b[^>]*>"))
            {
                var cls = Regex.Match(tag.Value, "class=\"([a-z0-9_-]+)");
                if (!cls.Success || tag.Value.Contains("hidden", StringComparison.Ordinal))
                {
                    continue;
                }

                var name = cls.Groups[1].Value;
                if (!global.Contains("." + name, StringComparison.Ordinal) && !deep.Contains("::deep ." + name, StringComparison.Ordinal))
                {
                    missing.Add(Path.GetFileName(path) + " -> ." + name);
                }
            }
        }

        Assert.True(missing.Count == 0, "Style these file inputs in a global style sheet or with ::deep: " + string.Join(", ", missing));
    }
}
