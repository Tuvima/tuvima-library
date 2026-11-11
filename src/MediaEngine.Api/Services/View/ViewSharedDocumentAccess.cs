using MediaEngine.Domain.PersonalMedia;

namespace MediaEngine.Api.Services.View;

/// <summary>
/// Who may tag and edit the details of a document in the household's Shared Library: anyone who may use Documents and
/// open the Shared Library. A document added to Shared is an ordinary shared document, so there is no Shared Inbox and
/// nothing waits for an administrator. Child profiles follow their Documents switch. Deleting from Shared stays an
/// administrator action and is decided elsewhere.
/// </summary>
public static class ViewSharedDocumentAccess
{
    public static bool CanEdit(ViewProfilePolicy policy) =>
        policy.ViewEnabled && policy.AccessSharedLibrary && policy.DocumentsEnabled;
}
