namespace MediaEngine.Web.Models.ViewDTOs;

public sealed record ItemEditorPreferencesSaveResultDto(
    bool Saved,
    bool Conflict,
    MediaEngine.Contracts.Items.ItemEditorPreferencesResponse? Preferences,
    string? Error);
