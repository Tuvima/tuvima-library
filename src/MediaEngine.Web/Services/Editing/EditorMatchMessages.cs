using MediaEngine.Contracts.Editing;
using MediaEngine.Web.Resources;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Services.Editing;

/// <summary>
/// The words for the reasons the Engine's Match reads give as codes (universal editor v5): the Engine never sends a sentence or a provider's
/// name to be shown, it sends an <see cref="EditorMatchMessageCodes"/> value, and the Dashboard says it in its own wording.
/// </summary>
public static class EditorMatchMessages
{
    /// <summary>The sentence for a code, with the value it speaks of (a number, a season) when it has one; null for no code.</summary>
    public static string? Text(IStringLocalizer<SharedStrings> text, string? code, string? value = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        return code switch
        {
            null or "" => null,
            EditorMatchMessageCodes.EnterWords => text["EditorMatchMessage_EnterWords"].Value,
            EditorMatchMessageCodes.InvalidRequest => text["EditorMatchMessage_InvalidRequest"].Value,
            EditorMatchMessageCodes.ProviderSilent => text["EditorMatchMessage_ProviderSilent"].Value,
            EditorMatchMessageCodes.NothingFound => text["EditorMatchMessage_NothingFound"].Value,
            EditorMatchMessageCodes.ProviderNotConnected => text["EditorMatchMessage_ProviderNotConnected"].Value,
            EditorMatchMessageCodes.NotOnProvider => text["EditorMatchMessage_NotOnProvider"].Value,
            EditorMatchMessageCodes.CannotChange => text["EditorMatchMessage_CannotChange"].Value,
            EditorMatchMessageCodes.WrongKind => text["EditorMatchMessage_WrongKind"].Value,
            EditorMatchMessageCodes.CouldNotRead => text["EditorMatchMessage_CouldNotRead"].Value,
            EditorMatchMessageCodes.NoNumber => text["EditorMatchMessage_NoNumber"].Value,
            EditorMatchMessageCodes.NoSuchNumber => text["EditorMatchMessage_NoSuchNumber", value ?? string.Empty].Value,
            EditorMatchMessageCodes.OtherSeason => text["EditorMatchMessage_OtherSeason", value ?? string.Empty].Value,
            EditorMatchMessageCodes.Duplicate => text["EditorMatchMessage_Duplicate"].Value,
            _ => text["EditorMatchMessage_Unknown"].Value,
        };
    }
}
