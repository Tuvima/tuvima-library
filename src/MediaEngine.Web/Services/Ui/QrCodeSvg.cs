using QRCoder;

namespace MediaEngine.Web.Services.Ui;

/// <summary>
/// Draws text (an invitation link) as a QR code in SVG, on this computer. The picture scales to whatever box it is
/// placed in, and nothing is sent anywhere.
/// </summary>
public static class QrCodeSvg
{
    public static string Render(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(4, "#000000", "#FFFFFF", true, SvgQRCode.SizingMode.ViewBoxAttribute);

        // With a view box and no size the picture would not fill its box; ask for all of it.
        const string opening = "<svg ";
        var at = svg.IndexOf(opening, StringComparison.Ordinal);
        return at < 0 ? svg : svg.Insert(at + opening.Length, "width=\"100%\" height=\"100%\" role=\"img\" aria-label=\"QR code\" ");
    }
}
