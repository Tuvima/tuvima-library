using System.Reflection;
using System.Text.RegularExpressions;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

public sealed partial class ComponentParameterGuardrailTests
{
    private static readonly Dictionary<string, Type[]> Components = typeof(AppSkeleton).Assembly.GetTypes()
        .Where(type => typeof(IComponent).IsAssignableFrom(type) && !type.IsAbstract)
        .GroupBy(type => type.Name.Split('`')[0])
        .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

    private static readonly HashSet<string> HtmlAttributes = new((
        "id class style title role tabindex hidden inert lang dir slot accesskey draggable spellcheck translate autofocus " +
        "href target rel download type name value disabled readonly required placeholder autocomplete inputmode " +
        "min max step minlength maxlength pattern multiple checked selected accept capture for form method action " +
        "src srcset sizes alt width height loading decoding fetchpriority colspan rowspan scope headers datetime " +
        "popover popovertarget popovertargetaction contenteditable enterkeyhint wrap rows cols list").Split(' '),
        StringComparer.Ordinal);

    [Fact]
    public void FirstPartyComponentAttributesMatchTheirDirectParameterContracts()
    {
        var root = FindRepoRoot();
        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "MediaEngine.Web"), "*.razor", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            foreach (var tag in ReadTags(File.ReadAllText(file)))
            {
                if (!Components.TryGetValue(tag.Name, out var types))
                {
                    continue;
                }

                var component = types.Length == 1 ? types[0] : ResolveAmbiguous(file, tag.Name, types);
                foreach (var attribute in tag.Attributes)
                {
                    if (!Accepts(component, attribute))
                    {
                        failures.Add($"{Path.GetRelativePath(root, file)}:{tag.Line}: {tag.Name}.{attribute}");
                    }
                }
            }
        }
        Assert.True(failures.Count == 0, "Invalid component attributes:\n" + string.Join('\n', failures));
    }

    private static Type ResolveAmbiguous(string file, string name, Type[] types)
    {
        // The two OverviewTab components are explicitly qualified by their owning area.
        Assert.Equal("OverviewTab", name);
        var area = file.Contains($"{Path.DirectorySeparatorChar}Settings{Path.DirectorySeparatorChar}")
            || Path.GetFileName(file) == "Settings.razor" ? "Settings" : "Details";
        return Assert.Single(types, type => type.Namespace == $"MediaEngine.Web.Components.{area}");
    }

    [Theory]
    [InlineData("Animation")]
    [InlineData("SkeletonType")]
    [InlineData("Heigth")]
    [InlineData("made-up-html")]
    public void AttributeCaptureDoesNotHideRetiredOrMisspelledSkeletonParameters(string attribute) =>
        Assert.False(Accepts(typeof(AppSkeleton), attribute));

    [Fact]
    public void CascadingValuesAreNotDirectParameters() => Assert.False(Accepts(typeof(AppDialog), "Context"));

    [Fact]
    public void AttributeNamesExcludeNestedRazorExpressionText()
    {
        var tags = ReadTags("<AppButton Label=\"@($\"Edit {GetLabel(\"title\")}\")\" OnClick=\"@(() => Run(\"x > y\"))\" data-test=\"ok\" />").ToArray();
        Assert.Single(tags);
        Assert.Equal(new[] { "Label", "OnClick", "data-test" }, tags[0].Attributes);
    }

    private static bool Accepts(Type type, string attribute)
    {
        if (attribute.StartsWith("@bind-", StringComparison.Ordinal))
        {
            var bound = attribute[6..].Split(':')[0];
            return HasParameter(type, bound) && HasParameter(type, bound + "Changed");
        }
        if (attribute.StartsWith('@'))
        {
            return true;
        }

        if (HasParameter(type, attribute) || type.GetGenericArguments().Any(argument => argument.Name == attribute))
        {
            return true;
        }

        var captures = type.GetProperties().Any(property => property.GetCustomAttribute<ParameterAttribute>()?.CaptureUnmatchedValues == true);
        return captures && (attribute.StartsWith("data-", StringComparison.Ordinal) || attribute.StartsWith("aria-", StringComparison.Ordinal)
            || HtmlAttributes.Contains(attribute));
    }

    private static bool HasParameter(Type type, string name) => type.GetProperties().Any(property =>
        property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
        && property.GetCustomAttribute<ParameterAttribute>() is not null
        && property.GetCustomAttribute<CascadingParameterAttribute>() is null);

    // Read only opening tags and attribute names. Quoted Razor expressions are balanced
    // independently, so C# string literals and comparison operators are never attributes.
    private static IEnumerable<Tag> ReadTags(string source)
    {
        source = Comments().Replace(source, match => new string(' ', match.Length));
        foreach (Match match in OpeningTags().Matches(source))
        {
            var index = match.Index + match.Length;
            var attributes = new List<string>();
            while (index < source.Length)
            {
                while (index < source.Length && char.IsWhiteSpace(source[index]))
                {
                    index++;
                }

                if (index == source.Length || source[index] is '>' or '/')
                {
                    break;
                }

                var start = index;
                while (index < source.Length && (char.IsLetterOrDigit(source[index]) || source[index] is '@' or '-' or ':' or '_'))
                {
                    index++;
                }

                if (index == start) { index++; continue; }
                attributes.Add(source[start..index]);
                while (index < source.Length && char.IsWhiteSpace(source[index]))
                {
                    index++;
                }

                if (index == source.Length || source[index] != '=')
                {
                    continue;
                }

                index++;
                while (index < source.Length && char.IsWhiteSpace(source[index]))
                {
                    index++;
                }

                if (index == source.Length)
                {
                    break;
                }

                if (source[index] is '\'' or '"')
                {
                    var quote = source[index++];
                    while (index < source.Length && source[index] != quote)
                    {
                        if (source[index] is '(' or '[')
                        {
                            SkipExpression(source, ref index);
                        }
                        else
                        {
                            index++;
                        }
                    }
                    if (index < source.Length)
                    {
                        index++;
                    }
                }
                else
                {
                    while (index < source.Length && !char.IsWhiteSpace(source[index]) && source[index] != '>')
                    {
                        index++;
                    }
                }
            }
            yield return new Tag(match.Groups[1].Value, attributes, source[..match.Index].Count(character => character == '\n') + 1);
        }
    }

    private static void SkipExpression(string source, ref int index)
    {
        var opening = source[index++];
        var closing = opening == '(' ? ')' : ']';
        var depth = 1;
        while (index < source.Length && depth > 0)
        {
            var character = source[index++];
            if (character is '\'' or '"')
            {
                while (index < source.Length)
                {
                    var next = source[index++];
                    if (next == '\\' && index < source.Length)
                    {
                        index++;
                    }
                    else if (next == character)
                    {
                        break;
                    }
                }
            }
            else if (character == opening)
            {
                depth++;
            }
            else if (character == closing)
            {
                depth--;
            }
        }
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed record Tag(string Name, IReadOnlyList<string> Attributes, int Line);
    [GeneratedRegex(@"<([A-Z][\w]*)(?=[\s/>])")]
    private static partial Regex OpeningTags();
    [GeneratedRegex(@"@\*.*?\*@|<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();
}
