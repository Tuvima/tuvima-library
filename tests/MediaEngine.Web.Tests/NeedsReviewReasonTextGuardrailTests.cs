using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Services.Editing;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Every reason the Engine can raise has its own plain sentence in the Dashboard's language file, and no sentence uses
/// engineering words (spec needs-review-redesign D3, A3).
/// </summary>
public sealed class NeedsReviewReasonTextGuardrailTests
{
    private static readonly string[] BannedWords = ["retail", "canonical", "QID", "pipeline", "metadata", "provider"];

    private static readonly Lazy<IReadOnlyDictionary<string, string>> Strings = new(LoadStrings);

    private static IEnumerable<string> Constants(Type type) =>
        type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);

    public static TheoryData<string> Triggers() => ToData(Constants(typeof(ReviewTrigger)));

    public static TheoryData<string> FileReasons() => ToData(Constants(typeof(UnmatchedReasons)));

    private static TheoryData<string> ToData(IEnumerable<string> values)
    {
        var data = new TheoryData<string>();
        foreach (var value in values)
        {
            data.Add(value);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Triggers))]
    public void EveryReviewTrigger_HasItsOwnPlainSentence(string trigger)
    {
        var key = "NeedsReviewReason_" + trigger;

        Assert.Equal(key, NeedsReviewReasonText.KeyForTrigger(trigger));
        Assert.True(Strings.Value.TryGetValue(key, out var sentence), $"SharedStrings.resx has no {key}.");
        Assert.False(string.IsNullOrWhiteSpace(sentence));
        Assert.NotEqual(Strings.Value["NeedsReviewReason_Unknown"], sentence);
    }

    [Theory]
    [MemberData(nameof(FileReasons))]
    public void EveryUnmatchedReason_HasItsOwnPlainSentence(string reason)
    {
        var key = "NeedsReviewReason_File_" + reason;

        Assert.Equal(key, NeedsReviewReasonText.KeyForUnmatchedReason(reason));
        Assert.True(Strings.Value.TryGetValue(key, out var sentence), $"SharedStrings.resx has no {key}.");
        Assert.False(string.IsNullOrWhiteSpace(sentence));
        Assert.NotEqual(Strings.Value["NeedsReviewReason_File_Unknown"], sentence);
    }

    [Fact]
    public void EveryKeyTheClassKnows_ExistsInTheLanguageFile()
    {
        var missing = NeedsReviewReasonText.AllKeys.Where(key => !Strings.Value.ContainsKey(key)).ToList();

        Assert.True(missing.Count == 0, "These keys are not in SharedStrings.resx:\n" + string.Join('\n', missing));
    }

    [Fact]
    public void NoReasonSentence_UsesEngineeringWords()
    {
        var offenders = Strings.Value
            .Where(entry => entry.Key.StartsWith("NeedsReviewReason_", StringComparison.Ordinal))
            .SelectMany(entry => BannedWords
                .Where(word => Regex.IsMatch(entry.Value, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase))
                .Select(word => $"{entry.Key} uses \"{word}\""))
            .ToList();

        Assert.True(offenders.Count == 0, string.Join('\n', offenders));
    }

    private static IReadOnlyDictionary<string, string> LoadStrings()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "MediaEngine.Web", "Resources", "SharedStrings.resx")))
        {
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new InvalidOperationException("Could not find the repository root from the test output folder.");
        }

        return XDocument.Load(Path.Combine(dir.FullName, "src", "MediaEngine.Web", "Resources", "SharedStrings.resx"))
            .Root!.Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty, StringComparer.Ordinal);
    }
}
