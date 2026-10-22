using System.Diagnostics;
using MediaEngine.Domain.Configuration;
using MediaEngine.Providers.Services.Vault;

namespace MediaEngine.Providers.Tests.Vault;

/// <summary>Reading a Vault file name (Videos spec D38a) and checking the text a server sends (D30).</summary>
public sealed class VaultFileReadingTests
{
    private static VaultFileNameReader Reader() => new(new VaultNamingConfigurationProvider(() => null));

    [Fact]
    public void Read_StudioDateAndTitle_AreTakenFromTheFirstPatternThatFits()
    {
        var parts = Reader().Read("/vault/Example Studio - 2021-03-04 - Scene One 1080p.mp4");

        Assert.True(parts.PatternMatched);
        Assert.Equal("Example Studio", parts.Studio);
        Assert.Equal(new DateOnly(2021, 3, 4), parts.Date);
        Assert.Equal("Scene One", parts.Title);
    }

    [Fact]
    public void Read_DotsAreSpaces_AndReleaseWordsAreIgnored()
    {
        var parts = Reader().Read("/vault/Example.Studio.2021.03.04.Scene.One.x264.WEB-DL.mp4");

        Assert.Equal("Example Studio", parts.Studio);
        Assert.Equal(new DateOnly(2021, 3, 4), parts.Date);
        Assert.Equal("Scene One", parts.Title);
    }

    [Fact]
    public void Read_ACodeAndTitlePattern_ReadsTheProductCode()
    {
        var parts = Reader().Read("/vault/abc123 - Scene One.mkv");

        Assert.Equal("ABC-123", parts.ProductCode);
        Assert.Equal("Scene One", parts.Title);
    }

    [Fact]
    public void Read_ACodeInTheFolderName_IsUsedWhenTheFileHasNone()
    {
        var parts = Reader().Read("/vault/XYZ-0456/clip.mp4");

        Assert.Equal("XYZ-0456", parts.ProductCode);
        Assert.Equal("clip", parts.Title);
    }

    [Theory]
    [InlineData("/vault/Some Clip HD720.mp4")]
    [InlineData("/vault/Some Clip 1080p x264.mp4")]
    [InlineData("/vault/Part 12 of the story.mp4")]
    public void Read_SizesDiscsAndVolumes_AreNotProductCodes(string path)
    {
        Assert.Null(Reader().Read(path).ProductCode);
    }

    [Fact]
    public void Read_ANameThatFitsNoPattern_IsTheCleanedNameAndIsMarkedGuessed()
    {
        var parts = Reader().Read("/vault/just a plain name.mp4");

        Assert.False(parts.PatternMatched);
        Assert.Equal("just a plain name", parts.Title);
    }

    [Fact]
    public void Read_AHostileVeryLongName_FinishesQuicklyAndStaysShort()
    {
        var name = string.Concat(Enumerable.Repeat("a - ", 3000)) + "x";
        var clock = Stopwatch.StartNew();

        var parts = Reader().Read("/vault/" + name + ".mp4");

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
        Assert.True(parts.Title.Length <= 300);
    }

    [Fact]
    public void SameCode_IgnoresCaseAndDashes()
    {
        Assert.True(VaultFileNameReader.SameCode("ABC-123", "abc123"));
        Assert.False(VaultFileNameReader.SameCode("ABC-123", "ABC-124"));
        Assert.False(VaultFileNameReader.SameCode(null, "ABC-123"));
    }

    [Fact]
    public void FindProductCode_UsesTheConfiguredIgnoredPrefixes()
    {
        var rules = new VaultProductCodeConfiguration { IgnoredPrefixes = ["abc"] };

        Assert.Null(VaultFileNameReader.FindProductCode("ABC-123", rules));
        Assert.Equal("XYZ-77", VaultFileNameReader.FindProductCode("xyz77", rules));
    }

    // ------------------------------------------------------------- the checks on text from a server

    [Fact]
    public void Title_MarkupLinksAndHiddenCharactersAreRemoved()
    {
        Assert.Equal("Example Scene One", VaultFieldValidator.Title("<b>Example</b> Scene&#x200B; One"));
        Assert.Equal("Click", VaultFieldValidator.Title("[Click](https://spam.example.net/buy)"));
        Assert.Null(VaultFieldValidator.Title("https://spam.example.net/buy"));
        Assert.Null(VaultFieldValidator.Title("<script></script>"));
    }

    [Fact]
    public void Title_EncodedMarkupDoesNotComeBackAsATag()
    {
        var cleaned = VaultFieldValidator.Title("&lt;script&gt;x&lt;/script&gt;Hello");

        Assert.NotNull(cleaned);
        Assert.DoesNotContain('<', cleaned);
        Assert.DoesNotContain("script", cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Description_KeepsLineBreaksButNotMarkupOrLinks()
    {
        var cleaned = VaultFieldValidator.Description("A <script>alert(1)</script>short description. See https://spam.example.net/buy now.\nSecond line.");

        Assert.NotNull(cleaned);
        Assert.DoesNotContain('<', cleaned);
        Assert.DoesNotContain("https", cleaned, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\nSecond line.", cleaned);
    }

    [Fact]
    public void Text_IsCutToTheLimit()
    {
        Assert.Equal(VaultFieldValidator.MaxTitleLength, VaultFieldValidator.Title(new string('a', 5000))!.Length);
    }

    [Fact]
    public void Names_AreCleanedAndDeduplicatedIgnoringCase()
    {
        var names = VaultFieldValidator.Names(["Performer One", "performer one", "<i>Performer Two</i>", "  ", "http://x.example.net"]);

        Assert.Equal(["Performer One", "Performer Two"], names);
    }

    [Theory]
    [InlineData(1899, 12, 31, false)]
    [InlineData(1900, 1, 1, true)]
    [InlineData(2021, 3, 4, true)]
    public void IsSensibleDate_RejectsImpossibleDates(int year, int month, int day, bool expected)
    {
        Assert.Equal(expected, VaultFieldValidator.IsSensibleDate(new DateOnly(year, month, day)));
    }

    [Fact]
    public void IsSensibleDate_RejectsTheFarFuture()
    {
        Assert.False(VaultFieldValidator.IsSensibleDate(DateOnly.FromDateTime(DateTime.UtcNow).AddYears(5)));
    }

    [Theory]
    [InlineData("日本語のタイトル", "ja")]
    [InlineData("한국어 제목", "ko")]
    [InlineData("Заголовок", "ru")]
    [InlineData("Plain title", "und-Latn")]
    public void NoteLanguage_IsReadFromTheWritingSystem(string text, string expected)
    {
        Assert.Equal(expected, VaultFieldValidator.NoteLanguage(text));
    }
}
