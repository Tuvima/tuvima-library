using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Taste;

/// <summary>The values of the <c>group</c> field on taste actions and of a taste item's <c>source</c>.</summary>
public static class TasteValues
{
    public const string Genres = "genres";
    public const string Moods = "moods";
    public const string Eras = "eras";

    /// <summary>The Library worked this out from what the person watched, read and listened to.</summary>
    public const string SourceLearned = "learned";

    /// <summary>The person typed this themselves ("Added by you").</summary>
    public const string SourceAdded = "added";
}

/// <summary>One genre, mood or era in a person's taste.</summary>
public sealed record TasteItemDto(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("weight")] double? Weight,
    [property: JsonPropertyName("source")] string Source);

/// <summary>One person's taste with their removals and additions already applied.</summary>
public sealed class TastePersonDto
{
    [JsonPropertyName("profile_id")]
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>The person's name; present only when the scope covers other people.</summary>
    [JsonPropertyName("person")]
    public string? Person { get; set; }

    /// <summary>True for the signed-in person's own taste, the only taste that can be changed.</summary>
    [JsonPropertyName("is_you")]
    public bool IsYou { get; set; }

    [JsonPropertyName("genres")]
    public List<TasteItemDto> Genres { get; set; } = [];

    [JsonPropertyName("moods")]
    public List<TasteItemDto> Moods { get; set; } = [];

    [JsonPropertyName("eras")]
    public List<TasteItemDto> Eras { get; set; } = [];

    /// <summary>Share of each kind of media (for example Movies 0.4); empty until the Library has learned enough.</summary>
    [JsonPropertyName("media_mix")]
    public Dictionary<string, double> MediaMix { get; set; } = [];

    [JsonPropertyName("summary")]
    public string? Summary { get; set; }

    /// <summary>UTC. When the Library last worked this person's taste out; null when it has not yet.</summary>
    [JsonPropertyName("last_updated_at")]
    public string? LastUpdatedAt { get; set; }

    /// <summary>How many different titles this person has finished (songs and My list adds are not titles).</summary>
    [JsonPropertyName("finished_title_count")]
    public int FinishedTitleCount { get; set; }

    /// <summary>Titles finished in each of the last 12 months, oldest first, for "How your taste shifts". Months without a finished title are left out.</summary>
    [JsonPropertyName("shifts")]
    public List<TasteMonthDto> Shifts { get; set; } = [];
}

/// <summary>Titles first finished in one month (<c>yyyy-MM</c>, UTC) and how they split by genre; titles without a known genre are counted in <c>finished</c> only.</summary>
public sealed class TasteMonthDto
{
    [JsonPropertyName("month")]
    public string Month { get; set; } = string.Empty;

    [JsonPropertyName("finished")]
    public int Finished { get; set; }

    [JsonPropertyName("genres")]
    public Dictionary<string, int> Genres { get; set; } = [];
}

/// <summary>Known values offered while typing in an Add control.</summary>
public sealed class TasteSuggestionsDto
{
    [JsonPropertyName("genres")]
    public List<string> Genres { get; set; } = [];

    [JsonPropertyName("moods")]
    public List<string> Moods { get; set; } = [];

    [JsonPropertyName("eras")]
    public List<string> Eras { get; set; } = [];
}

/// <summary>The Taste page: everyone the scope covers, and the suggestions for the Add controls.</summary>
public sealed class TasteViewDto
{
    /// <summary>The scope that was answered: <c>me</c>, <c>profile:{id}</c>, <c>household:{id}</c> or <c>everyone</c>.</summary>
    [JsonPropertyName("scope")]
    public string Scope { get; set; } = "me";

    /// <summary>True when the scope covers anyone besides the signed-in person; viewing it is logged.</summary>
    [JsonPropertyName("includes_others")]
    public bool IncludesOthers { get; set; }

    [JsonPropertyName("people")]
    public List<TastePersonDto> People { get; set; } = [];

    [JsonPropertyName("suggestions")]
    public TasteSuggestionsDto Suggestions { get; set; } = new();
}

/// <summary>Adds one value to the signed-in person's own taste.</summary>
public sealed record AddTasteItemRequest(
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("value")] string Value);
