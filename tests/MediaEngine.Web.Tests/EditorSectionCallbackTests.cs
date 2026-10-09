using Bunit;
using MediaEngine.Web.Components.MediaEditor.Sections;
using MediaEngine.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class EditorSectionCallbackTests : AsyncBunitContext
{
    public EditorSectionCallbackTests()
    {
        Services.AddNativeUiServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Render<AppPopoverHost>();
    }

    [Fact]
    public void HeaderCallbacksRerenderTheStateOwnerAndPreserveTheSaveBoundary()
    {
        var cut = Render<HeaderStateOwner>();
        Assert.True(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save Changes").HasAttribute("disabled"));
        cut.Find("button.dirty-probe").Click();
        Assert.False(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save Changes").HasAttribute("disabled"));
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").Click();
        Assert.False(cut.Instance.Dirty);
        Assert.True(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save Changes").HasAttribute("disabled"));
        cut.Find("button.dirty-probe").Click();
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save Changes").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(1, cut.Instance.SaveCount);
            Assert.False(cut.Instance.Dirty);
            Assert.True(cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save Changes").HasAttribute("disabled"));
        });
    }

    private sealed class HeaderStateOwner : ComponentBase
    {
        public bool Dirty { get; private set; }
        public int SaveCount { get; private set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "class", "dirty-probe");
            builder.AddAttribute(2, "onclick", EventCallback.Factory.Create(this, () => Dirty = true));
            builder.CloseElement();
            builder.OpenComponent<EditorHeaderSection>(3);
            builder.AddAttribute(4, "Inline", true);
            builder.AddAttribute(5, "ActiveTab", "details");
            builder.AddAttribute(6, "EditorPageTitle", "Fixture editor");
            builder.AddAttribute(7, "HasStagedEditorChanges", Dirty);
            builder.AddAttribute(8, "HandleCloseRequested", EventCallback.Factory.Create(this, () => Dirty = false));
            builder.AddAttribute(9, "ResetEditorChangesRequested", EventCallback.Factory.Create(this, () => Dirty = false));
            builder.AddAttribute(10, "SaveAsyncRequested", EventCallback.Factory.Create(this, SaveAsync));
            builder.CloseComponent();
        }

        private async Task SaveAsync()
        {
            await Task.Yield();
            SaveCount++;
            Dirty = false;
        }
    }
}
