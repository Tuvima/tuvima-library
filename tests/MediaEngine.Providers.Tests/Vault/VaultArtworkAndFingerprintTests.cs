using System.Security.Cryptography;
using MediaEngine.Providers.Services.Vault;
using MediaEngine.Providers.Tests.StashBox;

namespace MediaEngine.Providers.Tests.Vault;

/// <summary>Images from a server are checked before they are kept (Videos spec D31); fingerprints are computed on this machine (D35).</summary>
public sealed class VaultArtworkAndFingerprintTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tuvima-vault-fp-" + Guid.NewGuid().ToString("N"));

    public VaultArtworkAndFingerprintTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Validate_ARealLandscapeJpeg_IsAccepted()
    {
        var (image, refusal) = VaultArtworkValidator.Validate(TestImages.Jpeg(640, 360), VaultImagePurpose.Cover);

        Assert.NotNull(image);
        Assert.Equal(VaultImageRefusal.None, refusal);
        Assert.Equal("image/jpeg", image.MimeType);
        Assert.Equal(64, image.Hash.Length);
    }

    [Theory]
    [InlineData("png", "image/png")]
    [InlineData("webp", "image/webp")]
    public void Validate_PngAndWebp_AreAccepted(string kind, string mime)
    {
        var bytes = kind == "png" ? TestImages.Png(640, 360) : TestImages.Webp(640, 360);

        var (image, _) = VaultArtworkValidator.Validate(bytes, VaultImagePurpose.Cover);

        Assert.Equal(mime, image!.MimeType);
    }

    [Fact]
    public void Validate_BytesThatAreNotAnImage_AreRefused()
    {
        var notAnImage = new byte[4096];
        new Random(3).NextBytes(notAnImage);

        Assert.Equal(VaultImageRefusal.NotAnImage, VaultArtworkValidator.Validate(notAnImage, VaultImagePurpose.Cover).Refusal);
        Assert.Equal(VaultImageRefusal.NotAnImage, VaultArtworkValidator.Validate(null, VaultImagePurpose.Cover).Refusal);
        Assert.Equal(VaultImageRefusal.NotAnImage, VaultArtworkValidator.Validate(new byte[100], VaultImagePurpose.Cover).Refusal);
    }

    [Fact]
    public void Validate_AWebPageDressedAsAnImage_IsRefused()
    {
        var html = System.Text.Encoding.UTF8.GetBytes("<html><script>alert(1)</script>" + new string(' ', 3000) + "</html>");

        Assert.Equal(VaultImageRefusal.NotAnImage, VaultArtworkValidator.Validate(html, VaultImagePurpose.Cover).Refusal);
    }

    [Fact]
    public void Validate_AnImageOfTheWrongKind_IsRefused()
    {
        Assert.Equal(VaultImageRefusal.WrongType, VaultArtworkValidator.Validate(TestImages.Gif(), VaultImagePurpose.Cover).Refusal);
    }

    [Fact]
    public void Validate_ATinyImage_IsRefused()
    {
        Assert.Equal(VaultImageRefusal.TooSmall, VaultArtworkValidator.Validate(TestImages.Png(120, 80), VaultImagePurpose.Cover).Refusal);
    }

    [Fact]
    public void Validate_AnOversizedFile_IsRefused()
    {
        var huge = new byte[VaultArtworkValidator.MaxFileBytes + 1];

        Assert.Equal(VaultImageRefusal.TooLarge, VaultArtworkValidator.Validate(huge, VaultImagePurpose.Cover).Refusal);
    }

    [Fact]
    public void Validate_AnOddlyShapedImage_IsRefused()
    {
        Assert.Equal(VaultImageRefusal.WrongShape, VaultArtworkValidator.Validate(TestImages.Png(2000, 250), VaultImagePurpose.Cover).Refusal);
    }

    [Fact]
    public void Validate_AHugeImage_IsKeptAtMostTwentyFiveSixtyOnTheLongEdge()
    {
        var (image, _) = VaultArtworkValidator.Validate(TestImages.Jpeg(4000, 2250), VaultImagePurpose.Cover);

        Assert.NotNull(image);
        Assert.Equal(VaultArtworkValidator.MaxStoredLongEdge, image.Width);
    }

    [Fact]
    public void Validate_EmbeddedLocationAndCameraData_IsNotKept()
    {
        var bytes = TestImages.JpegWithExif(640, 360);
        Assert.Contains("GPSLatitude", System.Text.Encoding.ASCII.GetString(bytes), StringComparison.Ordinal);

        var (image, _) = VaultArtworkValidator.Validate(bytes, VaultImagePurpose.Cover);

        Assert.NotNull(image);
        var kept = System.Text.Encoding.ASCII.GetString(image.Bytes);
        Assert.DoesNotContain("GPSLatitude", kept, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateCamera", kept, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ASmallStillFrame_IsAcceptedForAStillButNotForACover()
    {
        var still = TestImages.Jpeg(120, 68);

        Assert.NotNull(VaultArtworkValidator.Validate(still, VaultImagePurpose.Still).Image);
        Assert.Equal(VaultImageRefusal.TooSmall, VaultArtworkValidator.Validate(still, VaultImagePurpose.Cover).Refusal);
    }

    // ---------------------------------------------------------------- fingerprints

    [Fact]
    public void ComputeOshash_IsSixteenHexCharactersAndStableForTheSameBytes()
    {
        var path = WriteFile("a.mp4", 300_000, seed: 5);

        var first = VaultFileFingerprinter.ComputeOshash(path);

        Assert.NotNull(first);
        Assert.Equal(16, first.Length);
        Assert.Equal(first, VaultFileFingerprinter.ComputeOshash(path));
    }

    [Fact]
    public void ComputeOshash_ChangesWhenTheContentChanges_AndIsNullForATinyFile()
    {
        var a = VaultFileFingerprinter.ComputeOshash(WriteFile("a.mp4", 300_000, seed: 5));
        var b = VaultFileFingerprinter.ComputeOshash(WriteFile("b.mp4", 300_000, seed: 6));

        Assert.NotEqual(a, b);
        Assert.Null(VaultFileFingerprinter.ComputeOshash(WriteFile("tiny.mp4", 1000, seed: 1)));
    }

    [Fact]
    public async Task ComputeMd5Async_MatchesTheFullFileHash()
    {
        var path = WriteFile("a.mp4", 200_000, seed: 9);

        var hash = await VaultFileFingerprinter.ComputeMd5Async(path);

        Assert.Equal(Convert.ToHexString(MD5.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), hash.ToLowerInvariant());
    }

    [Fact]
    public async Task ComputeAsync_GivesOshashAndMd5WithTheFileSize()
    {
        var path = WriteFile("a.mp4", 200_000, seed: 9);

        var all = await VaultFileFingerprinter.ComputeAsync(path, TimeProvider.System);

        Assert.Contains(all, f => f.Algorithm == "OSHASH");
        Assert.Contains(all, f => f.Algorithm == "MD5");
        Assert.All(all, f => Assert.Equal(200_000, f.FileSize));
    }

    private string WriteFile(string name, int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        var path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
