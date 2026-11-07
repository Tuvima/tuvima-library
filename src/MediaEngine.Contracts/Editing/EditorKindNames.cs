namespace MediaEngine.Contracts.Editing;

/// <summary>
/// The ids of the kinds of item the editor's header describes (<see cref="EditorHeaderDto.Kind"/>, <see cref="EditorHeaderConfigDto.Kind"/>).
/// They are wire values, so they live here and the Dashboard reads them from here; the Engine keeps the same ids for its own
/// <c>config/editor.json</c> keys, and a Contracts test keeps the two lists equal.
/// </summary>
public static class EditorKindNames
{
    public const string Item = "item";
    public const string TvShow = "tv_show";
    public const string TvSeason = "tv_season";
    public const string TvEpisode = "tv_episode";
    public const string Movie = "movie";
    public const string Album = "album";
    public const string Song = "song";
    public const string Book = "book";
    public const string Audiobook = "audiobook";
    public const string Comic = "comic";
    public const string Series = "series";
    public const string Course = "course";
    public const string CourseSection = "course_section";
    public const string Video = "video";
    public const string Person = "person";
    public const string SupportingFile = "supporting_file";
    public const string Universe = "universe";
    public const string ViewItem = "view_item";
    public const string ViewGallery = "view_gallery";
    public const string Multiple = "multiple";

    public static readonly IReadOnlyList<string> All =
    [
        Item, TvShow, TvSeason, TvEpisode, Movie, Album, Song, Book, Audiobook, Comic, Series, Course, CourseSection, Video, Person,
        SupportingFile, Universe, ViewItem, ViewGallery, Multiple,
    ];
}
