using MediaEngine.Web.Services.Navigation;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace MediaEngine.Web.Tests.Support;

/// <summary>
/// Stands in for the Router and the overlay-aware route view in tests: it shows <typeparamref name="TPage"/> and cascades the
/// current address as the address the page was routed with, following every navigation, as the route view does in the app.
/// Routed pages read their query from that address (RoutedPageQuery), so a test that changes the address after the page is
/// shown renders the page through this host.
/// </summary>
public sealed class RoutedPageHost<TPage> : ComponentBase, IDisposable
    where TPage : IComponent
{
    [Inject] private NavigationManager Navigation { get; set; } = default!;

    /// <summary>The page's own parameters (route values such as a lane or shelf key).</summary>
    [Parameter] public IReadOnlyDictionary<string, object?>? PageParameters { get; set; }

    protected override void OnInitialized() => Navigation.LocationChanged += OnLocationChanged;

    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e) =>
        _ = InvokeAsync(StateHasChanged);

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenComponent<CascadingValue<string>>(0);
        builder.AddComponentParameter(1, nameof(CascadingValue<string>.Name), RoutedPageQuery.CascadingName);
        builder.AddComponentParameter(2, nameof(CascadingValue<string>.Value), new Uri(Navigation.Uri).PathAndQuery);
        builder.AddComponentParameter(3, nameof(CascadingValue<string>.ChildContent), (RenderFragment)(page =>
        {
            page.OpenComponent<TPage>(0);
            if (PageParameters is not null)
            {
                foreach (var parameter in PageParameters)
                {
                    page.AddComponentParameter(1, parameter.Key, parameter.Value);
                }
            }

            page.CloseComponent();
        }));
        builder.CloseComponent();
    }

    public void Dispose() => Navigation.LocationChanged -= OnLocationChanged;
}
