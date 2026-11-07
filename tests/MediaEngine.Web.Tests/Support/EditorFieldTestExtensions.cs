using Bunit;
using Microsoft.AspNetCore.Components;

namespace MediaEngine.Web.Tests;

/// <summary>
/// The Details form starts every provider-filled field locked (editor v5 D5), so a test that types into one unlocks it first, the way a
/// person clicks the lock.
/// </summary>
internal static class EditorFieldTestExtensions
{
    /// <summary>Clicks the lock of a provider field (when it is locked) and waits for the field to take typing.</summary>
    public static void UnlockField(this IRenderedComponent<IComponent> cut, string fieldKey)
    {
        var field = cut.WaitForElement($".tl-editor-field[data-field-key='{fieldKey}']");
        if (field.GetAttribute("data-locked") == "true")
        {
            field.QuerySelector("button.tl-editor-field__lock-toggle")!.Click();
        }

        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find($".tl-editor-field[data-field-key='{fieldKey}']").GetAttribute("data-locked")));
    }

    /// <summary>The form lock of a field: "locked", "unlocked" or "yours" (the person's own changed value).</summary>
    public static string? LockStateOf(this IRenderedComponent<IComponent> cut, string fieldKey) =>
        cut.Find($".tl-editor-field[data-field-key='{fieldKey}'] [data-lock-state]").GetAttribute("data-lock-state");
}
