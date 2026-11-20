namespace MediaEngine.Web.Components.View;

/// <summary>One choice in <see cref="ViewPickDialog"/>: a face, and the name under it when there is one.</summary>
public sealed record ViewPickItem(Guid Id, Guid? FaceId, string? Label);
