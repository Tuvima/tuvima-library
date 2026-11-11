using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Browse;
using MediaEngine.Web.Components.Library;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Editing;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Pages;

public partial class ListenPage
{
    // A playlist or queue action that failed is told at the top of the page content and stays until dismissed or the next try; what worked is
    // already shown by the list, the rail or the player.
    private string? _notice;
    private AppUiTone _noticeTone = AppUiTone.Error;
    private bool _noticeUrgent;

    // urgent: a warning that says an action just failed is announced at once, like an error.
    private void ShowNotice(string message, AppUiTone tone, bool urgent = false)
    {
        _notice = message;
        _noticeTone = tone;
        _noticeUrgent = urgent;
    }
}
