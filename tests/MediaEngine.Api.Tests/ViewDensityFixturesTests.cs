using System.Security.Cryptography;
using MediaEngine.Api.DevSupport;
using SkiaSharp;

namespace MediaEngine.Api.Tests;

public sealed class ViewDensityFixturesTests
{
    [Fact]
    public void Catalog_ExercisesDenseDaysMultipleYearsAndImageShapes()
    {
        var samples = ViewDensityFixtures.Create();
        Assert.Equal(360, samples.Count);
        Assert.Equal(360, samples.Select(sample => sample.FileName).Distinct().Count());
        Assert.Equal(180, samples.Count(sample => sample.City == "Seattle"));
        Assert.Equal(72, samples.Count(sample => sample.City == "Tokyo"));
        Assert.Equal(36, samples.Count(sample => sample.City == "Paris"));
        Assert.Equal(120, samples.Count(sample => sample.City == "Seattle" && sample.CapturedAt.Date == new DateTime(2026, 9, 20)));
        Assert.Equal(7, samples.Select(sample => sample.CapturedAt.Year).Distinct().Count());
        Assert.Equal(4, samples.Select(sample => (sample.Width, sample.Height)).Distinct().Count());
        Assert.Equal(72, samples.Count(sample => sample.City == "Chicago"));
        Assert.Equal(16, samples.Select(sample => sample.LocationName).Distinct().Count());
        foreach (var group in samples.GroupBy(sample => sample.LocationName))
        {
            Assert.Equal(group.Count(), group.Select(sample => (sample.Latitude, sample.Longitude)).Distinct().Count());
        }
        Assert.Equal(24, samples.Count(sample => sample.LocationName == "Naperville, United States"));
        Assert.Equal(12, samples.Count(sample => sample.LocationName == "Paris, France"));
        Assert.All(samples, sample => { Assert.InRange(sample.Latitude, -90, 90); Assert.InRange(sample.Longitude, -180, 180); });
    }

    [Fact]
    public void Images_AreDecodableUniqueAndRepeatableForContentDeduplication()
    {
        var hashes = new HashSet<string>();
        foreach (var sample in ViewDensityFixtures.Create())
        {
            var bytes = ViewDensityFixtures.Render(sample);
            using var decoded = SKBitmap.Decode(bytes);
            Assert.Equal(sample.Width, decoded.Width);
            Assert.Equal(sample.Height, decoded.Height);
            Assert.True(hashes.Add(Convert.ToHexString(SHA256.HashData(bytes))));
            Assert.Equal(bytes, ViewDensityFixtures.Render(sample));
        }
    }
}
