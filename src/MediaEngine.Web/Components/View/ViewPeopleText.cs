namespace MediaEngine.Web.Components.View;

/// <summary>The words on the People pages and their dialogs (spec view-people-search, WP9), in one place like <see cref="EmptyTrashText"/>.</summary>
public static class ViewPeopleText
{
    public const string WhosThis = "Who's this?";
    public const string WhosThisHint = "Name a face and Library finds the rest.";
    public const string NamePrompt = "Name this person";
    public const string NameLabel = "Name";
    public const string NameButton = "Save name";
    public const string Skip = "Skip";
    public const string Yes = "Yes";
    public const string Cancel = "Cancel";
    public const string Done = "Done";
    public const string Rename = "Rename";
    public const string MergeWith = "Merge with…";
    public const string ChooseCover = "Choose cover photo";
    public const string Hide = "Hide this person";
    public const string Unhide = "Show this person";
    public const string RemoveEverywhere = "Remove from all photos…";
    public const string ShowHidden = "Show hidden people";
    public const string PickMergeTitle = "Merge with which person?";
    public const string PickCoverTitle = "Choose a cover photo";
    public const string NoOthers = "There is nobody else to merge with yet.";
    public const string EmptyTitle = "No people yet";
    public const string EmptyLooking = "Library is looking through your photos. People appear here as faces are found.";
    public const string OffTitle = "Face recognition is off for your photos";
    public const string OffMessage = "Switch it on and Library groups the faces in your photos. Nothing leaves your computer.";
    public const string SwitchOn = "Turn on";
    public const string GettingTools = "Getting the face tools. This happens once and the progress is in Tools.";
    public const string CouldNotSave = "That change could not be saved. Try again.";
    public const string Unnamed = "Not named yet";
    public const string NameThisFace = "Name this face";
    public const string OpenInPlaces = "Open Places";

    public const string SettingsTitle = "Face recognition";
    public const string RecognizeLabel = "Recognize people in my photos";
    public const string RecognizeHelp = "Library looks for faces in your own photos on this computer and groups them. It never looks in the Vault or at anyone else's photos, and nothing leaves your computer.";
    public const string DeleteAll = "Delete all face data";
    public const string DeleteAllTitle = "Delete all face data?";
    public const string DeleteAllMessage = "Every name, group and face position is removed. Your photos are not changed. If recognition stays on, Library starts again from the beginning.";
    public static string DataSize(int faces, int people) => $"{faces:N0} faces found, {people:N0} named {(people == 1 ? "person" : "people")}";
    public static string NotThem(string name) => $"Not {name}";
    public static string IsThis(string name) => $"Is this {name}?";
    public static string IsThisHint(string name) => $"Untick any face that is someone else, then choose Yes.";
    public static string Photos(int count) => count == 1 ? "1 photo" : $"{count:N0} photos";
    public static string Looking(int looked, int total) => $"Looking at {looked:N0} of {total:N0} photos";
    public static string RemoveTitle(string name) => $"Remove {name} from all photos?";
    public const string RemoveMessage = "The name and its faces are forgotten. Your photos are not changed, and Library may group these faces again later.";
    public const string Remove = "Remove";
}
