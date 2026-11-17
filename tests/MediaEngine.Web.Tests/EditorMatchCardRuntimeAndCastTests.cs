using System.Globalization;
using MediaEngine.Contracts.Editing;
using MediaEngine.Domain.Configuration;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Editing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Tests;

/// <summary>The match card says how long the record runs and who stars in it, beside its creator and year (universal editor v7 follow-up).</summary>
public sealed class EditorMatchCardRuntimeAndCastTests
{
    private static T WithEnglish<T>(Func<IStringLocalizer<SharedStrings>, T> action)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            using var provider = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
            return action(provider.GetRequiredService<IStringLocalizer<SharedStrings>>());
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void CurrentMatchCard_ShowsCreatorYearAndRuntimeOnOneLine_AndTheLeadCastOnTheNext()
    {
        var current = new EditorCurrentMatchDto
        {
            State = EditorCurrentMatchStates.Matched,
            Record = new EditorMatchedRecordDto
            {
                Title = "Heat",
                Year = "1995",
                Creator = "Michael Mann",
                LengthText = "170 min",
                Cast = ["Al Pacino", "Robert De Niro", "Val Kilmer"],
            },
        };

        var card = WithEnglish(L => EditorMatchCardModels.ForCurrent(current, EditorKinds.Movie, L));

        Assert.Equal("Michael Mann · 1995 · 170 min", card.Byline);
        Assert.Equal("Al Pacino, Robert De Niro, Val Kilmer", card.CastLine);
    }

    [Fact]
    public void CurrentMatchCard_WithNoRuntimeOrCast_ShowsOnlyCreatorAndYear()
    {
        var current = new EditorCurrentMatchDto
        {
            State = EditorCurrentMatchStates.Matched,
            Record = new EditorMatchedRecordDto { Title = "Dune", Year = "1965", Creator = "Frank Herbert" },
        };

        var card = WithEnglish(L => EditorMatchCardModels.ForCurrent(current, EditorKinds.Book, L));

        Assert.Equal("Frank Herbert · 1965", card.Byline);
        Assert.Null(card.CastLine);
    }

    [Fact]
    public void Cast_KeepsOnlyTheFirstFewDistinctNames()
    {
        var lead = EditorMatchCast.FromText("A | B |  | a | C | D | E");

        Assert.Equal(["A", "B", "C", "D"], lead);
        Assert.Empty(EditorMatchCast.FromText(null));
    }
}
