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
    [InlineData("narrator")]
    public void ProviderFact_IsListedButNeverEditable(string key)
    {
        Assert.True(BatchEditFieldCatalog.TryGet(key, out var field));
        Assert.False(field.IsEditable);
        Assert.Empty(field.Operations);

        var ok = BatchEditFieldCatalog.TryValidateChange(key, "add", null, ["Someone"], out _, out var error);

        Assert.False(ok);
        Assert.Contains("cannot be changed", error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("album_artist", "Album artist")]
    [InlineData("author", "Author")]
    public void GroupPeopleField_IsEditableAsAList_AndKeepsItsNameInErrors(string key, string label)
    {
        Assert.True(BatchEditFieldCatalog.TryGet(key, out var field));
        Assert.True(field.IsEditable);
        Assert.Equal(label, field.Label);
        Assert.Equal(BatchEditFieldKind.List, field.Kind);

        Assert.True(BatchEditFieldCatalog.TryValidateChange(key, "add", null, ["Someone"], out var change, out var error), error);
        Assert.Equal("Someone", Assert.Single(change.Values));

        Assert.False(BatchEditFieldCatalog.TryValidateChange(key, "add", null, ["A; B"], out _, out var bad));
        Assert.DoesNotContain("genre", bad, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Ann Lee; Bo Ray", BatchEditFieldCatalog.ToOverrideValue(field, ["Ann Lee", "Bo Ray"]));
        Assert.False(BatchEditFieldCatalog.CanBeEmptied(field));
    }

    [Theory]
    [InlineData("disc_number", "2", true)]
    [InlineData("disc_number", "0", false)]
    [InlineData("disc_number", "second", false)]
    [InlineData("series", "Dune", true)]
    public void ScalarGroupField_ChecksItsValue(string key, string value, bool valid)
    {
        var ok = BatchEditFieldCatalog.TryValidateChange(key, "set", value, null, out var change, out var error);

        Assert.Equal(valid, ok);
        if (valid)
        {
            Assert.Equal(value, change.Value);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(error));
        }
    }

    [Fact]
    public void Explicit_IsAnOnOffMarkOnlyWithSet()
    {
        Assert.True(BatchEditFieldCatalog.TryGet("explicit", out var field));
        Assert.Equal(BatchEditFieldStore.ExplicitMark, field.Store);
        Assert.Equal([BatchEditFieldCatalog.OpSet], field.Operations);

        Assert.True(BatchEditFieldCatalog.TryValidateChange("explicit", "set", "TRUE", null, out var change, out _));
        Assert.Equal("true", change.Value);
        Assert.False(BatchEditFieldCatalog.TryValidateChange("explicit", "set", "maybe", null, out _, out _));
        Assert.False(BatchEditFieldCatalog.TryValidateChange("explicit", "clear", null, null, out _, out _));
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
    public void OnlyTagsCanBeEmptied_BecauseAnEmptyGenreOverrideMeansUseTheProvidersGenres()
    {
        BatchEditFieldCatalog.TryGet("custom_tags", out var tags);
        BatchEditFieldCatalog.TryGet("genre", out var genre);

        Assert.True(BatchEditFieldCatalog.CanBeEmptied(tags));
        Assert.False(BatchEditFieldCatalog.CanBeEmptied(genre));
    }

    [Fact]
    public void GenreLimits_UseGenreWordingNotTagWording()
    {
        BatchEditFieldCatalog.TryGet("genre", out var genre);
        var tooMany = Enumerable.Range(1, BatchEditFieldCatalog.MaximumGenres + 1).Select(i => $"Genre {i}").ToList();

        Assert.False(BatchEditFieldCatalog.TryNormalizeList(genre, tooMany, out _, out var countError));
        Assert.False(BatchEditFieldCatalog.TryNormalizeList(genre, [new string('x', BatchEditFieldCatalog.MaximumGenreLength + 1)], out _, out var lengthError));
        Assert.False(BatchEditFieldCatalog.TryNormalizeList(genre, ["Sci-Fi; Fantasy"], out _, out var separatorError));
        foreach (var error in new[] { countError, lengthError, separatorError })
        {
            Assert.Contains("genre", error, StringComparison.Ordinal);
            Assert.DoesNotContain("tag", error, StringComparison.OrdinalIgnoreCase);
        }
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
