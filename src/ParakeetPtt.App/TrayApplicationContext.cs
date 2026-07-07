using ParakeetPtt.Core;
using System.Media;

namespace ParakeetPtt.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ModelRegistry _modelRegistry = ModelRegistry.CreateDefault();
    private readonly SessionHistory _history = new();
    private readonly AppSettingsStore _settingsStore = new(AppPaths.SettingsPath);
    private readonly WaveInAudioRecorder _recorder;
    private readonly DictationController _dictationController;
    private readonly Icon _trayIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly RightCtrlHotkeySource _hotkeySource;
    private readonly StatusOverlayForm _statusOverlay = new();
    private readonly SynchronizationContext _uiContext;
    private readonly CancellationTokenSource _lifetime = new();
    private SettingsForm? _settingsForm;
    private SessionHistoryForm? _historyForm;
    private AppSettings _settings = AppSettings.Default;
    private bool _exiting;
    private bool _acceptedRecordingStart;
    private bool _toggleRecordingActive;
    private bool _settingsOpening;
    private bool _listeningPreviewActive;
    private string? _lastTranscriptPreview;

    public TrayApplicationContext()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _recorder = new WaveInAudioRecorder(AppPaths.RootDirectory);
        _recorder.AudioLevelChanged += OnAudioLevelChanged;
        var transcriber = new LazyAssetTranscriber(
            AppPaths.RootDirectory,
            _settingsStore,
            () => _settings,
            settings => _settings = settings,
            message => ShowTrayNotification("Parakeet PTT", message, ToolTipIcon.Info));
        _dictationController = new DictationController(
            new ChunkedTranscribingDictationSessionFactory(_recorder, transcriber),
            new ClipboardPaster(),
            _history,
            ShowTranscriptPreview,
            ShowCleanupWarning,
            OnTranscriptUpdate,
            () => _settings.TranscriptCorrections);

        _trayIcon = TrayIconFactory.Create();
        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "Parakeet PTT",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };

        _notifyIcon.DoubleClick += (_, _) => ShowSettings();
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowSettings();
            }
        };
        _hotkeySource = new RightCtrlHotkeySource();
        _hotkeySource.Pressed += () => PostToUi(OnHotkeyPressedAsync);
        _hotkeySource.Released += () => PostToUi(OnHotkeyReleasedAsync);
        _hotkeySource.ToggleRequested += () => PostToUi(OnToggleRequestedAsync);
        _hotkeySource.Start();
        _ = LoadSettingsAsync();
    }

    private void PostToUi(Func<Task> action)
    {
        _uiContext.Post(async _ =>
        {
            try
            {
                await action();
            }
            catch (OperationCanceledException) when (_exiting)
            {
            }
            catch (Exception ex)
            {
                var status = DictationStatusCatalog.Error(ex.Message);
                ShowStatus(status, ToolTipIcon.Error);
                PlayStatusSound(StatusSound.Error);
            }
        }, null);
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip
        {
            BackColor = DarkTheme.Surface,
            ForeColor = DarkTheme.Text,
            Renderer = new ToolStripProfessionalRenderer(new DarkMenuColorTable())
        };

        menu.Items.Add(new ToolStripMenuItem("Open Settings", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripMenuItem("Session History", null, (_, _) => ShowHistory()));
        menu.Items.Add(new ToolStripMenuItem("Play Test Sound", null, (_, _) => PlayStatusSound(StatusSound.Listening)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit Parakeet PTT", null, (_, _) => ExitApplication()));

        return menu;
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            _settings = await _settingsStore.LoadAsync(_lifetime.Token);
            UpdateTrayText();
        }
        catch (OperationCanceledException) when (_exiting)
        {
        }
        catch (Exception ex)
        {
            ShowTrayNotification("Settings were not loaded", ex.Message, ToolTipIcon.Warning);
        }
    }

    private void ShowSettings()
    {
        if (_settingsOpening)
        {
            return;
        }

        if (_settingsForm is { Visible: true })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm ??= CreateSettingsForm();
        _ = ShowSettingsAsync(_settingsForm);
    }

    private SettingsForm CreateSettingsForm()
    {
        var form = new SettingsForm(_settingsStore, _modelRegistry);
        form.SettingsSaved += (_, settings) =>
        {
            _settings = settings;
            UpdateTrayText();
            ShowTrayNotification("Settings saved", "Parakeet PTT settings were updated.", ToolTipIcon.Info);
        };
        form.QuitRequested += (_, _) => ExitApplication();
        return form;
    }

    private async Task ShowSettingsAsync(SettingsForm form)
    {
        _settingsOpening = true;
        try
        {
            await form.LoadSettingsAsync(_lifetime.Token);
            if (!_exiting)
            {
                form.Show();
                form.Activate();
            }
        }
        catch (OperationCanceledException) when (_exiting)
        {
        }
        catch (Exception ex)
        {
            if (!_exiting)
            {
                ShowTrayNotification("Settings were not loaded", ex.Message, ToolTipIcon.Warning);
                form.UseSettings(AppSettings.Default);
                form.Show();
                form.Activate();
            }
        }
        finally
        {
            _settingsOpening = false;
        }
    }

    private void ShowHistory()
    {
        _historyForm ??= CreateHistoryForm();
        _historyForm.RefreshItems();
        _historyForm.Show();
        _historyForm.Activate();
    }

    private SessionHistoryForm CreateHistoryForm()
    {
        var form = new SessionHistoryForm(_history);
        form.QuitRequested += (_, _) => ExitApplication();
        return form;
    }

    private async Task OnHotkeyPressedAsync()
    {
        if (_exiting)
        {
            return;
        }

        if (await _dictationController.HandleHotkeyDownAsync(_lifetime.Token))
        {
            _acceptedRecordingStart = true;
            _listeningPreviewActive = true;
            PlayStatusSound(StatusSound.Listening);
            ShowStatus(DictationStatusCatalog.Listening, ToolTipIcon.Info, mode: ListeningTriggerMode.PushToTalk);
        }
    }

    private async Task OnHotkeyReleasedAsync()
    {
        if (_exiting)
        {
            return;
        }

        if (!_acceptedRecordingStart)
        {
            return;
        }

        _acceptedRecordingStart = false;
        await StopRecordingAndTranscribeAsync();
    }

    private async Task OnToggleRequestedAsync()
    {
        if (_exiting)
        {
            return;
        }

        if (_toggleRecordingActive)
        {
            _toggleRecordingActive = false;
            await StopRecordingAndTranscribeAsync();
            return;
        }

        if (await _dictationController.HandleHotkeyDownAsync(_lifetime.Token))
        {
            _toggleRecordingActive = true;
            _listeningPreviewActive = true;
            PlayStatusSound(StatusSound.Listening);
            ShowStatus(
                DictationStatusCatalog.Listening,
                ToolTipIcon.Info,
                notifyMessage: ListeningStatusFormatter.FormatHint(ListeningTriggerMode.Toggle),
                mode: ListeningTriggerMode.Toggle);
        }
    }

    private async Task StopRecordingAndTranscribeAsync()
    {
        _lastTranscriptPreview = null;
        _listeningPreviewActive = false;
        PlayStatusSound(StatusSound.Transcribing);
        ShowStatus(DictationStatusCatalog.Transcribing, ToolTipIcon.Info);
        try
        {
            var outcome = await _dictationController.HandleHotkeyUpAsync(_lifetime.Token);
            _historyForm?.RefreshItems();
            if (outcome == DictationOutcome.Pasted)
            {
                PlayStatusSound(StatusSound.Done);
                var status = _lastTranscriptPreview is { Length: > 0 }
                    ? DictationStatusCatalog.PastedTranscript(_lastTranscriptPreview)
                    : DictationStatusCatalog.Pasted;
                ShowStatus(status, ToolTipIcon.Info, notifyMessage: DictationStatusCatalog.Pasted.Message);
            }
            else if (outcome == DictationOutcome.EmptyTranscript)
            {
                ShowStatus(DictationStatusCatalog.EmptyTranscript, ToolTipIcon.Info);
            }
        }
        catch (OperationCanceledException) when (_exiting)
        {
        }
        catch (Exception ex)
        {
            PlayStatusSound(StatusSound.Error);
            ShowStatus(DictationStatusCatalog.Error(ex.Message), ToolTipIcon.Error);
        }
    }

    private void UpdateTrayText()
    {
        var model = _modelRegistry.Find(_settings.SelectedModelId) ?? _modelRegistry.DefaultModel;
        var text = $"Parakeet PTT - {model.DisplayName}";
        _notifyIcon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void ShowTrayNotification(string title, string message, ToolTipIcon icon)
    {
        if (!_settings.NotificationsEnabled)
        {
            return;
        }

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(2500);
    }

    private void ShowTranscriptPreview(string transcript)
    {
        _lastTranscriptPreview = transcript;
        ShowStatus(DictationStatusCatalog.TranscriptPreview(transcript), ToolTipIcon.Info, notify: false);
    }

    private void OnTranscriptUpdate(TranscriptUpdate update)
    {
        if (update.Kind != TranscriptUpdateKind.Partial)
        {
            return;
        }

        _uiContext.Post(_ =>
        {
            if (_exiting || _statusOverlay.IsDisposed || !_listeningPreviewActive)
            {
                return;
            }

            var text = string.IsNullOrWhiteSpace(update.UnstableText)
                ? update.StableText
                : $"{update.StableText} {update.UnstableText}".Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                ShowStatus(DictationStatusCatalog.TranscriptPreview(text), ToolTipIcon.Info, notify: false);
            }
        }, null);
    }

    private void ShowCleanupWarning(string path)
    {
        ShowTrayNotification(
            "Temporary audio remains",
            $"Could not delete {Path.GetFileName(path)} from local app data.",
            ToolTipIcon.Warning);
    }

    private void ShowStatus(
        DictationStatus status,
        ToolTipIcon icon,
        bool notify = true,
        string? notifyMessage = null,
        ListeningTriggerMode mode = ListeningTriggerMode.PushToTalk)
    {
        _statusOverlay.ShowStatus(status, mode);
        if (notify)
        {
            ShowTrayNotification(status.Title, notifyMessage ?? status.Message, icon);
        }
    }

    private void OnAudioLevelChanged(double level)
    {
        _uiContext.Post(_ =>
        {
            if (_exiting || _statusOverlay.IsDisposed)
            {
                return;
            }

            _statusOverlay.UpdateActivityLevel(level);
        }, null);
    }

    private void PlayStatusSound(StatusSound sound)
    {
        if (!_settings.AudibleStatusEnabled)
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

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _lifetime.Cancel();
        _recorder.AudioLevelChanged -= OnAudioLevelChanged;
        _hotkeySource.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
        _statusOverlay.Dispose();
        _settingsForm?.Dispose();
        _historyForm?.Dispose();
        _ = Task.Run(_recorder.Dispose);
        _lifetime.Dispose();
        ExitThread();
    }
}

internal enum StatusSound
{
    Listening,
    Transcribing,
    Done,
    Error
}

internal sealed class DarkMenuColorTable : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => DarkTheme.Surface;
    public override Color ImageMarginGradientBegin => DarkTheme.Surface;
    public override Color ImageMarginGradientMiddle => DarkTheme.Surface;
    public override Color ImageMarginGradientEnd => DarkTheme.Surface;
    public override Color MenuItemSelected => DarkTheme.SurfaceRaised;
    public override Color MenuItemSelectedGradientBegin => DarkTheme.SurfaceRaised;
    public override Color MenuItemSelectedGradientEnd => DarkTheme.SurfaceRaised;
    public override Color MenuItemBorder => DarkTheme.Border;
    public override Color SeparatorDark => DarkTheme.Border;
    public override Color SeparatorLight => DarkTheme.Border;
}
