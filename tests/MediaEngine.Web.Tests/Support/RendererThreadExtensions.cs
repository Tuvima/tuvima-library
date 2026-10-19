using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests.Support;

/// <summary>
/// Finds an element and acts on it in one step on the renderer's own thread.
/// A component that loads data re-renders on its own schedule and hands out new event handler ids each time. Reading the
/// page on the test thread and then clicking can land between two of those renders, and Blazor ignores an event that
/// carries a stale id, so the click silently does nothing. Doing both on the renderer thread cannot interleave with a render.
/// </summary>
public static class RendererThreadExtensions
{
    public static Task ClickAsync<TComponent>(this IRenderedComponent<TComponent> cut, Func<IRenderedComponent<TComponent>, IElement> find)
        where TComponent : IComponent =>
        cut.InvokeAsync(() => find(cut).Click());

    public static Task ClickAsync<TComponent>(this IRenderedComponent<TComponent> cut, string selector, int index = 0)
        where TComponent : IComponent =>
        cut.InvokeAsync(() => cut.FindAll(selector)[index].Click());

    public static Task TriggerEventAsync<TComponent>(this IRenderedComponent<TComponent> cut, Func<IRenderedComponent<TComponent>, IElement> find, string eventName, EventArgs args)
        where TComponent : IComponent =>
        cut.InvokeAsync(() => find(cut).TriggerEvent(eventName, args));
}
