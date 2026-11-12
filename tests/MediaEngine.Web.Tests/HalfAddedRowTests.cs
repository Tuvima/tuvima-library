using Bunit;
using MediaEngine.Contracts.Ingestion;
using MediaEngine.Web.Components.Settings;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MediaEngine.Web.Tests;

/// <summary>
/// One row that cannot be drawn must never replace the whole screen with "Something went wrong" (ingestion WP8, bug 13):
/// the row boundary shows a "couldn't be shown" row instead, and an impossible Needs Review duration is shown as detected.
/// </summary>
public sealed class HalfAddedRowTests : LiveLibraryTestContext
{
    private readonly RecordingLoggerProvider _logs = new();

    public HalfAddedRowTests()
    {
        Services.AddNativeUiServices();
        Services.GetRequiredService<ILoggerFactory>().AddProvider(_logs);
    }

    [Fact]
    public void TheRowBoundary_LogsTheExceptionAndShowsTheItemTitleInItsFallback()
    {
        var cut = Render<AppRowErrorBoundary>(parameters => parameters
            .Add(boundary => boundary.ItemTitle, "Cibola Burn")
            .Add(boundary => boundary.ChildContent, (RenderFragment)(builder =>
            {
                builder.OpenComponent<ThrowingRow>(0);
                builder.CloseComponent();
            })));

        var fallback = cut.Find("[data-testid='row-error']");
        Assert.Contains(AppRowErrorBoundary.Headline, fallback.TextContent, StringComparison.Ordinal);
        Assert.Contains("Cibola Burn", fallback.TextContent, StringComparison.Ordinal);
        var logged = Assert.Single(_logs.Errors);
        Assert.IsType<InvalidOperationException>(logged);
        Assert.Equal("this row cannot be drawn", logged.Message);
    }

    [Fact]
    public void TheRowBoundary_ShowsItsChildAgainWhenItsResetKeyChanges()
    {
        RenderFragment throwing = builder =>
        {
            builder.OpenComponent<ThrowingRow>(0);
            builder.CloseComponent();
        };
        RenderFragment fine = builder => builder.AddMarkupContent(0, "<p class=\"fine-row\">fine</p>");
        var cut = Render<AppRowErrorBoundary>(parameters => parameters
            .Add(boundary => boundary.ResetKey, 1)
            .Add(boundary => boundary.ChildContent, throwing));
        Assert.Single(cut.FindAll("[data-testid='row-error']"));

        cut.Render(parameters => parameters
            .Add(boundary => boundary.ResetKey, 1)
            .Add(boundary => boundary.ChildContent, fine));
        Assert.Single(cut.FindAll("[data-testid='row-error']"));

        cut.Render(parameters => parameters
            .Add(boundary => boundary.ResetKey, 2)
            .Add(boundary => boundary.ChildContent, fine));
        Assert.Empty(cut.FindAll("[data-testid='row-error']"));
        Assert.Single(cut.FindAll(".fine-row"));
    }

    [Theory]
    [InlineData("src/MediaEngine.Web/Components/Settings/NeedsReview/NeedsReviewGroupSection.razor")]
    [InlineData("src/MediaEngine.Web/Components/Settings/ActivityLogPanel.razor")]
    public void EveryRowLoopOnTheIngestionPages_IsWrappedInTheRowErrorBoundary(string relativePath)
    {
        var source = File.ReadAllText(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", relativePath)));

        Assert.Contains("<AppRowErrorBoundary", source, StringComparison.Ordinal);
        Assert.Contains("</AppRowErrorBoundary>", source, StringComparison.Ordinal);
    }

    private sealed class ThrowingRow : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) =>
            throw new InvalidOperationException("this row cannot be drawn");
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        public List<Exception> Errors { get; } = [];

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Errors);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<Exception> errors) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (logLevel >= LogLevel.Error && exception is not null)
                {
                    errors.Add(exception);
                }
            }
        }
    }
}
