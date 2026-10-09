using MediaEngine.Contracts.Authentication;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Ui;
using Microsoft.AspNetCore.Components.Web;

namespace MediaEngine.Web.Components.Settings;

/// <summary>Optional two-step codes: turn on (QR code + first code), turn off, and the recovery codes shown once.</summary>
public partial class AccountSettingsTab
{
    private TwoStepSetupResponse? _twoStepSetup;
    private string _twoStepQr = string.Empty;
    private string _twoStepCode = string.Empty;
    private string _twoStepDisableCode = string.Empty;
    private IReadOnlyList<string> _twoStepRecoveryCodes = [];
    private bool? _twoStepOn;
    private bool _twoStepWorking;
    private bool _twoStepTurningOff;

    private bool TwoStepOn => _twoStepOn ?? Capabilities.HasTwoStep;

    private async Task BeginTwoStepSetupAsync()
    {
        if (_busy || _disposed) return;
        _busy = _twoStepWorking = true;
        try
        {
            var result = await Confirmed.RunAsync(() => Identity.BeginTwoStepSetupResultAsync(_lifetime.Token), _lifetime.Token);
            if (result.Value is not { } setup)
            {
                Snackbar.Add(result.Failure == DashboardAccessMutationFailure.Conflict
                    ? "Two-step codes can't be set up for this account right now."
                    : NotChangedMessage(result, "Two-step codes could not be set up."), AppSeverity.Error);
                return;
            }

            _twoStepSetup = setup;
            _twoStepQr = QrCodeSvg.Render(setup.OtpAuthUri);
            _twoStepCode = string.Empty;
            _twoStepRecoveryCodes = [];
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _busy = _twoStepWorking = false; }
    }

    private async Task EnableTwoStepAsync()
    {
        if (_busy || _disposed || string.IsNullOrWhiteSpace(_twoStepCode)) return;
        _busy = _twoStepWorking = true;
        try
        {
            var code = _twoStepCode.Trim();
            var result = await Confirmed.RunAsync(() => Identity.EnableTwoStepResultAsync(code, _lifetime.Token), _lifetime.Token);
            if (result.Value is not { } codes)
            {
                Snackbar.Add(result.Failure switch
                {
                    DashboardAccessMutationFailure.Unauthorized => "That code didn't match. Check the code in your app and try again.",
                    DashboardAccessMutationFailure.Transient => "Too many attempts. Wait a minute and try again.",
                    _ => NotChangedMessage(result, "Two-step codes could not be turned on."),
                }, AppSeverity.Error);
                return;
            }

            _twoStepOn = true;
            _twoStepSetup = null;
            _twoStepQr = string.Empty;
            _twoStepCode = string.Empty;
            _twoStepRecoveryCodes = codes.RecoveryCodes;
            Snackbar.Add("Two-step codes are on. Save your new recovery codes.", AppSeverity.Success);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _busy = _twoStepWorking = false; }
    }

    private void CancelTwoStepSetup()
    {
        _twoStepSetup = null;
        _twoStepQr = string.Empty;
        _twoStepCode = string.Empty;
    }

    // Confirming it's you comes first, so a code typed for that step is not reused for turning off.
    private async Task StartTurnOffTwoStepAsync()
    {
        if (_busy || _disposed) return;
        _busy = true;
        try
        {
            var recent = await Confirmed.RunAsync(() => Identity.CheckRecentSignInAsync(_lifetime.Token), _lifetime.Token);
            if (!recent.Succeeded) { Snackbar.Add(NotChangedMessage(recent, "Two-step codes could not be turned off."), AppSeverity.Error); return; }
            _twoStepDisableCode = string.Empty;
            _twoStepTurningOff = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _busy = false; }
    }

    private async Task DisableTwoStepAsync()
    {
        if (_busy || _disposed || string.IsNullOrWhiteSpace(_twoStepDisableCode)) return;
        _busy = _twoStepWorking = true;
        try
        {
            var code = _twoStepDisableCode.Trim();
            var result = await Confirmed.RunAsync(() => Identity.DisableTwoStepResultAsync(code, _lifetime.Token), _lifetime.Token);
            if (!result.Succeeded)
            {
                Snackbar.Add(result.Failure switch
                {
                    DashboardAccessMutationFailure.Unauthorized => "That code didn't work. Each code works once, so wait for the next one in your app.",
                    DashboardAccessMutationFailure.Transient => "Too many attempts. Wait a minute and try again.",
                    _ => NotChangedMessage(result, "Two-step codes could not be turned off."),
                }, AppSeverity.Error);
                return;
            }

            _twoStepOn = false;
            _twoStepTurningOff = false;
            _twoStepDisableCode = string.Empty;
            _twoStepRecoveryCodes = [];
            Snackbar.Add("Two-step codes are off.", AppSeverity.Success);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally { _busy = _twoStepWorking = false; }
    }

    private void CancelTurnOffTwoStep()
    {
        _twoStepTurningOff = false;
        _twoStepDisableCode = string.Empty;
    }

    private async Task OnTwoStepKeyDownAsync(KeyboardEventArgs args)
    {
        if (args.Key != "Enter") return;
        if (_twoStepSetup is not null) await EnableTwoStepAsync();
        else if (_twoStepTurningOff) await DisableTwoStepAsync();
    }
}
