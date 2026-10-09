using MediaEngine.Contracts.Metadata;

namespace MediaEngine.Web.Components.MediaEditor;

/// <summary>Explicit choices for one immutable server-reviewed TV selection.</summary>
public sealed class MediaEditorPairingReviewState
{
    private readonly Dictionary<Guid, PairingDecision> _decisions = [];
    private readonly Dictionary<Guid, Dictionary<string, MediaEditorPairingChildSearchItemDto>> _searchedCandidates = [];
    private string? _reviewToken;
    private string _operationToken = Guid.NewGuid().ToString("D");

    public int DecidedCount => _decisions.Count;
    public int AcceptedCount => _decisions.Values.Count(decision => decision.CandidateId is not null);
    public int ExcludedCount => _decisions.Values.Count(decision => decision.Excluded);

    public void Begin(MediaEditorPairingPreviewDto preview)
    {
        _decisions.Clear();
        _searchedCandidates.Clear();
        _reviewToken = preview.ReviewToken;
        _operationToken = Guid.NewGuid().ToString("D");
    }

    public bool IsAccepted(Guid assetId) =>
        _decisions.TryGetValue(assetId, out var decision) && decision.CandidateId is not null;

    public bool IsAccepted(Guid assetId, string candidateId) =>
        _decisions.TryGetValue(assetId, out var decision)
        && string.Equals(decision.CandidateId, candidateId, StringComparison.Ordinal);

    public string? AcceptedCandidateId(Guid assetId) =>
        _decisions.TryGetValue(assetId, out var decision) ? decision.CandidateId : null;

    public bool IsExcluded(Guid assetId) =>
        _decisions.TryGetValue(assetId, out var decision) && decision.Excluded;

    public bool Accept(MediaEditorPairingRowDto row, MediaEditorPairingCandidateDto candidate)
    {
        if (!candidate.CanSave || !Candidates(row).Any(option => option.Child.ChildId == candidate.Child.ChildId))
        {
            return false;
        }
        Set(row.AssetId, new PairingDecision(candidate.Child.ChildId, false));
        return true;
    }

    public void RegisterSearchedCandidates(MediaEditorPairingRowDto row, IEnumerable<MediaEditorPairingChildSearchItemDto> candidates)
    {
        if (!_searchedCandidates.TryGetValue(row.AssetId, out var choices))
        {
            _searchedCandidates[row.AssetId] = choices = new Dictionary<string, MediaEditorPairingChildSearchItemDto>(StringComparer.Ordinal);
        }
        foreach (var candidate in candidates)
        {
            choices[candidate.Child.ChildId] = candidate;
        }
    }

    public bool AcceptSearched(MediaEditorPairingRowDto row, MediaEditorPairingChildSearchItemDto candidate)
    {
        if (!candidate.CanSave || !_searchedCandidates.TryGetValue(row.AssetId, out var choices)
            || !choices.TryGetValue(candidate.Child.ChildId, out var registered) || !registered.CanSave)
        {
            return false;
        }
        Set(row.AssetId, new PairingDecision(candidate.Child.ChildId, false));
        return true;
    }

    public MediaEditorPairingChildDto? SearchedAcceptedChild(Guid assetId)
    {
        var candidateId = AcceptedCandidateId(assetId);
        return candidateId is not null && _searchedCandidates.TryGetValue(assetId, out var choices)
            && choices.TryGetValue(candidateId, out var candidate) ? candidate.Child : null;
    }

    public void Exclude(Guid assetId) => Set(assetId, new PairingDecision(null, true));

    public void Clear(Guid assetId)
    {
        if (_decisions.Remove(assetId))
        {
            _operationToken = Guid.NewGuid().ToString("D");
        }
    }

    public bool CanSubmit(MediaEditorPairingPreviewDto? preview, DateTimeOffset now) =>
        preview is { ReviewToken: not null }
        && preview.MediaKind is "tv_episode" or "music_release_track"
        && preview.ReviewToken == _reviewToken
        && (preview.ReviewExpiresAt is null || preview.ReviewExpiresAt > now)
        && preview.Rows.Count > 0
        && AcceptedCount > 0
        && _decisions.Count == preview.Rows.Count
        && preview.Rows.All(row => _decisions.TryGetValue(row.AssetId, out var decision)
            && (decision.Excluded || Candidates(row).Any(candidate => candidate.CanSave
                && decision.CandidateId == candidate.Child.ChildId)
                || _searchedCandidates.TryGetValue(row.AssetId, out var choices)
                && decision.CandidateId is not null
                && choices.TryGetValue(decision.CandidateId, out var searched) && searched.CanSave));

    public MediaEditorPairingSaveRequestDto? BuildRequest(MediaEditorPairingPreviewDto? preview, DateTimeOffset now)
    {
        if (!CanSubmit(preview, now))
        {
            return null;
        }
        return new MediaEditorPairingSaveRequestDto(
            _reviewToken!, _operationToken,
            _decisions.Where(pair => pair.Value.CandidateId is not null)
                .Select(pair => new MediaEditorPairingAcceptedDto(pair.Key, pair.Value.CandidateId!))
                .OrderBy(item => item.AssetId).ToArray(),
            _decisions.Where(pair => pair.Value.Excluded).Select(pair => pair.Key)
                .OrderBy(id => id).ToArray());
    }

    private void Set(Guid assetId, PairingDecision decision)
    {
        if (_decisions.TryGetValue(assetId, out var current) && current == decision)
        {
            return;
        }
        _decisions[assetId] = decision;
        _operationToken = Guid.NewGuid().ToString("D");
    }

    private static IEnumerable<MediaEditorPairingCandidateDto> Candidates(MediaEditorPairingRowDto row)
    {
        if (row.Proposed is not null)
        {
            yield return row.Proposed;
        }
        foreach (var alternative in row.Alternatives)
        {
            yield return alternative;
        }
    }

    private sealed record PairingDecision(string? CandidateId, bool Excluded);
}
