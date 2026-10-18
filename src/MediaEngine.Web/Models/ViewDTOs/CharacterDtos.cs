namespace MediaEngine.Web.Models.ViewDTOs;

// ── Universe Health ───────────────────────────────────────────────────────────

/// <summary>
/// Health score for a fictional universe: how well enriched the entity graph is.
/// Returned by GET /universe/{qid}/health.
/// </summary>
public sealed class UniverseHealthDto
{
    public string Qid { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int EntitiesTotal { get; set; }
    public int EntitiesEnriched { get; set; }
    public int EntitiesWithImages { get; set; }
    public int RelationshipsTotal { get; set; }
    public double HealthPercent { get; set; }
}
