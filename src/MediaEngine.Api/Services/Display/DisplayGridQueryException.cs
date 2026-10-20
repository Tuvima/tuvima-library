namespace MediaEngine.Api.Services.Display;

/// <summary>
/// The "View all" request asked for a sort or filter value the grids do not offer. Thrown only by the grid sort and
/// filter validation, so the connection point can answer 400 for exactly this and let any other fault surface as one.
/// </summary>
internal sealed class DisplayGridQueryException(string message) : Exception(message);
