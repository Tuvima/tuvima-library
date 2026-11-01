using MediaEngine.Contracts.Import;

namespace MediaEngine.Web.Models.ViewDTOs;

/// <summary>
/// A file the shared Import has read: the preview the Engine returned for its kind, plus the file itself, kept in memory so the
/// step that uses it (making a collection, adding a bookmark pack) can send it back without asking the person to choose it again.
/// The file never leaves this machine.
/// </summary>
/// <param name="FileName">The file's name only.</param>
/// <param name="ContentBase64">The file's bytes, or null when the text was pasted.</param>
/// <param name="Text">Pasted text, or null when a file was chosen.</param>
/// <param name="Preview">What the Engine recognised the file as, in that kind's own words.</param>
public sealed record ImportRecognition(string FileName, string? ContentBase64, string? Text, ImportPreviewDto Preview);
