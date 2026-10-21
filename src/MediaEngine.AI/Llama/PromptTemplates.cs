namespace MediaEngine.AI.Llama;

/// <summary>
/// System prompts and GBNF grammars for all AI-powered features.
/// Grammars are model-agnostic — they constrain token generation via llama.cpp,
/// working identically with Llama, Mistral, Phi, Gemma, Qwen, etc.
/// </summary>
public static class PromptTemplates
{
    // ── Smart Labeling (Feature 1) ────────────────────────────────────────

    public const string SmartLabelingSystem = """
        You extract clean metadata from media filenames. Rules:
        - Do NOT remove years that are part of the title (e.g. "2001 A Space Odyssey")
        - Extract author names if present (e.g. "Frank Herbert - Dune")
        - Remove quality tags, release groups, encoding info (BluRay, x264, FLUX, etc.)
        - Detect TV season/episode patterns (S01E02, 1x03)
        - Return ONLY valid JSON matching the grammar
        """;

    public const string SmartLabelingGrammar = """
        root   ::= "{" ws "\"title\"" ws ":" ws string ws title-rest "}"
        title-rest ::= "," ws "\"author\"" ws ":" ws (string | "null") ws "," ws "\"year\"" ws ":" ws (number | "null") ws "," ws "\"season\"" ws ":" ws (number | "null") ws "," ws "\"episode\"" ws ":" ws (number | "null") ws "," ws "\"confidence\"" ws ":" ws number
        string ::= "\"" ([^"\\] | "\\" .)* "\""
        number ::= [0-9]+ ("." [0-9]+)?
        ws     ::= [ \t\n]*
        """;

    public static string SmartLabelingPrompt(string filename) =>
        $"{SmartLabelingSystem}\n\nFilename: {filename}";

    // ── Media Type Classification (Feature 2) ──────────────────────────────

    public const string MediaTypeSystem = """
        You classify media files into exactly one type based on their metadata.
        Types: Book, Audiobook, Movie, TV, Music, Comic
        Consider ALL signals: filename, duration, container format, genre, folder path, bitrate, chapters.
        Return ONLY valid JSON matching the grammar.
        """;

    public const string MediaTypeGrammar = """
        root   ::= "{" ws "\"type\"" ws ":" ws type-value ws "," ws "\"confidence\"" ws ":" ws number ws "," ws "\"reason\"" ws ":" ws string ws "}"
        type-value ::= "\"Book\"" | "\"Audiobook\"" | "\"Movie\"" | "\"TV\"" | "\"Music\"" | "\"Comic\""
        string ::= "\"" ([^"\\] | "\\" .)* "\""
        number ::= [0-9]+ ("." [0-9]+)?
        ws     ::= [ \t\n]*
        """;

    public static string MediaTypePrompt(
        string filename,
        string? container,
        double? durationSec,
        int? bitrate,
        string? genre,
        bool hasChapters,
        string? folderPath) =>
        $"""
        {MediaTypeSystem}

        Filename: {filename}
        Container: {container ?? "unknown"}
        Duration: {(durationSec.HasValue ? $"{durationSec:F0} seconds" : "unknown")}
        Bitrate: {(bitrate.HasValue ? $"{bitrate} kbps" : "unknown")}
        Genre tag: {genre ?? "none"}
        Has chapters: {(hasChapters ? "yes" : "no")}
        Folder path: {folderPath ?? "unknown"}
        """;

    // ── Series Position (Feature 6) ───────────────────────────────────────

    public const string SeriesPositionSystem = """
        You determine a work's position within a series.
        Given the work title, series name, and sibling titles already in the series,
        determine what position number this work should have (1-based).
        If you cannot determine the position, set position to null.
        Return ONLY valid JSON matching the grammar.
        """;

