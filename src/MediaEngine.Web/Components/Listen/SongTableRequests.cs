namespace MediaEngine.Web.Components.Listen;

/// <summary>What the song table hands its host when a song is played: the owned rows in the order shown, and the one to start from.</summary>
/// <param name="Rows">The rows as sorted on screen, without tracks the person does not own.</param>
/// <param name="Start">The row the person chose.</param>
public sealed record SongPlayRequest(IReadOnlyList<SongRow> Rows, SongRow Start);

/// <summary>
/// A playlist row the person moved (drag, Alt+Up or Alt+Down, or Move up and Move down on a phone). The indexes are places in the
/// rows the host gave the table: the row leaves <see cref="FromIndex"/> and ends up at <see cref="ToIndex"/> once moved. The host saves
/// the new order and hands the table the reordered rows.
/// </summary>
public sealed record SongReorderRequest(SongRow Row, int FromIndex, int ToIndex);
