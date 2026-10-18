using System.Text.Json.Serialization;

namespace MediaEngine.Contracts.System;

/// <summary>
/// The latest upgrade step applied to the data store. <c>StepNumber</c> 0 with no <c>AppliedAt</c>
/// means no step has been recorded yet (a brand-new install still setting up).
/// </summary>
public sealed record DataStoreVersionDto(
    [property: JsonPropertyName("step_number")] int StepNumber,
    [property: JsonPropertyName("step_name")] string StepName,
    [property: JsonPropertyName("applied_at")] DateTimeOffset? AppliedAt);
