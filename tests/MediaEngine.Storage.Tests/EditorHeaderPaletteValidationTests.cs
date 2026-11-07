using System.Text.Json;
using MediaEngine.Domain.Models;
using MediaEngine.Storage.Configuration;

namespace MediaEngine.Storage.Tests;

/// <summary>The editor header's gradient strength lives in <c>config/ui/palette.json</c> (universal editor v5 D9, D34), never in Dashboard code.</summary>
public sealed class EditorHeaderPaletteValidationTests
{
    private static PaletteConfiguration Shipped()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var json = File.ReadAllText(Path.Combine(dir!, "config", "ui", "palette.json"));
        return JsonSerializer.Deserialize<PaletteConfiguration>(json)!;
    }

    [Fact]
    public void TheShippedPaletteSaysHowMuchOfTheCoverColoursTheHeaderKeeps()
    {
        var palette = Shipped();

        Assert.Equal(55, palette.EditorHeader.PrimaryPercent);
        Assert.Equal(70, palette.EditorHeader.SecondaryPercent);
        Assert.Empty(JsonConfigValidator.Validate(palette, "palette.json"));
    }

    [Theory]
    [InlineData(-1, 70, "editor_header.primary_percent")]
    [InlineData(101, 70, "editor_header.primary_percent")]
    [InlineData(55, -5, "editor_header.secondary_percent")]
    [InlineData(55, 130, "editor_header.secondary_percent")]
    public void AGradientStrengthOutsideZeroToOneHundred_IsRefused(int primary, int secondary, string expected)
    {
        var palette = Shipped();
        palette.EditorHeader.PrimaryPercent = primary;
        palette.EditorHeader.SecondaryPercent = secondary;

        var errors = JsonConfigValidator.Validate(palette, "palette.json");

        Assert.Contains(errors, error => error.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void ThePaletteSchemaDescribesTheEditorHeaderKeys()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        var schema = File.ReadAllText(Path.Combine(dir!, "config", "schemas", "ui", "palette.schema.json"));

        Assert.Contains("editor_header", schema, StringComparison.Ordinal);
        Assert.Contains("primary_percent", schema, StringComparison.Ordinal);
        Assert.Contains("secondary_percent", schema, StringComparison.Ordinal);
    }
}
