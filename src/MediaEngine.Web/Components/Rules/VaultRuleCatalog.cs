using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Ui;

namespace MediaEngine.Web.Components.Rules;

/// <summary>
/// The rule fields a Vault image gallery offers (Videos spec D44). It is View's Smart Gallery rule builder with a narrower list: only what
/// Vault images really have (kind, file type, shape, date, the Vault's own tags and the favourite mark). View's people, places, devices,
/// owners, sources and Documents are not offered, and the Engine refuses them too (<c>ViewSmartGalleryRules.VaultFields</c>).
/// </summary>
public static class VaultRuleCatalog
{
    public static RuleBuilderCatalog Instance { get; } = new()
    {
        Domain = "vault",
        Eyebrow = "Smart gallery membership",
        Title = "Build rules",
        Description = "Choose which Vault images belong in this gallery.",
        DefaultCategory = "media",
        ShowSortControls = false,
        Categories =
        [
            new("media", "Image", AppMaterialIcons.Outlined.PhotoLibrary),
            new("library", "Vault", AppMaterialIcons.Outlined.Inventory2),
        ],
        Fields =
        [
            ViewRuleCatalog.Choice("media", "media_type", "Media type", AppMaterialIcons.Outlined.PhotoLibrary, [new("image", "Images")]),
            ViewRuleCatalog.Text("media", "file_type", "File type", AppMaterialIcons.Outlined.InsertDriveFile, [RuleOperators.Is, RuleOperators.IsNot]),
            ViewRuleCatalog.Choice("media", "orientation", "Orientation", AppMaterialIcons.Outlined.CropRotate, [new("landscape", "Landscape"), new("portrait", "Portrait"), new("square", "Square")]),
            ViewRuleCatalog.Text("media", "captured_date", "Date", AppMaterialIcons.Outlined.CalendarToday,
                [new("eq", "is on"), new("gt", "is after"), new("lt", "is before"), new("between", "is between")]),
            ViewRuleCatalog.Text("library", "tags", "Tags", AppMaterialIcons.Outlined.LocalOffer,
                [RuleOperators.Is, RuleOperators.IsNot, RuleOperators.Contains, RuleOperators.Known, RuleOperators.Unknown]),
            ViewRuleCatalog.Boolean("library", "favorite", "Favorite", AppMaterialIcons.Outlined.FavoriteBorder),
        ],
    };
}
