using System.Text;
using MediaEngine.Api.Services.View;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Api.Tests;

public sealed class ViewDocumentCsvTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData("a, b", "\"a, b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+44 20 7946 0000", "'+44 20 7946 0000")]
    [InlineData("-5", "'-5")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("=cmd|' /C calc'!A0", "'=cmd|' /C calc'!A0")]
    [InlineData("=SUM(1,2)", "\"'=SUM(1,2)\"")]
    [InlineData("a=b", "a=b")]
    public void CellsAreQuotedAndFormulasAreDefused(string? value, string expected)
    {
        Assert.Equal(expected, ViewDocumentCsv.Cell(value));
    }

    [Fact]
    public async Task TheFileStartsWithAByteOrderMarkAndHasOneRowPerDocumentWithCrLfEndings()
    {
        var field = new ViewDocumentFieldDto(Guid.NewGuid(), "Total", ViewDocumentFieldKinds.Money, [], 1, false);
        var item = new LocalAssetDto(
            Guid.NewGuid(), Guid.NewGuid(), null, null, LocalAssetMediaKinds.Document, "Caf\u00e9 bill", "bill.pdf", "application/pdf",
            null, new DateTimeOffset(2026, 10, 14, 23, 0, 0, TimeSpan.Zero), null, null, null, null, null, null, null, null, null,
            false, false, null, null, 1, [], ["Home", "Tax"], string.Empty, string.Empty,
            DocumentInfo: new LocalAssetDocumentInfoDto(null, false, false, true, new DateOnly(2026, 3, 1),
                new ViewCatalogueRefDto(Guid.NewGuid(), "City Water"), new ViewCatalogueRefDto(Guid.NewGuid(), "Bill"), "A-7", "private note"));
        var values = new Dictionary<Guid, IReadOnlyList<ViewDocumentFieldValueDto>>
        {
            [item.Id] = [new ViewDocumentFieldValueDto(field.Id, "Total", ViewDocumentFieldKinds.Money, "EUR", 9.5)],
        };

        using var stream = new MemoryStream();
        await ViewDocumentCsv.WriteAsync(stream, [item, item], [field], values, CancellationToken.None);
        var bytes = stream.ToArray();

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes.Take(3).ToArray());
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n");
        Assert.Equal("Title,File name,Document date,Correspondent,Type,Tags,Archive number,Total,Added", lines[0]);
        Assert.Equal("Caf\u00e9 bill,bill.pdf,2026-03-01,City Water,Bill,Home; Tax,A-7,9.50 EUR,2026-10-14", lines[1]);
        Assert.Equal(lines[1], lines[2]);
        Assert.Equal(string.Empty, lines[3]);
        Assert.DoesNotContain("private note", string.Join('|', lines), StringComparison.Ordinal);
    }

    [Fact]
    public void TheRowCapIsTenThousand()
    {
        Assert.Equal(10_000, ViewDocumentCsv.MaximumRows);
    }
}
