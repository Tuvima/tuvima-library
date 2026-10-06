namespace MediaEngine.Web.Services.Ui;

/// <summary>Circuit-local host registration; browser positioning selects the active modal or fullscreen host.</summary>
public sealed class AppPopoverService
{
    private readonly HashSet<string> _hosts = [];
    public string? HostId => _hosts.FirstOrDefault();
    public IDisposable RegisterHost(string id) { _hosts.Add(id); return new Registration(_hosts, id); }
    private sealed class Registration(HashSet<string> hosts, string id) : IDisposable { public void Dispose() => hosts.Remove(id); }
}
