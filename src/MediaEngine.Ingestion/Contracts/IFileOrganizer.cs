using MediaEngine.Ingestion.Models;

namespace MediaEngine.Ingestion.Contracts;

/// <summary>
/// Defines methods for calculating destination paths and executing file moves.
/// Spec: Phase 7 – Interfaces § IFileOrganizer.
///
/// MUST NOT act unless "Auto-Organize" is explicitly enabled in settings.
/// Spec: "The system MUST NOT move, rename, or modify files unless the
///        'Auto-Organize' or 'Write-Back' features are explicitly enabled."
/// </summary>
public interface IFileOrganizer
{
    /// <summary>
    /// Evaluates the tokenized <paramref name="template"/> against the candidate's
    /// resolved metadata and returns the intended destination path.
    ///
    /// Supported tokens are registered at runtime by media processors.
    /// Examples: <c>{Author}/{Series}/{Title}</c>, <c>{Year}/{MediaType}/{Title}</c>.
    /// Spec: Phase 7 – Extension Points § Organization Templates.
    /// </summary>
    string CalculatePath(IngestionCandidate candidate, string template);

    /// <summary>
    /// Resolves the template against sample tokens and validates the resulting path.
    /// Returns the resolved sample path string, or <see langword="null"/> if validation fails
    /// (with <paramref name="error"/> explaining the failure).
    /// </summary>
    string? ValidateTemplate(string template, out string? error);

    /// <summary>
    /// Moves (or copies-then-deletes) the file from <paramref name="sourcePath"/>
    /// to <paramref name="destinationPath"/>.
    ///
    /// Collision handling: MUST append a unique suffix rather than overwriting.
    /// Spec: "If a naming conflict occurs … MUST append a unique suffix."
    ///
    /// Lock handling: MUST retry with exponential backoff on <see cref="IOException"/>.
    ///
    /// Returns the path the file really ended up at (the requested one, or the one with a unique suffix when the
    /// requested name was taken), or <see langword="null"/> when the file was not moved. Callers record that path,
    /// never the requested one.
    /// </summary>
    Task<string?> ExecuteMoveAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct = default);
}
