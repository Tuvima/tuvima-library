using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

/// <summary>
/// A bUnit context that already has the Dashboard's string table available, because nearly every component now reads its wording
/// from <c>IStringLocalizer&lt;SharedStrings&gt;</c>. Tests that build their own context should use this instead of the plain one.
/// </summary>
public class LocalizedBunitContext : BunitContext
{
    public LocalizedBunitContext() => Services.AddLocalization();
}
