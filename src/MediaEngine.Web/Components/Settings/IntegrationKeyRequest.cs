namespace MediaEngine.Web.Components.Settings;

/// <summary>What the Generate key window collects: a name and when the key expires (<c>year</c>, <c>quarter</c> or <c>never</c>).</summary>
public sealed record IntegrationKeyRequest(string Name, string Expiry);
