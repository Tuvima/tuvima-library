using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class BatchEditFieldCatalogTests
{
    [Theory]
    [InlineData("genre", "add")]
    [InlineData("genre", "replace")]
    [InlineData("genre", "remove")]
    [InlineData("custom_tags", "add")]
    [InlineData("rating", "set")]
    [InlineData("rating", "clear")]
    [InlineData("language", "set")]
    public void ValidChange_ForTheFieldsKind_IsAccepted(string key, string op)
    {
        var isList = key is "genre" or "custom_tags";
        var ok = BatchEditFieldCatalog.TryValidateChange(
            key,
            op,
            value: !isList && op == "set" ? "8.5" : null,
            values: isList ? ["Comedy"] : null,
            out var change,
            out var error);

        Assert.True(ok, error);
        Assert.Equal(op, change.Operation);
    }

    [Theory]
    [InlineData("rating", "add")]
    [InlineData("rating", "replace")]
    [InlineData("rating", "remove")]
    [InlineData("genre", "set")]
    [InlineData("genre", "clear")]
    [InlineData("custom_tags", "set")]
    [InlineData("genre", "")]
    [InlineData("genre", "merge")]
    public void Operation_ThatDoesNotFitTheFieldsKind_IsRejected(string key, string op)
    {
        var ok = BatchEditFieldCatalog.TryValidateChange(key, op, "x", ["x"], out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("director")]
    [InlineData("cast_member")]
    [InlineData("author")]
    public void ProviderFact_IsListedButNeverEditable(string key)
    {
        Assert.True(BatchEditFieldCatalog.TryGet(key, out var field));
        Assert.False(field.IsEditable);
        Assert.Empty(field.Operations);

        var ok = BatchEditFieldCatalog.TryValidateChange(key, "add", null, ["Someone"], out _, out var error);

        Assert.False(ok);
        Assert.Contains("cannot be changed", error, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownField_IsRejected()
    {
        var ok = BatchEditFieldCatalog.TryValidateChange("title", "set", "Pilot", null, out _, out var error);

        Assert.False(ok);
        Assert.Contains("not a field", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    public void ListAddOrRemove_WithNoValues_IsRejected(string op)
    {
        var ok = BatchEditFieldCatalog.TryValidateChange("custom_tags", op, null, [], out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void ListReplace_WithNoValues_EmptiesTheList()
    {
        var ok = BatchEditFieldCatalog.TryValidateChange("custom_tags", "replace", null, [], out var change, out var error);

        Assert.True(ok, error);
        Assert.Empty(change.Values);
    }

    [Fact]
    public void ListValue_WithASeparator_IsRejected()
    {
        var ok = BatchEditFieldCatalog.TryValidateChange("genre", "add", null, ["Sci-Fi, Fantasy"], out _, out var error);

        Assert.False(ok);
        Assert.Contains("comma", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ScalarSet_UsesThePencilsValidation()
    {
        var bad = BatchEditFieldCatalog.TryValidateChange("rating", "set", "eleven", null, out _, out var error);
        var good = BatchEditFieldCatalog.TryValidateChange("rating", "set", " 8.50 ", null, out var change, out _);

        Assert.False(bad);
        Assert.Contains("Rating", error, StringComparison.Ordinal);
        Assert.True(good);
        Assert.Equal("8.5", change.Value);
    }

    [Fact]
    public void ScalarSet_WithoutAValue_IsRejected()
    {
        Assert.False(BatchEditFieldCatalog.TryValidateChange("year", "set", " ", null, out _, out _));
    }

    [Fact]
    public void ListOperations_AddReplaceAndRemove_DoWhatTheyName()
    {
        IReadOnlyList<string> current = ["Drama", "Comedy"];

        Assert.Equal(["Drama", "Comedy", "Thriller"], BatchEditFieldCatalog.ApplyListOperation(current, "add", ["Thriller", "COMEDY"]));
        Assert.Equal(["Drama", "Comedy"], BatchEditFieldCatalog.ApplyListOperation(current, "add", ["comedy"]));
        Assert.Equal(["Horror"], BatchEditFieldCatalog.ApplyListOperation(current, "replace", ["Horror"]));
        Assert.Equal(["Drama"], BatchEditFieldCatalog.ApplyListOperation(current, "remove", ["comedy", "Absent"]));
    }

    [Fact]
    public void ListStoredText_UsesTheSeparatorTheEditorUsesForThatField()
    {
        BatchEditFieldCatalog.TryGet("custom_tags", out var tags);
        BatchEditFieldCatalog.TryGet("genre", out var genre);

        Assert.Equal("a; b", BatchEditFieldCatalog.ToOverrideValue(tags, ["a", "b"]));
        Assert.Equal("a, b", BatchEditFieldCatalog.ToOverrideValue(genre, ["a", "b"]));
        Assert.Equal(["a", "b"], BatchEditFieldCatalog.ParseList("a; b, a"));
    }

    [Theory]
    [InlineData("TV", "child", true, "genre", true)]
    [InlineData("TV", "parent", true, "content_rating", true)]
    [InlineData("TV", "child", true, "rating", false)]
    [InlineData("TV", "standalone", false, "genre", false)]
    [InlineData("Music", "child", true, "genre", true)]
    [InlineData("Music", "parent", true, "genre", false)]
    [InlineData("Movies", "standalone", false, "genre", false)]
    public void Inheritance_FollowsTheRulesTheDetailPageUses(string mediaType, string workKind, bool hasParent, string key, bool expected)
    {
        Assert.Equal(expected, BatchEditFieldCatalog.IsInherited(mediaType, workKind, hasParent, key));
    }
}
