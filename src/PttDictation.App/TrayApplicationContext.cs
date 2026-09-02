using PttDictation.Core;

namespace PttDictation.App;

internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly ModelRegistry _modelRegistry = ModelRegistry.CreateDefault();
    private readonly SessionHistory _history = new();
    private readonly AppSettingsStore _settingsStore = new(AppPaths.SettingsPath);
    private readonly WasapiAudioRecorder _recorder;
    private readonly LazyAssetTranscriber _transcriber;
    private readonly DictationWorkflow _dictationWorkflow;
    private readonly DictationPresentation _dictationPresentation;
    private readonly StatusSoundPlayer _statusSoundPlayer;
    private readonly Func<SettingsForm> _settingsFormFactory;
    private readonly Icon _trayIcon;
    private readonly NotifyIcon _notifyIcon;
    private readonly GlobalHotkeySource _hotkeySource;
    private readonly StatusOverlayForm _statusOverlay = new();
    private readonly SynchronizationContext _uiContext;
    private readonly CancellationTokenSource _lifetime = new();
    private ToolStripMenuItem? _cancelDictationItem;
    private SettingsForm? _settingsForm;
    private SessionHistoryForm? _historyForm;
    private AppSettings _settings = AppSettings.Default;
    private bool _exiting;

    public TrayApplicationContext()
        : this(null)
    {
    }

    internal TrayApplicationContext(Func<SettingsForm>? settingsFormFactory)
    {
        _settingsFormFactory = settingsFormFactory
            ?? (() => new SettingsForm(_settingsStore, _modelRegistry));
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var staleAudioCleanupFailures = AudioResidueCleaner.DeleteStaleFiles(AppPaths.RootDirectory);
        _recorder = new WasapiAudioRecorder(AppPaths.RootDirectory);
        _recorder.AudioLevelChanged += OnAudioLevelChanged;
        DictationWorkflow? workflow = null;
        _transcriber = new LazyAssetTranscriber(
            AppPaths.RootDirectory,
            _settingsStore,
            () => _settings,
            settings => _settings = settings,
            message => workflow?.ReportProcessingDetail(message));
        workflow = new DictationWorkflow(
            new ChunkedTranscribingDictationSessionFactory(_recorder, _transcriber, _transcriber),
            new ClipboardPaster(),
            _history,
            getTranscriptCorrections: () => _settings.TranscriptCorrections);
        _dictationWorkflow = workflow;
        _statusSoundPlayer = new StatusSoundPlayer(() => _settings);

        _trayIcon = TrayIconFactory.Create();
        _notifyIcon = new NotifyIcon
        {
            Icon = _trayIcon,
            Text = "PTT Dictation",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _dictationPresentation = new DictationPresentation(
            _statusOverlay,
            _cancelDictationItem ?? throw new InvalidOperationException("The dictation cancellation menu item was not created."),
            new DictationPresentationEnvironment(
                GetSettings: () => _settings,
                GetCurrentState: () => _dictationWorkflow.CurrentState,
                IsUnavailable: () => _exiting,
                RefreshHistory: () => _historyForm?.RefreshItems(),
                ShowCleanupWarning,
                ShowTrayNotification,
                PlayStatusSound: _statusSoundPlayer.Play,
                DelayAsync: Task.Delay));
        _dictationWorkflow.StateChanged += OnDictationStateChanged;

        _notifyIcon.DoubleClick += (_, _) => ShowSettings();
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowSettings();
            }
        };
        _hotkeySource = new GlobalHotkeySource();
        _hotkeySource.Pressed += () => PostToUi(OnHotkeyPressedAsync);
        _hotkeySource.Released += () => PostToUi(OnHotkeyReleasedAsync);
        _hotkeySource.ToggleRequested += () => PostToUi(OnToggleRequestedAsync);
        _hotkeySource.Start();
        LoadSettingsAtStartup();
        if (staleAudioCleanupFailures.Count > 0)
        {
            ShowCleanupWarning(staleAudioCleanupFailures[0]);
        }
    }

    internal void OpenSettings()
    {
        ShowSettings();
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
                _statusSoundPlayer.Play(StatusSound.Error);
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
        menu.Items.Add(new ToolStripMenuItem("Play Test Sound", null, (_, _) => _statusSoundPlayer.Play(StatusSound.Listening)));
        _cancelDictationItem = new ToolStripMenuItem(
            "Cancel Current Dictation",
            null,
            (_, _) => PostToUi(() => _dictationWorkflow.HandleAsync(DictationIntent.Cancel, _lifetime.Token)))
        {
            Enabled = false
        };
        menu.Items.Add(_cancelDictationItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit PTT Dictation", null, (_, _) => ExitApplication()));

        return menu;
    }

    private void LoadSettingsAtStartup()
    {
        try
        {
            ApplySettings(_settingsStore.Load());
        }
        catch (Exception ex)
        {
            ShowTrayNotification("Settings were not loaded", ex.Message, ToolTipIcon.Warning);
        }
    }

    private void ShowSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = CreateSettingsForm();
        }

        _settingsForm = PresentSettingsForm(_settingsForm, CreateSettingsForm, _settings);
    }

    private SettingsForm CreateSettingsForm()
    {
        var form = _settingsFormFactory();
        form.SettingsSaved += (_, settings) =>
        {
            ApplySettings(settings);
            ShowTrayNotification("Settings saved", "PTT Dictation settings were updated.", ToolTipIcon.Info);
        };
        form.QuitRequested += (_, _) => ExitApplication();
        return form;
    }

    internal static SettingsForm PresentSettingsForm(
        SettingsForm? form,
        Func<SettingsForm> createForm,
        AppSettings settings)
    {
        if (form is { IsDisposed: false, Visible: true })
        {
            form.Activate();
            form.BringToFront();
            return form;
        }

        if (form is null || form.IsDisposed)
        {
            form = createForm();
        }

        form.UseSettings(settings);
        form.Show();
        form.Activate();
        form.BringToFront();
        return form;
    }

    internal SettingsForm? SettingsFormForTest => _settingsForm;

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

        await _dictationWorkflow.HandleAsync(DictationIntent.BeginHold, _lifetime.Token);
    }

    private async Task OnHotkeyReleasedAsync()
    {
        if (_exiting)
        {
            return;
        }

        await _dictationWorkflow.HandleAsync(DictationIntent.EndHold, _lifetime.Token);
    }

    private async Task OnToggleRequestedAsync()
    {
        if (_exiting)
        {
            return;
        }

        await _dictationWorkflow.HandleAsync(DictationIntent.Toggle, _lifetime.Token);
    }

    private void OnDictationStateChanged(DictationWorkflowState state)
    {
        PostToUi(() => _dictationPresentation.ApplyAsync(state));
    }

    private void UpdateTrayText()
    {
        var model = _modelRegistry.Find(_settings.SelectedModelId) ?? _modelRegistry.DefaultModel;
        var text = $"PTT Dictation - {model.DisplayName}";
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
        ListeningTriggerMode mode = ListeningTriggerMode.PushToTalk,
        string? listeningHotkeyName = null)
    {
        _statusOverlay.ShowStatus(status, mode, listeningHotkeyName);
        if (notify)
        {
            ShowTrayNotification(status.Title, notifyMessage ?? status.Message, icon);
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        _hotkeySource.Configure(settings.HoldHotkey, settings.ToggleHotkey);
        _settings = settings;
        UpdateTrayText();
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

    private void ExitApplication()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _lifetime.Cancel();
        _dictationWorkflow.StateChanged -= OnDictationStateChanged;
        _recorder.AudioLevelChanged -= OnAudioLevelChanged;
        _hotkeySource.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _trayIcon.Dispose();
        _statusOverlay.Dispose();
        _settingsForm?.Dispose();
        _historyForm?.Dispose();
        _transcriber.Dispose();
        _ = Task.Run(_recorder.Dispose);
        _lifetime.Dispose();
        ExitThread();
    }
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
