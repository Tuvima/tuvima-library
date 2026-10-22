using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Providers.Services.StashBox;
using MediaEngine.Providers.Services.Vault;

namespace MediaEngine.Providers.Tests.Vault;

/// <summary>How strong a match is (Videos spec D30, D37): exact fingerprint, then code with length, then title with length. Anything else is Unmatched.</summary>
public sealed class VaultMatchRulesTests
{
    private static readonly VaultDurationToleranceConfiguration Tolerance = new();
    private static readonly AdultFingerprint Oshash = new(AdultFingerprintAlgorithms.Oshash, "8e245d9679d31e12");

    private static StashBoxScene Scene(
        string id, string title = "Example Scene One", string? code = "ABC-123", int? duration = 1800, DateOnly? date = null,
        params AdultFingerprint[] fingerprints) =>
        new(id, title, null, date, code, null, [], [], null) { DurationSeconds = duration, Fingerprints = fingerprints };

    private static AdultLookupQuery Query(string? title = "Example Scene One", string? code = null, double? duration = 1800, DateOnly? date = null) =>
        new(title, [], null, code, duration, date);

    private static VaultMatchEvidence Evidence(
        StashBoxScene[]? fingerprint = null, StashBoxScene[]? code = null, StashBoxScene[]? title = null, bool sentExact = true) =>
        new(sentExact ? [Oshash] : [], fingerprint ?? [], code ?? [], title ?? []);

    [Fact]
    public void Decide_AnExactFingerprint_IsAcceptedWhateverTheLength()
    {
        var scene = Scene("a", duration: 60, fingerprints: Oshash);

        var decision = VaultMatchRules.Decide(Query(), Evidence(fingerprint: [scene]), Tolerance);

        Assert.Equal(VaultMatchVerdict.Accepted, decision.Verdict);
        Assert.Equal(AdultMatchSignals.Fingerprint, decision.Signal);
        Assert.Equal("a", decision.Scene!.Id);
    }

    [Fact]
    public void Decide_ARecordThatListsOtherFingerprints_IsNotAnExactCopy()
    {
        var scene = Scene("a", fingerprints: new AdultFingerprint(AdultFingerprintAlgorithms.Oshash, "0000000000000000"));

        var decision = VaultMatchRules.Decide(Query(title: null), Evidence(fingerprint: [scene]), Tolerance);

        Assert.Equal(VaultMatchVerdict.Weak, decision.Verdict);
        Assert.Null(decision.Scene);
    }

    [Fact]
    public void Decide_APerceptualHashAlone_IsNeverAnExactCopy()
    {
        var scene = Scene("a");
        var evidence = new VaultMatchEvidence([new AdultFingerprint(AdultFingerprintAlgorithms.Phash, "ffffffffffffffff")], [scene], [], []);

        Assert.Equal(VaultMatchVerdict.Weak, VaultMatchRules.Decide(Query(title: null), evidence, Tolerance).Verdict);
    }

    [Fact]
    public void Decide_AProductCodeWithAnAgreeingLength_IsAccepted()
    {
        var decision = VaultMatchRules.Decide(Query(code: "ABC-123", duration: 1803), Evidence(code: [Scene("a", duration: 1800)]), Tolerance);

        Assert.Equal(VaultMatchVerdict.Accepted, decision.Verdict);
        Assert.Equal(AdultMatchSignals.ProductCode, decision.Signal);
    }

    [Fact]
    public void Decide_AProductCodeWhoseLengthDisagrees_StaysUnmatched()
    {
        var decision = VaultMatchRules.Decide(Query(code: "ABC-123", duration: 600), Evidence(code: [Scene("a", duration: 1800)]), Tolerance);

        Assert.Equal(VaultMatchVerdict.Weak, decision.Verdict);
        Assert.Null(decision.Scene);
    }

