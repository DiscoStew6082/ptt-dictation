using PttDictation.Core;
using System.Media;

namespace PttDictation.App;

internal sealed record DictationPresentationEnvironment(
    Func<AppSettings> GetSettings,
    Func<DictationWorkflowState> GetCurrentState,
    Func<bool> IsUnavailable,
    Action RefreshHistory,
    Action<string> ShowCleanupWarning,
    Action<string, string, ToolTipIcon> ShowTrayNotification,
    Action<StatusSound> PlayStatusSound,
    Func<TimeSpan, Task> DelayAsync,
    Func<bool> IsInlinePreview,
    Func<bool>? IsWaitingForTargetResponse = null);

internal enum StatusSound
{
    Listening,
    Transcribing,
    Done,
    Error
}

internal sealed class StatusSoundPlayer(Func<AppSettings> getSettings)
{
    public void Play(StatusSound sound)
    {
        if (!getSettings().AudibleStatusEnabled)
        {
            return;
        }

        switch (sound)
        {
            case StatusSound.Listening:
                SystemSounds.Asterisk.Play();
                break;
            case StatusSound.Transcribing:
                SystemSounds.Question.Play();
                break;
            case StatusSound.Done:
                SystemSounds.Exclamation.Play();
                break;
            case StatusSound.Error:
                SystemSounds.Hand.Play();
                break;
        }
    }
}

internal sealed class DictationPresentation
{
    internal static TimeSpan PostPasteVisibilityDurationForTest => TimeSpan.FromMilliseconds(250);

    private readonly StatusOverlayForm _overlay;
    private readonly ToolStripMenuItem _cancelItem;
    private readonly DictationPresentationEnvironment _environment;
    private DictationWorkflowPhase _previousPhase = DictationWorkflowPhase.Idle;
    private string? _presentedCleanupWarningPath;

    public DictationPresentation(
        StatusOverlayForm overlay,
        ToolStripMenuItem cancelItem,
        DictationPresentationEnvironment environment)
    {
        _overlay = overlay;
        _cancelItem = cancelItem;
        _environment = environment;
    }

    public async Task ApplyAsync(DictationWorkflowState state)
    {
        if (_environment.IsUnavailable() || _overlay.IsDisposed)
        {
            return;
        }

        _cancelItem.Enabled = state.CanCancel;
        _overlay.SetInlinePreview(_environment.IsInlinePreview());

        if (string.IsNullOrWhiteSpace(state.CleanupWarningPath))
        {
            _presentedCleanupWarningPath = null;
        }
        else if (!string.Equals(
                     state.CleanupWarningPath,
                     _presentedCleanupWarningPath,
                     StringComparison.OrdinalIgnoreCase))
        {
            _environment.ShowCleanupWarning(state.CleanupWarningPath);
            _presentedCleanupWarningPath = state.CleanupWarningPath;
        }

        var phaseChanged = state.Phase != _previousPhase;
        _previousPhase = state.Phase;

        // The live-paste workflow presents words in the destination editor.
        // This includes the asynchronous capture interval and focus pauses;
        // neither should flash a transcript card over that editor.
        if (state.Phase is DictationWorkflowPhase.Recording or DictationWorkflowPhase.Processing)
        {
            if (_environment.IsWaitingForTargetResponse?.Invoke() == true)
                _overlay.ShowStatus(state.Phase == DictationWorkflowPhase.Recording
                    ? DictationStatusCatalog.WaitingForTextbox with
                    {
                        Message = "Recording continues. Text is waiting for the original textbox to respond. You can cancel dictation."
                    }
                    : DictationStatusCatalog.WaitingForTextbox);
            else _overlay.HideRecording();
            if (phaseChanged)
                _environment.PlayStatusSound(state.Phase == DictationWorkflowPhase.Recording
                    ? StatusSound.Listening : StatusSound.Transcribing);
            return;
        }

        switch (state.Phase)
        {
            case DictationWorkflowPhase.Preparing:
                var settings = _environment.GetSettings();
                var hint = state.TriggerMode == DictationTriggerMode.Toggle
                    ? $"Press {DictationHotkeyCatalog.DisplayName(settings.ToggleHotkey)} again to cancel."
                    : $"Keep holding {DictationHotkeyCatalog.DisplayName(settings.HoldHotkey)}.";
                _overlay.ShowStatus(DictationStatusCatalog.Preparing with
                {
                    Message = DictationStatusCatalog.Preparing.Message + " " + hint
                });
                // Listening is signalled only after preparation and microphone
                // startup. Do not play the listening sound for a pending start.
                break;
            case DictationWorkflowPhase.Pasted:
            case DictationWorkflowPhase.InsertedPreview:
                await PresentPastedAsync(phaseChanged);
                break;
            case DictationWorkflowPhase.Empty:
                if (phaseChanged)
                {
                    ShowStatus(DictationStatusCatalog.EmptyTranscript, ToolTipIcon.Info);
                }

                break;
            case DictationWorkflowPhase.Cancelled:
                if (phaseChanged)
                {
                    var cancelled = DictationStatusCatalog.DictationCancelled with
                    {
                        Message = "Recording stopped. Text already inserted remains in the textbox."
                    };
                    ShowStatus(cancelled, ToolTipIcon.Info);
                }

                break;
            case DictationWorkflowPhase.Failed:
                _environment.RefreshHistory();
                if (phaseChanged)
                {
                    _environment.PlayStatusSound(StatusSound.Error);
                    ShowStatus(
                        DictationStatusCatalog.Error(state.ErrorMessage ?? "Unknown error."),
                        ToolTipIcon.Error);
                }

                break;
            case DictationWorkflowPhase.Idle:
                break;
        }
    }

    private async Task PresentPastedAsync(bool phaseChanged)
    {
        _environment.RefreshHistory();
        if (!phaseChanged)
        {
            return;
        }

        _environment.PlayStatusSound(StatusSound.Done);
        await _environment.DelayAsync(PostPasteVisibilityDurationForTest);
        if (_environment.GetCurrentState().Phase is DictationWorkflowPhase.Pasted or DictationWorkflowPhase.InsertedPreview)
        {
            _overlay.HideRecording();
        }
    }

    private void ShowStatus(
        DictationStatus status,
        ToolTipIcon icon)
    {
        _overlay.ShowStatus(status);
        _environment.ShowTrayNotification(status.Title, status.Message, icon);
    }
}
