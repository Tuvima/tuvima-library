using MediaEngine.Providers.Services.Vault;
using MediaEngine.Providers.Tests.StashBox;
using SkiaSharp;

namespace MediaEngine.Providers.Tests.Vault;

/// <summary>Vault pictures are served at the sizes of Videos spec D41, from cached copies made once beside the original.</summary>
public sealed class VaultArtworkSizerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tuvima-vault-size-" + Guid.NewGuid().ToString("N"));

    public VaultArtworkSizerTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Original(int width, int height)
    {
        var path = Path.Combine(_folder, "scene.jpg");
        File.WriteAllBytes(path, TestImages.Jpeg(width, height));
        return path;
    }

    private static int WidthOf(string path)
    {
        using var codec = SKCodec.Create(path);
        return codec!.Info.Width;
    }

    [Fact]
    public async Task AWiderOriginal_GetsACopyAtTheAskedWidth_ThatIsReusedAndLeavesTheOriginalAlone()
    {
        var original = Original(1600, 900);
        var originalBytes = File.ReadAllBytes(original);

        var first = await VaultArtworkSizer.GetOrCreateCopyAsync(original, 640);
        var second = await VaultArtworkSizer.GetOrCreateCopyAsync(original, 640);

        Assert.NotNull(first);
        Assert.NotEqual(original, first);
        Assert.Equal(640, WidthOf(first));
        Assert.Equal(first, second);
        Assert.Equal(originalBytes, File.ReadAllBytes(original));
    }

    [Fact]
    public async Task AnOriginalNoWiderThanTheRequest_IsServedAsItIs()
    {
        var original = Original(500, 300);

        Assert.Equal(original, await VaultArtworkSizer.GetOrCreateCopyAsync(original, 640));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task AWidthThatIsNotOnTheList_IsRefused_AndNothingIsWritten()
    {
        var original = Original(1600, 900);

        Assert.Null(await VaultArtworkSizer.GetOrCreateCopyAsync(original, 123));
        Assert.Null(await VaultArtworkSizer.GetOrCreateCopyAsync(original, 0));
        Assert.Single(Directory.GetFiles(_folder));
        Assert.False(VaultArtworkSizer.IsAllowedWidth(123));
        Assert.All(new[] { 300, 400, 640, 800, 1280 }, width => Assert.True(VaultArtworkSizer.IsAllowedWidth(width)));
    }

    [Fact]
    public async Task AMissingOrUnreadableOriginal_GivesNothing()
    {
        Assert.Null(await VaultArtworkSizer.GetOrCreateCopyAsync(Path.Combine(_folder, "missing.jpg"), 640));

        var notAnImage = Path.Combine(_folder, "bad.jpg");
        File.WriteAllText(notAnImage, "not an image");
        Assert.Null(await VaultArtworkSizer.GetOrCreateCopyAsync(notAnImage, 640));
    }

    [Fact]
    public async Task DeleteCopies_RemovesEveryCopy_AndKeepsTheOriginal()
    {
        var original = Original(1600, 900);
        await VaultArtworkSizer.GetOrCreateCopyAsync(original, 640);
        await VaultArtworkSizer.GetOrCreateCopyAsync(original, 1280);
        Assert.Equal(3, Directory.GetFiles(_folder).Length);

        VaultArtworkSizer.DeleteCopies(original);

        Assert.Equal([original], Directory.GetFiles(_folder));
    }
}
