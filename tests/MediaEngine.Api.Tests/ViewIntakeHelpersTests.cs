using MediaEngine.Api.Services.LocalAssets.Intake;
using MediaEngine.Domain.Configuration;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

public sealed class ViewIntakeHelpersTests
{
    private static readonly DateTimeOffset Today = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("IMG_20240315_142233.jpg", 2024, 3, 15)]
    [InlineData("PXL_20240315_142233123.jpg", 2024, 3, 15)]
    [InlineData("VID-20240315-WA0001.mp4", 2024, 3, 15)]
    [InlineData("WhatsApp Image 2024-03-15 at 14.22.33.jpeg", 2024, 3, 15)]
    [InlineData("Screenshot_2024-03-15-14-22-33.png", 2024, 3, 15)]
    public void ADateInThePhoneOrCameraName_IsRead(string name, int year, int month, int day)
    {
        var date = Assert.IsType<DateTimeOffset>(ViewCaptureDates.FromFileName(name, Today));

        Assert.Equal((year, month, day), (date.Year, date.Month, date.Day));
    }

    [Theory]
    [InlineData("holiday.jpg")]
    [InlineData("IMG_20241345_142233.jpg")]
    [InlineData("IMG_20240230_142233.jpg")]
    [InlineData("IMG_19800101_000000.jpg")]
    [InlineData("IMG_20990101_000000.jpg")]
    [InlineData("1710512553123.jpg")]
    public void ANameWithNoRealDate_GivesNoDate(string name) =>
        Assert.Null(ViewCaptureDates.FromFileName(name, Today));

    [Fact]
    public void TheCameraDateWins_ThenTheNameDate_ThenTheFileDate()
    {
        var fileDate = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var camera = new DateTimeOffset(2019, 5, 6, 7, 8, 9, TimeSpan.Zero);

        Assert.Equal(ViewCaptureDateSource.Metadata, ViewCaptureDates.Resolve(camera, "IMG_20240315_142233.jpg", fileDate, Today).Source);
        Assert.Equal(ViewCaptureDateSource.FileName, ViewCaptureDates.Resolve(null, "IMG_20240315_142233.jpg", fileDate, Today).Source);
        Assert.Equal(ViewCaptureDateSource.FileDate, ViewCaptureDates.Resolve(null, "holiday.jpg", fileDate, Today).Source);
    }

    [Fact]
    public void AMetadataDateInTheFuture_IsNotTrusted()
    {
        var future = Today.AddYears(3);

        Assert.Equal(ViewCaptureDateSource.FileDate,
            ViewCaptureDates.Resolve(future, "holiday.jpg", Today.AddDays(-5), Today).Source);
    }

    [Fact]
    public void TheDefaultLayout_IsYearThenDateFolder()
    {
        var folder = ViewFilingLayout.FolderFor(PhotoFilingSettings.DefaultLayoutPattern, new DateTimeOffset(2024, 3, 5, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2024, 3, 5, 12, 0, 0))));

        Assert.Equal(Path.Combine("2024", "2024-03-05"), folder);
    }

    [Theory]
    [InlineData("{year}/{month}", true)]
    [InlineData("{year}/{year}-{month}-{day}", true)]
    [InlineData("Photos/{year}", true)]
    [InlineData("", false)]
    [InlineData("nothing-here", false)]
    [InlineData("{year}/{colour}", false)]
    [InlineData("../{year}", false)]
    [InlineData("/{year}", false)]
    [InlineData("{year}//{month}", false)]
    [InlineData("{year}/bad:name", false)]
    public void ALayoutIsUsable_OnlyWhenItIsRelativeFoldersMadeOfKnownParts(string pattern, bool valid) =>
        Assert.Equal(valid, ViewFilingLayout.IsValidPattern(pattern, out _));

    [Fact]
    public void AnUnusableLayout_FallsBackToTheDefault() =>
        Assert.Equal(PhotoFilingSettings.DefaultLayoutPattern, ViewFilingLayout.EffectivePattern("../escape"));

    [Fact]
    public void AClashGivesTheWholeGroupTheSameNumber()
    {
        var taken = new HashSet<string> { Path.Combine("d", "a.jpg") };

        var names = ViewFilingLayout.NamesFor("d", ["a.jpg", "a.nef"], taken.Contains);

        Assert.Equal(["a (2).jpg", "a (2).nef"], names);
    }

    [Theory]
    [InlineData("IMG_1.jpg", true)]
    [InlineData("a/b.jpg", false)]
    [InlineData("CON.jpg", false)]
    [InlineData("name.", false)]
    [InlineData("bad|name.jpg", false)]
    public void OnlyANameThatCanBeStoredIsAccepted(string name, bool storable) =>
        Assert.Equal(storable, ViewFilingLayout.IsStorableName(name));

    [Fact]
    public void AJpegWithoutItsEnd_IsDamaged_AndACompleteOneIsNot()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"tuvima-integrity-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var whole = Path.Combine(folder, "whole.jpg");
            var cut = Path.Combine(folder, "cut.jpg");
            var empty = Path.Combine(folder, "empty.jpg");
            File.WriteAllBytes(whole, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9]);
            File.WriteAllBytes(cut, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
            File.WriteAllBytes(empty, []);

            Assert.Null(ViewFileIntegrity.Check(whole, LocalAssetMediaKinds.Image));
            Assert.Equal("damaged", ViewFileIntegrity.Check(cut, LocalAssetMediaKinds.Image));
            Assert.Equal("empty", ViewFileIntegrity.Check(empty, LocalAssetMediaKinds.Image));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