    public const string SeriesPositionGrammar = """
        root   ::= "{" ws "\"position\"" ws ":" ws (number | "null") ws "," ws "\"confidence\"" ws ":" ws number ws "}"
        number ::= [0-9]+
        ws     ::= [ \t\n]*
        """;

    public static string SeriesPositionPrompt(
        string workTitle,
        string seriesName,
        IReadOnlyList<string> siblingTitles)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(SeriesPositionSystem);
        sb.AppendLine();
        sb.AppendLine($"Series: {seriesName}");
        sb.AppendLine($"Work to position: {workTitle}");
        if (siblingTitles.Count > 0)
        {
            sb.AppendLine("Existing titles in series:");
            for (int i = 0; i < siblingTitles.Count; i++)
            {
                sb.AppendLine($"  {i + 1}. {siblingTitles[i]}");
            }
        }
        return sb.ToString();
    }

    // ── Description Intelligence ──────────────────────────────────────────────

    /// <summary>GBNF grammar for Description Intelligence Pass 1: vocabulary extraction (no people).</summary>
    public const string DescriptionIntelligenceGrammar = """
        root ::= "{" ws "\"themes\"" ws ":" ws sarr ws "," ws "\"mood\"" ws ":" ws sarr ws "," ws "\"setting\"" ws ":" ws nstr ws "," ws "\"time_period\"" ws ":" ws nstr ws "," ws "\"audience\"" ws ":" ws nstr ws "," ws "\"content_warnings\"" ws ":" ws sarr ws "," ws "\"pace\"" ws ":" ws nstr ws "," ws "\"tldr\"" ws ":" ws str ws "}"
        sarr ::= "[" ws (str ("," ws str)*)? ws "]"
        nstr ::= str | "null"
        str ::= "\"" ([^"\\] | "\\" .)* "\""
        ws ::= [ \t\n]*
        """;

    /// <summary>GBNF grammar for Description Intelligence Pass 2: people extraction.</summary>
    public const string DescriptionIntelligencePeopleGrammar = """
        root ::= "{" ws "\"people\"" ws ":" ws people ws "}"
        people ::= "[" ws (person ("," ws person)*)? ws "]"
        person ::= "{" ws "\"name\"" ws ":" ws str ws "," ws "\"role\"" ws ":" ws role ws "}"
        role ::= "\"narrator\"" | "\"translator\"" | "\"editor\"" | "\"illustrator\"" | "\"director\"" | "\"cast\"" | "\"host\"" | "\"producer\"" | "\"author\""
        str ::= "\"" ([^"\\] | "\\" .)* "\""
        ws ::= [ \t\n]*
        """;

    /// <summary>Build the Description Intelligence prompt.</summary>
    public static string DescriptionIntelligencePrompt(
        string title,
        string mediaCategory,
        IReadOnlyList<string> moodVocabulary,
        string combinedDescriptions)
    {
        var moodList = string.Join(", ", moodVocabulary);
        return $"""
            Analyze "{title}" ({mediaCategory}). Extract:
            - themes: 3-5 key themes (e.g. "survival", "identity", "ecology")
            - mood: 2-3 from ONLY: [{moodList}]
            - setting: primary location/world (null if unclear)
            - time_period: when it takes place (null if unclear)
            - audience: adult, young-adult, children, all-ages, or null
            - content_warnings: [] if none
            - pace: slow-burn, fast-paced, moderate, varied, or null
            - tldr: one punchy sentence, no spoilers

            JSON only. No text outside JSON.

            {combinedDescriptions}
            """;
    }

    /// <summary>Build the Description Intelligence Pass 2 prompt (people extraction).</summary>
    public static string DescriptionIntelligencePeoplePrompt(
        string title,
        string combinedDescriptions)
    {
        return $"""
            Extract real person names from this description of "{title}".
            Look for: "Read by X", "Narrated by X", "Translated by X", "Written by X", "Directed by X", "Starring X".
            Only include real people (first + last name). Return JSON only.

            {combinedDescriptions}
            """;
    }
}
