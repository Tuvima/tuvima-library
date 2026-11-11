namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// How a delete started from the universal editor ended. <see cref="Deleted"/> says whether anything was removed (the editor has already moved
/// on); <see cref="Notice"/> is the line the editor window shows in place (an alert) when something needs saying: why nothing happened, that
/// the delete failed, or that a file stayed on disk. A plain success has no notice; the item leaving the editor is the confirmation.
/// </summary>
/// <param name="Deleted">True when anything was removed.</param>
/// <param name="Notice">What to show in the window, or null.</param>
/// <param name="NoticeIsError">True when the notice is a failure; false when it is a warning about something that otherwise worked.</param>
public readonly record struct EditorDeleteResult(bool Deleted, string? Notice = null, bool NoticeIsError = false)
{
    /// <summary>Nothing was removed and there is nothing to say (the person said no).</summary>
    public static EditorDeleteResult Nothing => new(false);

    /// <summary>Nothing was removed, and this is why.</summary>
    public static EditorDeleteResult Failed(string notice) => new(false, notice, true);
}
