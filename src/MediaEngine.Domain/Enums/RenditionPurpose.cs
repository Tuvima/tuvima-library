namespace MediaEngine.Domain.Enums;

/// <summary>
/// Describes why a physical asset exists. This is delivery metadata and never
/// participates in Work or Edition identity.
/// </summary>
public enum RenditionPurpose
{
    Original = 0,
    Mobile = 1,
    Offline = 2,
    Compatibility = 3,
    Other = 4,
}