    [Fact]
    public void Decide_AProductCodeWhoseDateDisagrees_StaysUnmatched()
    {
        var decision = VaultMatchRules.Decide(
            Query(code: "ABC-123", date: new DateOnly(2020, 1, 1)),
            Evidence(code: [Scene("a", date: new DateOnly(2021, 3, 4))]),
            Tolerance);

        Assert.Equal(VaultMatchVerdict.Weak, decision.Verdict);
    }

    [Fact]
    public void Decide_ACodeThatDisagreesWithTheFingerprint_IsAConflictAndNothingIsApplied()
    {
        var byFingerprint = Scene("a", code: "XYZ-999", fingerprints: Oshash);

        var decision = VaultMatchRules.Decide(Query(code: "ABC-123"), Evidence(fingerprint: [byFingerprint]), Tolerance);

        Assert.Equal(VaultMatchVerdict.Conflict, decision.Verdict);
        Assert.Null(decision.Scene);
    }

    [Fact]
    public void Decide_AFingerprintRecordAndADifferentCodeRecord_IsAConflict()
    {
        var decision = VaultMatchRules.Decide(
            Query(code: "ABC-123"),
            Evidence(fingerprint: [Scene("a", code: null, fingerprints: Oshash)], code: [Scene("b")]),
            Tolerance);

        Assert.Equal(VaultMatchVerdict.Conflict, decision.Verdict);
    }

    [Fact]
    public void Decide_TheSameRecordByFingerprintAndCode_IsAcceptedByTheFingerprint()
    {
        var scene = Scene("a", fingerprints: Oshash);

        var decision = VaultMatchRules.Decide(Query(code: "abc123"), Evidence(fingerprint: [scene], code: [scene]), Tolerance);

        Assert.Equal(VaultMatchVerdict.Accepted, decision.Verdict);
        Assert.Equal(AdultMatchSignals.Fingerprint, decision.Signal);
    }

    [Fact]
    public void Decide_TwoRecordsWithTheSameCodeAndLength_IsAConflict()
    {
        var decision = VaultMatchRules.Decide(
            Query(code: "ABC-123"), Evidence(code: [Scene("a", duration: 1800), Scene("b", duration: 1802)]), Tolerance);

        Assert.Equal(VaultMatchVerdict.Conflict, decision.Verdict);
    }

    [Fact]
    public void Decide_ATitleThatIsEqualWithAnAgreeingLength_IsAccepted()
    {
        var decision = VaultMatchRules.Decide(Query(title: "example scene one!"), Evidence(title: [Scene("a")], sentExact: false), Tolerance);

        Assert.Equal(VaultMatchVerdict.Accepted, decision.Verdict);
        Assert.Equal(AdultMatchSignals.Title, decision.Signal);
    }

    [Fact]
    public void Decide_ATitleAloneWithNoKnownLength_IsWeak()
    {
        var decision = VaultMatchRules.Decide(Query(duration: null), Evidence(title: [Scene("a")], sentExact: false), Tolerance);

        Assert.Equal(VaultMatchVerdict.Weak, decision.Verdict);
    }

    [Fact]
    public void Decide_ASimilarTitleOnly_IsWeak()
    {
        var decision = VaultMatchRules.Decide(
            Query(title: "Example Scene"), Evidence(title: [Scene("a", title: "Example Scene One")], sentExact: false), Tolerance);

        Assert.Equal(VaultMatchVerdict.Weak, decision.Verdict);
    }

    [Fact]
    public void Decide_NothingFound_HasNoCandidate()
    {
        Assert.Equal(VaultMatchVerdict.NoCandidate, VaultMatchRules.Decide(Query(), VaultMatchEvidence.Empty, Tolerance).Verdict);
    }

    [Theory]
    [InlineData(1800, 1826, true)]
    [InlineData(1800, 1830, false)]
    [InlineData(60, 64, true)]
    [InlineData(60, 66, false)]
    [InlineData(null, 1800, false)]
    public void DurationAgrees_UsesTheLargerOfTheSecondsAndPercentTolerances(int? file, int record, bool expected)
    {
        Assert.Equal(expected, VaultMatchRules.DurationAgrees(file, record, Tolerance));
    }
}
