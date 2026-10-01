namespace MediaEngine.Web.Services.Editing;

/// <summary>Tracks one internal route change while the editor has unsaved changes.</summary>
public sealed class MediaEditorNavigationGuard
{
    private string? _allowedLocation;

    public string? PendingLocation { get; private set; }

    public bool Intercept(string targetLocation, bool hasUnsavedChanges)
    {
        if (string.Equals(_allowedLocation, targetLocation, StringComparison.Ordinal))
        {
            _allowedLocation = null;
            return false;
        }

        _allowedLocation = null;
        if (!hasUnsavedChanges)
        {
            PendingLocation = null;
            return false;
        }

        PendingLocation = targetLocation;
        return true;
    }

    public string? Approve()
    {
        var location = PendingLocation;
        PendingLocation = null;
        _allowedLocation = location;
        return location;
    }

    public void Stay()
    {
        PendingLocation = null;
        _allowedLocation = null;
    }
}
