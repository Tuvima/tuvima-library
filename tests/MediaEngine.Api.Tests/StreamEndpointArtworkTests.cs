using System.Reflection;
using MediaEngine.Api.Endpoints;
using MediaEngine.Domain.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Api.Tests;

public sealed class StreamEndpointArtworkTests
{
    [Fact]
    public async Task CreateLocalArtworkResult_ReturnsSvgPlaceholder_WhenLocalImageCannotDecode()
    {
        var corruptPath = Path.Combine(Path.GetTempPath(), $"tuvima-artwork-{Guid.NewGuid():N}.png");
        await File.WriteAllTextAsync(corruptPath, "not an image");

        try
        {
            var result = InvokeCreateLocalArtworkResult(corruptPath);

            Assert.NotNull(result);

            var context = new DefaultHttpContext();
            context.RequestServices = new ServiceCollection()
                .AddLogging()
                .BuildServiceProvider();
            await using var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            await result.ExecuteAsync(context);

            responseBody.Position = 0;
            using var reader = new StreamReader(responseBody);
            var body = await reader.ReadToEndAsync();

            Assert.StartsWith("image/svg+xml", context.Response.ContentType, StringComparison.Ordinal);
            Assert.Contains("<svg", body, StringComparison.Ordinal);
            Assert.Contains("Artwork unavailable", body, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(corruptPath);
        }
    }

    [Fact]
    public void ResolveArtworkPath_FallsBackToOriginal_WhenSizedRenditionIsMissing()
    {
        var originalPath = CreateTempImagePath();
        var missingSmallPath = originalPath + ".missing";

        try
        {
            var asset = new EntityAsset
            {
                LocalImagePath = originalPath,
                LocalImagePathSmall = missingSmallPath,
            };

            var result = InvokeResolveArtworkPath(asset, "s");

            Assert.Equal(originalPath, result);
        }
        finally
        {
            File.Delete(originalPath);
        }
    }

    [Fact]
    public void ResolveArtworkPath_UsesRequestedRendition_WhenItExists()
    {
        var originalPath = CreateTempImagePath();
        var smallPath = CreateTempImagePath();

        try
        {
            var asset = new EntityAsset
            {
                LocalImagePath = originalPath,
                LocalImagePathSmall = smallPath,
            };

            var result = InvokeResolveArtworkPath(asset, "s");

            Assert.Equal(smallPath, result);
        }
        finally
        {
            File.Delete(originalPath);
            File.Delete(smallPath);
        }
    }

    [Fact]
    public async Task CreateLocalArtworkResult_StreamsFileWithValidatorsAndPrivateRevalidateHeaders()
    {
        var path = CreateTempImagePath();
        try
        {
            var result = Assert.IsAssignableFrom<IResult>(InvokeCreateLocalArtworkResult(path));
            var context = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
            };
            await using var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            await result.ExecuteAsync(context);

            Assert.Equal("image/jpeg", context.Response.ContentType);
            Assert.False(string.IsNullOrWhiteSpace(context.Response.Headers.ETag));
            Assert.False(string.IsNullOrWhiteSpace(context.Response.Headers.LastModified));
            Assert.Equal("private, no-cache", context.Response.Headers.CacheControl.ToString());
            Assert.Equal(await File.ReadAllBytesAsync(path), responseBody.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string? InvokeResolveArtworkPath(EntityAsset asset, string? size)
    {
        var method = typeof(StreamEndpoints).GetMethod(
            "ResolveArtworkPath",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (string?)method.Invoke(null, [asset, size]);
    }

    private static IResult? InvokeCreateLocalArtworkResult(string? path)
    {
        var method = typeof(StreamEndpoints).GetMethod(
            "CreateLocalArtworkResult",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);
        return (IResult?)method.Invoke(null, [path]);
    }

    private static string CreateTempImagePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-artwork-{Guid.NewGuid():N}.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 0xFF, 0xD9]);
        return path;
    }
}
