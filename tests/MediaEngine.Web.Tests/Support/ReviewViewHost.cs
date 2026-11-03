using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Editing;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace MediaEngine.Web.Tests.Support;

/// <summary>
/// Draws Review changes the way the pop-over window does: as a page that goes away as soon as the person leaves it (back, a tool,
/// discard or save), so tests can check what the page shows and how it is left without drawing the whole window.
/// </summary>
public sealed class ReviewViewHost : ComponentBase
{
    private bool _open = true;

    [Parameter, EditorRequired] public IReviewSource Source { get; set; } = default!;

    /// <summary>How the person left Review, or null while it is still open.</summary>
    public ReviewChangesOutcome? Outcome { get; private set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        if (!_open)
        {
            return;
        }

        builder.OpenComponent<ReviewChangesView>(0);
        builder.AddComponentParameter(1, nameof(ReviewChangesView.Source), Source);
        builder.AddComponentParameter(2, nameof(ReviewChangesView.OnOutcome), EventCallback.Factory.Create<ReviewChangesOutcome>(this, OnOutcome));
        builder.CloseComponent();
    }

    private void OnOutcome(ReviewChangesOutcome outcome)
    {
        Outcome = outcome;
        _open = false;
        StateHasChanged();
    }
}
