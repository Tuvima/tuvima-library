using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services.StashBox;

namespace MediaEngine.Providers.Services.Vault;

public enum VaultMatchVerdict
{
    /// <summary>No record was found by any signal.</summary>
    NoCandidate,

    /// <summary>A record was found, but only by a weak signal (a name alone, or a code whose length does not agree). It is not applied.</summary>
    Weak,

    /// <summary>Signals point at different records, or two records fit equally well. Nothing is applied.</summary>
    Conflict,

    /// <summary>One record, by a strong signal. It may be applied.</summary>
    Accepted,
}

public sealed record VaultMatchDecision(VaultMatchVerdict Verdict, StashBoxScene? Scene, string? Signal)
{
    public static VaultMatchDecision None { get; } = new(VaultMatchVerdict.NoCandidate, null, null);
}

/// <summary>What the servers answered for each signal. Empty lists for signals that were not tried.</summary>
/// <param name="ExactFingerprints">The exact-copy fingerprints (OSHASH, MD5) that were sent.</param>
/// <param name="FingerprintScenes">Records the server returned for those fingerprints.</param>
/// <param name="CodeScenes">Records a search for the product code returned.</param>
/// <param name="TitleScenes">Records a search for the title returned.</param>
public sealed record VaultMatchEvidence(
    IReadOnlyList<AdultFingerprint> ExactFingerprints,
    IReadOnlyList<StashBoxScene> FingerprintScenes,
    IReadOnlyList<StashBoxScene> CodeScenes,
    IReadOnlyList<StashBoxScene> TitleScenes)
{
    public static VaultMatchEvidence Empty { get; } = new([], [], [], []);
}

/// <summary>
/// How strong a match is (Videos spec D30, D37). Strongest first: an exact fingerprint; a product code whose record agrees with the
/// file's length (and date, when both are known); the same title whose record agrees with the length (and date). A name alone is weak
/// and is never applied. Signals that point at different records, or two records that fit equally well, are a conflict and nothing is
/// applied. Pure: no network, no storage.
/// </summary>
public static class VaultMatchRules
{
    public static VaultMatchDecision Decide(AdultLookupQuery query, VaultMatchEvidence evidence, VaultDurationToleranceConfiguration tolerance)
    {
        var strong = new List<(StashBoxScene Scene, string Signal)>();
        var weak = false;

        // 1. Fingerprints. An exact copy is a strong match whatever the length.
        var byFingerprint = evidence.FingerprintScenes.DistinctBy(s => s.Id).ToList();
        if (byFingerprint.Count > 1)
        {
            return new VaultMatchDecision(VaultMatchVerdict.Conflict, null, null);
        }

        if (byFingerprint.Count == 1)
        {
            var scene = byFingerprint[0];
            if (ConfirmsExactCopy(scene, evidence.ExactFingerprints))
            {
                // A code in the file's name that belongs to a different record than the fingerprint is a disagreement.
                if (!string.IsNullOrWhiteSpace(query.ProductCode) && !string.IsNullOrWhiteSpace(scene.Code)
                    && !VaultFileNameReader.SameCode(scene.Code, query.ProductCode))
                {
                    return new VaultMatchDecision(VaultMatchVerdict.Conflict, null, null);
                }

                strong.Add((scene, AdultMatchSignals.Fingerprint));
            }
            else
            {
                weak = true;
            }
        }

        // 2. Product code, only strong when the length (and date) agree.
        if (!string.IsNullOrWhiteSpace(query.ProductCode))
        {
            var sameCode = evidence.CodeScenes
                .Where(s => VaultFileNameReader.SameCode(s.Code, query.ProductCode))
                .DistinctBy(s => s.Id)
                .ToList();
            var agreeing = sameCode.Where(s => Agrees(query, s, tolerance)).ToList();
            if (agreeing.Count > 1)
            {
                return new VaultMatchDecision(VaultMatchVerdict.Conflict, null, null);
            }

            if (agreeing.Count == 1)
            {
                strong.Add((agreeing[0], AdultMatchSignals.ProductCode));
            }
            else if (sameCode.Count > 0)
            {
                weak = true;
            }
        }

        // 3. Title, only consulted when nothing stronger decided, and only strong when the length (and date) agree.
        if (strong.Count == 0 && Normalise(query.Title) is { Length: > 0 } wanted)
        {
            var sameTitle = evidence.TitleScenes.Where(s => Normalise(s.Title) == wanted).DistinctBy(s => s.Id).ToList();
            var agreeing = sameTitle.Where(s => Agrees(query, s, tolerance)).ToList();
            if (agreeing.Count == 1)
            {
                strong.Add((agreeing[0], AdultMatchSignals.Title));
            }
            else if (sameTitle.Count > 0 || evidence.TitleScenes.Count > 0)
            {
                weak = true;
            }
        }

        var distinct = strong.Select(s => s.Scene.Id).Distinct(StringComparer.Ordinal).Count();
        if (distinct > 1)
        {
            return new VaultMatchDecision(VaultMatchVerdict.Conflict, null, null);
        }

        if (distinct == 1)
        {
            var best = strong.OrderBy(s => Rank(s.Signal)).First();
            return new VaultMatchDecision(VaultMatchVerdict.Accepted, best.Scene, best.Signal);
        }

        return weak ? new VaultMatchDecision(VaultMatchVerdict.Weak, null, null) : VaultMatchDecision.None;
    }

    /// <summary>The record's length agrees with the file's (needed), and so does its date when both sides have one.</summary>
    public static bool Agrees(AdultLookupQuery query, StashBoxScene scene, VaultDurationToleranceConfiguration tolerance) =>
        DurationAgrees(query.DurationSeconds, scene.DurationSeconds, tolerance) && DateAgrees(query.Date, scene.Date);

    public static bool DurationAgrees(double? file, int? record, VaultDurationToleranceConfiguration tolerance)
    {
        if (!VaultFieldValidator.IsSensibleDuration(file) || record is not > 0)
        {
            return false;
        }

        var longer = Math.Max(file!.Value, record.Value);
        var allowed = Math.Max(tolerance.Seconds, longer * tolerance.Percent / 100.0);
        return Math.Abs(file.Value - record.Value) <= allowed;
    }

    public static bool DateAgrees(DateOnly? file, DateOnly? record) => file is null || record is null || file == record;

    private static bool ConfirmsExactCopy(StashBoxScene scene, IReadOnlyList<AdultFingerprint> sent)
    {
        // A record that lists its fingerprints must list one of the exact ones sent. One that lists none is trusted as the server's
        // own answer to an exact-hash question.
        var exact = sent.Where(f => f.Algorithm is AdultFingerprintAlgorithms.Oshash or AdultFingerprintAlgorithms.Md5).ToList();
        if (exact.Count == 0)
        {
            return false;
        }

        return scene.Fingerprints.Count == 0
            || scene.Fingerprints.Any(f => exact.Any(e =>
                e.Algorithm == f.Algorithm && string.Equals(e.Hash, f.Hash, StringComparison.OrdinalIgnoreCase)));
    }

    private static int Rank(string signal) => signal switch
    {
        AdultMatchSignals.Fingerprint => 0,
        AdultMatchSignals.ProductCode => 1,
        _ => 2,
    };

    /// <summary>Letters and digits only, lower case, so "Example Scene One!" and "example scene one" compare equal.</summary>
    public static string Normalise(string? text) =>
        new((text ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
