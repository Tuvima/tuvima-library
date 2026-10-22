using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

/// <summary>
/// When the Razor compiler does not recognise an <c>@on&lt;event&gt;</c> directive or one of its modifiers, it emits the
/// text as a literal attribute name starting with "@" (for example <c>@oncancel:preventDefault</c>, which the compiler
/// does not honour even though the framework registers <c>oncancel</c>). The browser rejects that name with
/// InvalidCharacterError and the circuit dies. bUnit does not validate attribute names, so this reads the compiled
/// Dashboard assembly's string literals, which is exactly what reaches the browser.
/// </summary>
public sealed class BlazorEventDirectiveGuardrailTests
{
    private static readonly Regex LiteralEventAttribute = new(@"^@on[a-z]+(?::[A-Za-z]+)?$", RegexOptions.Compiled);
    private static readonly Regex MarkupEventAttribute = new(@"<[a-z][^<>]*\s@on[a-z]+", RegexOptions.Compiled);

    [Fact]
    public void TheCompiledDashboardRendersNoAttributeNamesStartingWithAtOn()
    {
        var offenders = CompiledStrings(typeof(AppDrawer).Assembly.Location).Where(IsUnrecognisedDirective).ToList();

        Assert.True(offenders.Count == 0, "The Razor compiler left these event directives as literal '@' attributes; register the event or handle it in JavaScript:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void TheScanFlagsLiteralDirectivesAndIgnoresOrdinaryText()
    {
        Assert.True(IsUnrecognisedDirective("@oncancel:preventDefault"));
        Assert.True(IsUnrecognisedDirective("@onsomething"));
        Assert.True(IsUnrecognisedDirective("<dialog class=\"x\" @onclose:preventDefault></dialog>"));
        Assert.False(IsUnrecognisedDirective("onclick"));
        Assert.False(IsUnrecognisedDirective("Write to support@onlinehelp.example"));
        Assert.False(IsUnrecognisedDirective("@media (min-width: 600px)"));
    }

    private static bool IsUnrecognisedDirective(string value) => LiteralEventAttribute.IsMatch(value) || MarkupEventAttribute.IsMatch(value);

    private static IEnumerable<string> CompiledStrings(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        var strings = new List<string>();
        for (var handle = MetadataTokens.UserStringHandle(1); !handle.IsNil; handle = reader.GetNextHandle(handle))
        {
            strings.Add(reader.GetUserString(handle));
        }

        return strings;
    }
}
