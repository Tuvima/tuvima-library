using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.Display;

/// <summary>
/// Body of <c>POST /api/v1/display/items</c>: item ids to turn into the same cards the lane browse pages return.
/// An id the viewer may not see is simply absent from the answer, exactly like an id that does not exist.
/// </summary>
public sealed record DisplayItemsRequest(
    [property: JsonPropertyName("ids")] IReadOnlyList<Guid> Ids)
{
    /// <summary>The most ids one call accepts. A longer list is answered with 400; the Dashboard splits it into calls of this size.</summary>
    public const int MaxIds = 200;
}
