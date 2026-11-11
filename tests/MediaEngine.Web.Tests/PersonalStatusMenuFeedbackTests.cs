using Bunit;
using MediaEngine.Contracts.Progress;
using MediaEngine.Web.Components.Details;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Marking an album listened or unlistened from the "..." menu: the menu offering the other choice is the confirmation (no toast), and a
/// change the Engine refuses is handed to the host to show in place.
/// </summary>
public sealed class PersonalStatusMenuFeedbackTests : AsyncBunitContext
{
    private readonly Guid _albumId = Guid.NewGuid();
    private int _completed;
    private bool _failChange;

    public PersonalStatusMenuFeedbackTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddSingleton(EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetPersonalStatusAsync), _ =>
                Task.FromResult<PersonalStatusInfo?>(new PersonalStatusInfo("r" + _completed, 4, 1, _completed, false)));
            stub.SetHandler(nameof(IEngineApiClient.ChangePersonalStatusAsync), _ =>
            {
                if (_failChange)
                {
                    throw new InvalidOperationException("The Engine could not change the listening status.");
                }

                _completed = 4;
                return Task.FromResult(new PersonalStatusCommandResult(Guid.NewGuid(), 3, "r4"));
            });
        }));
    }

    private IRenderedComponent<PersonalStatusMenuItems> RenderMenu(List<string> failures) =>
        Render<PersonalStatusMenuItems>(parameters => parameters
            .Add(menu => menu.WorkId, _albumId)
            .Add(menu => menu.OnFailed, EventCallback.Factory.Create<string>(this, failures.Add)));

    [Fact]
    public void AChangeThatWorks_SwitchesTheMenuToTheOtherChoiceAndSaysNothingElse()
    {
        var failures = new List<string>();
        var cut = RenderMenu(failures);
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == "Mark as listened"));

        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Mark as listened").Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain(cut.FindAll("[role='menuitem']"), item => item.TextContent.Trim() == "Mark as listened"));
        Assert.Empty(failures);
    }

    [Fact]
    public void AChangeTheEngineRefuses_IsHandedToTheHostToShowInPlaceAndNeverToasted()
    {
        _failChange = true;
        var failures = new List<string>();
        var cut = RenderMenu(failures);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[role='menuitem']")));

        cut.FindAll("[role='menuitem']").Single(item => item.TextContent.Trim() == "Mark as listened").Click();

        cut.WaitForAssertion(() => Assert.Equal(["The Engine could not change the listening status."], failures));
    }
}
