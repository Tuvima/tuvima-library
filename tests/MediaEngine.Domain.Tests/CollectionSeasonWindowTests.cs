using MediaEngine.Domain.Models;

namespace MediaEngine.Domain.Tests;

public sealed class CollectionSeasonWindowTests
{
    [Theory]
    [InlineData("10-01", 10, 1)]
    [InlineData("02-29", 2, 29)]
    [InlineData("12-31", 12, 31)]
    public void DayMonth_ReadsMonthDayText(string text, int month, int day)
    {
        Assert.True(DayMonth.TryParse(text, out var value));
        Assert.Equal(new DayMonth(month, day), value);
        Assert.Equal(text, value.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1-1")]
    [InlineData("13-01")]
    [InlineData("00-10")]
    [InlineData("04-31")]
    [InlineData("02-30")]
    [InlineData("10/01")]
    [InlineData("ab-cd")]
    public void DayMonth_RejectsAnythingElse(string? text)
    {
        Assert.False(DayMonth.TryParse(text, out _));
    }

    [Theory]
    [InlineData(2026, 9, 30, false)]
    [InlineData(2026, 10, 1, true)]
    [InlineData(2026, 10, 15, true)]
    [InlineData(2026, 11, 1, true)]
    [InlineData(2026, 11, 2, false)]
    [InlineData(2026, 11, 15, false)]
    public void Window_IncludesBothEdges(int year, int month, int day, bool expected)
    {
        var window = new CollectionSeasonWindow(new DayMonth(10, 1), new DayMonth(11, 1));

        Assert.Equal(expected, window.Contains(new DateOnly(year, month, day)));
    }

    [Theory]
    [InlineData(2026, 11, 30, false)]
    [InlineData(2026, 12, 1, true)]
    [InlineData(2026, 12, 31, true)]
    [InlineData(2027, 1, 2, true)]
    [InlineData(2027, 1, 3, false)]
    public void Window_ThatEndsBeforeItStartsRunsOverNewYear(int year, int month, int day, bool expected)
    {
        var window = new CollectionSeasonWindow(new DayMonth(12, 1), new DayMonth(1, 2));

        Assert.Equal(expected, window.Contains(new DateOnly(year, month, day)));
    }

    [Theory]
    [InlineData(2026, 11, 15, 2027, 10, 1)]
    [InlineData(2026, 5, 3, 2026, 10, 1)]
    public void NextOpening_IsTheNextStartDay(int year, int month, int day, int openYear, int openMonth, int openDay)
    {
        var window = new CollectionSeasonWindow(new DayMonth(10, 1), new DayMonth(11, 1));

        Assert.Equal(new DateOnly(openYear, openMonth, openDay), window.NextOpening(new DateOnly(year, month, day)));
    }

    [Fact]
    public void NextOpening_ForA29FebruaryStartUses28FebruaryInAYearWithoutOne()
    {
        var window = new CollectionSeasonWindow(new DayMonth(2, 29), new DayMonth(3, 5));

        Assert.Equal(new DateOnly(2027, 2, 28), window.NextOpening(new DateOnly(2026, 12, 1)));
    }

    [Fact]
    public void TryCreate_NeedsBothEdgesOrNeither()
    {
        Assert.True(CollectionSeasonWindow.TryCreate(null, null, out var none));
        Assert.Null(none);
        Assert.True(CollectionSeasonWindow.TryCreate("", "", out var empty));
        Assert.Null(empty);
        Assert.False(CollectionSeasonWindow.TryCreate("10-01", null, out _));
        Assert.False(CollectionSeasonWindow.TryCreate("10-01", "bad", out _));
        Assert.True(CollectionSeasonWindow.TryCreate("10-01", "11-01", out var window));
        Assert.Equal(new CollectionSeasonWindow(new DayMonth(10, 1), new DayMonth(11, 1)), window);
    }
}
