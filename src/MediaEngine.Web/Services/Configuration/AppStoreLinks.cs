namespace MediaEngine.Web.Services.Configuration;

/// <summary>
/// Where each Tuvima app can be installed, read from <c>app_store_links</c> in <c>config/ui/global.json</c>. A blank value means the
/// app is not published in that store yet, and the Apps page shows nothing for it.
/// </summary>
public sealed record AppStoreLinks(string AppStore = "", string GooglePlay = "", string Roku = "");
