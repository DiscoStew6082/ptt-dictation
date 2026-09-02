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
    Func<TimeSpan, Task> DelayAsync);

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

        switch (state.Phase)
        {
            case DictationWorkflowPhase.Recording:
                PresentRecording(state, phaseChanged);
                break;
            case DictationWorkflowPhase.Processing:
                PresentProcessing(state, phaseChanged);
                break;
            case DictationWorkflowPhase.Pasted:
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
                    ShowStatus(DictationStatusCatalog.DictationCancelled, ToolTipIcon.Info);
                }

                break;
            case DictationWorkflowPhase.Failed:
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

    private void PresentProcessing(DictationWorkflowState state, bool phaseChanged)
    {
        if (phaseChanged)
        {
            _overlay.ShowProcessing();
            _environment.PlayStatusSound(StatusSound.Transcribing);
        }

        if (!string.IsNullOrWhiteSpace(state.Transcript))
        {
            _overlay.ShowProcessingTranscript(state.Transcript);
        }

        if (!string.IsNullOrWhiteSpace(state.ProcessingDetail))
        {
            _overlay.ShowProcessingDetail(state.ProcessingDetail);
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
        if (_environment.GetCurrentState().Phase == DictationWorkflowPhase.Pasted)
        {
            _overlay.HideRecording();
        }
    }

    private void PresentRecording(DictationWorkflowState state, bool phaseChanged)
    {
        var settings = _environment.GetSettings();
        var mode = state.TriggerMode == DictationTriggerMode.Toggle
            ? ListeningTriggerMode.Toggle
            : ListeningTriggerMode.PushToTalk;
        var hotkey = state.TriggerMode == DictationTriggerMode.Toggle
            ? settings.ToggleHotkey
            : settings.HoldHotkey;
        var hotkeyName = DictationHotkeyCatalog.DisplayName(hotkey);

        if (phaseChanged)
        {
            _environment.PlayStatusSound(StatusSound.Listening);
            ShowStatus(
                DictationStatusCatalog.Listening,
                ToolTipIcon.Info,
                ListeningStatusFormatter.FormatHint(mode, hotkeyName),
                mode,
                hotkeyName);
        }

        if (!string.IsNullOrWhiteSpace(state.Transcript))
        {
            _overlay.ShowListeningTranscript(state.Transcript, mode);
        }
    }

    private void ShowStatus(
        DictationStatus status,
        ToolTipIcon icon,
        string? notifyMessage = null,
        ListeningTriggerMode mode = ListeningTriggerMode.PushToTalk,
        string? listeningHotkeyName = null)
    {
        _overlay.ShowStatus(status, mode, listeningHotkeyName);
        _environment.ShowTrayNotification(status.Title, notifyMessage ?? status.Message, icon);
    }
}
