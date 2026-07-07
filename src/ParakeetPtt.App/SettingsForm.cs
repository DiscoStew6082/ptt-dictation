using ParakeetPtt.Core;
using System.ComponentModel;

namespace ParakeetPtt.App;

internal sealed class SettingsForm : Form
{
    private readonly AppSettingsStore _settingsStore;
    private readonly ModelRegistry _modelRegistry;
    private readonly Func<ModelInfo, CancellationToken, Task<string>> _downloadModelAsync;
    private readonly Func<ModelInfo, bool> _isModelDownloaded;
    private readonly ComboBox _model = new();
    private readonly Button _downloadModel = DarkTheme.Button("Download");
    private readonly Label _summary = new();
    private readonly Label _modelStatus = DarkTheme.HelpText(string.Empty);
    private readonly TextBox _hotkey = new();
    private readonly ComboBox _mode = new();
    private readonly ComboBox _device = new();
    private readonly CheckBox _notifications = new();
    private readonly CheckBox _sounds = new();
    private readonly ListBox _corrections = new();
    private readonly TextBox _correctionHeardAs = new();
    private readonly TextBox _correctionReplaceWith = new();
    private readonly TextBox _correctionPreviewInput = new();
    private readonly TextBox _correctionPreviewOutput = new();
    private AppSettings _settings = AppSettings.Default;
    private string? _runtimePathOverride;
    private string? _modelPathOverride;
    private List<TranscriptCorrection> _transcriptCorrections = [];

    public event EventHandler<AppSettings>? SettingsSaved;
    public event EventHandler? QuitRequested;

    public SettingsForm(AppSettingsStore settingsStore, ModelRegistry modelRegistry)
        : this(settingsStore, modelRegistry, DownloadModelWithDefaultManagerAsync, DefaultModelIsDownloaded)
    {
    }

    internal SettingsForm(
        AppSettingsStore settingsStore,
        ModelRegistry modelRegistry,
        Func<ModelInfo, CancellationToken, Task<string>> downloadModelAsync,
        Func<ModelInfo, bool> isModelDownloaded)
    {
        _settingsStore = settingsStore;
        _modelRegistry = modelRegistry;
        _downloadModelAsync = downloadModelAsync;
        _isModelDownloaded = isModelDownloaded;

        Text = "Parakeet PTT - Settings";
        MinimumSize = new Size(640, 560);
        Size = new Size(720, 620);

        DarkTheme.Apply(this);
        BuildLayout();
    }

    public async Task LoadSettingsAsync(CancellationToken cancellationToken)
    {
        _settings = await _settingsStore.LoadAsync(cancellationToken);
        UseSettings(_settings);
    }

    public void UseSettings(AppSettings settings)
    {
        _settings = settings;
        ApplySettings(settings);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(20),
            BackColor = DarkTheme.Background,
            AutoScroll = true
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Parakeet PTT Settings",
            AutoSize = true,
            Font = DarkTheme.HeaderFont,
            ForeColor = DarkTheme.Text,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6)
        };

        _summary.Text = "Hold Right Ctrl to record. Press Right Shift to toggle recording on or off.";
        _summary.AutoSize = false;
        _summary.Height = 44;
        _summary.Dock = DockStyle.Top;
        _summary.ForeColor = DarkTheme.MutedText;
        _summary.BackColor = Color.Transparent;
        _summary.Margin = new Padding(0, 0, 0, 16);

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            BackColor = DarkTheme.Background
        };

        _hotkey.Dock = DockStyle.Top;
        _hotkey.PlaceholderText = "RightCtrl";

        _model.Dock = DockStyle.Top;
        _model.DropDownStyle = ComboBoxStyle.DropDownList;
        _model.DisplayMember = nameof(ModelInfo.DisplayName);
        _model.Items.AddRange(_modelRegistry.Models.Cast<object>().ToArray());
        _model.SelectedIndexChanged += (_, _) =>
        {
            var selectedModel = SelectedModelFromControl();
            RefreshModeOptions(selectedModel);
            RefreshModelDownloadState(selectedModel);
        };

        _mode.Dock = DockStyle.Top;
        _mode.DropDownStyle = ComboBoxStyle.DropDownList;

        _device.Dock = DockStyle.Top;
        _device.DropDownStyle = ComboBoxStyle.DropDownList;
        _device.Items.AddRange(Enum.GetValues<DevicePreference>().Cast<object>().ToArray());

        _notifications.Text = "Show tray notifications";
        _notifications.AutoSize = true;
        _notifications.BackColor = Color.Transparent;
        _notifications.ForeColor = DarkTheme.Text;
        _notifications.Margin = new Padding(0, 12, 0, 8);
        _sounds.Text = "Play sounds when recording starts, transcription begins, and paste completes";
        _sounds.AutoSize = true;
        _sounds.BackColor = Color.Transparent;
        _sounds.ForeColor = DarkTheme.Text;
        _sounds.Margin = new Padding(0, 4, 0, 8);

        AddField(fields, "Push-to-talk hotkey", _hotkey);
        AddModelField(fields);
        AddField(fields, "Transcription mode", _mode);
        AddField(fields, "Device preference", _device);
        AddCorrectionEditor(fields);
        fields.Controls.Add(_notifications);
        fields.Controls.Add(_sounds);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            BackColor = DarkTheme.Background
        };

        var save = DarkTheme.Button("Save");
        save.Width = 96;
        save.BackColor = DarkTheme.Accent;
        save.Click += async (_, _) => await SaveAsync();

        var quit = DarkTheme.Button("Quit App");
        quit.Width = 96;
        quit.BackColor = DarkTheme.Danger;
        quit.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        var cancel = DarkTheme.Button("Cancel");
        cancel.Width = 96;
        cancel.Click += (_, _) => Hide();

        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(quit);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            BackColor = DarkTheme.Background
        };
        header.Controls.Add(title);
        header.Controls.Add(_summary);

        root.Controls.Add(header, 0, 0);
        root.Controls.Add(fields, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        DarkTheme.Apply(root);
    }

    private static void AddField(TableLayoutPanel fields, string labelText, Control control)
    {
        fields.Controls.Add(DarkTheme.Label(labelText));
        control.Height = 30;
        fields.Controls.Add(control);
    }

    private void AddModelField(TableLayoutPanel fields)
    {
        fields.Controls.Add(DarkTheme.Label("Model"));

        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            Height = 34,
            BackColor = DarkTheme.Background,
            Margin = new Padding(0, 0, 0, 2)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));

        _downloadModel.Dock = DockStyle.Fill;
        _downloadModel.Margin = new Padding(8, 0, 0, 0);
        _downloadModel.Click += async (_, _) => await DownloadSelectedModelAsync();

        row.Controls.Add(_model, 0, 0);
        row.Controls.Add(_downloadModel, 1, 0);
        fields.Controls.Add(row);
        _modelStatus.Height = 52;
        fields.Controls.Add(_modelStatus);
    }

    private void AddCorrectionEditor(TableLayoutPanel fields)
    {
        fields.Controls.Add(DarkTheme.Label("Corrections"));

        _corrections.Dock = DockStyle.Top;
        _corrections.Height = 76;
        _corrections.DisplayMember = nameof(CorrectionListItem.DisplayText);
        fields.Controls.Add(_corrections);

        var editRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            Height = 34,
            BackColor = DarkTheme.Background,
            Margin = new Padding(0, 6, 0, 2)
        };
        editRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        editRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        editRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        editRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));

        _correctionHeardAs.Dock = DockStyle.Fill;
        _correctionHeardAs.PlaceholderText = "Heard as";
        _correctionReplaceWith.Dock = DockStyle.Fill;
        _correctionReplaceWith.PlaceholderText = "Replace with";

        var add = DarkTheme.Button("Add");
        add.Dock = DockStyle.Fill;
        add.Margin = new Padding(8, 0, 0, 0);
        add.Click += (_, _) => AddCorrection();

        var delete = DarkTheme.Button("Delete");
        delete.Dock = DockStyle.Fill;
        delete.Margin = new Padding(8, 0, 0, 0);
        delete.Click += (_, _) => DeleteSelectedCorrection();

        editRow.Controls.Add(_correctionHeardAs, 0, 0);
        editRow.Controls.Add(_correctionReplaceWith, 1, 0);
        editRow.Controls.Add(add, 2, 0);
        editRow.Controls.Add(delete, 3, 0);
        fields.Controls.Add(editRow);

        fields.Controls.Add(DarkTheme.Label("Correction preview"));

        _correctionPreviewInput.Dock = DockStyle.Top;
        _correctionPreviewInput.Height = 48;
        _correctionPreviewInput.Multiline = true;
        _correctionPreviewInput.PlaceholderText = "Preview text";
        _correctionPreviewInput.TextChanged += (_, _) => RefreshCorrectionPreview();
        fields.Controls.Add(_correctionPreviewInput);

        _correctionPreviewOutput.Dock = DockStyle.Top;
        _correctionPreviewOutput.Height = 48;
        _correctionPreviewOutput.Multiline = true;
        _correctionPreviewOutput.ReadOnly = true;
        _correctionPreviewOutput.PlaceholderText = "Corrected preview";
        _correctionPreviewOutput.Margin = new Padding(0, 4, 0, 8);
        fields.Controls.Add(_correctionPreviewOutput);
    }

    private void ApplySettings(AppSettings settings)
    {
        _hotkey.Text = settings.Hotkey;
        _runtimePathOverride = settings.RuntimePath;
        _modelPathOverride = settings.ModelPath;
        _transcriptCorrections = settings.TranscriptCorrections.ToList();
        _device.SelectedItem = settings.DevicePreference;
        _mode.SelectedItem = settings.TranscriptionMode;
        _notifications.Checked = settings.NotificationsEnabled;
        _sounds.Checked = settings.AudibleStatusEnabled;

        var selected = _modelRegistry.Find(settings.SelectedModelId) ?? _modelRegistry.DefaultModel;
        _model.SelectedItem = selected;
        RefreshModeOptions(selected);
        _mode.SelectedItem = ModeSupportedByModel(settings.TranscriptionMode, selected)
            ? settings.TranscriptionMode
            : TranscriptionMode.Auto;
        RefreshModelDownloadState(selected);
        RefreshCorrectionsList();
        RefreshCorrectionPreview();
    }

    private async Task SaveAsync()
    {
        _settings = BuildSettingsFromControls();

        await _settingsStore.SaveAsync(_settings, CancellationToken.None);
        SettingsSaved?.Invoke(this, _settings);
        Hide();
    }

    private AppSettings BuildSettingsFromControls()
    {
        var selectedModelId = SelectedModelIdFromControl();
        var selectedModel = SelectedModelFromControl();
        var requestedMode = _mode.SelectedItem is TranscriptionMode mode ? mode : TranscriptionMode.Auto;
        var selectedMode = ModeSupportedByModel(requestedMode, selectedModel)
            ? requestedMode
            : TranscriptionMode.Auto;
        var modelPath = EmptyToNull(_modelPathOverride);
        if (!string.Equals(selectedModelId, _settings.SelectedModelId, StringComparison.OrdinalIgnoreCase)
            && IsPreviousAutoModelPath(modelPath))
        {
            modelPath = null;
        }

        return _settings with
        {
            Hotkey = string.IsNullOrWhiteSpace(_hotkey.Text) ? AppSettings.Default.Hotkey : _hotkey.Text.Trim(),
            SelectedModelId = selectedModelId,
            TranscriptionMode = selectedMode,
            RuntimePath = EmptyToNull(_runtimePathOverride),
            ModelPath = modelPath,
            DevicePreference = _device.SelectedItem is DevicePreference preference ? preference : DevicePreference.Cuda,
            NotificationsEnabled = _notifications.Checked,
            AudibleStatusEnabled = _sounds.Checked,
            TranscriptCorrections = _transcriptCorrections.ToList()
        };
    }

    private string SelectedModelIdFromControl()
    {
        return SelectedModelFromControl().Id;
    }

    private ModelInfo SelectedModelFromControl()
    {
        return _model.SelectedItem is ModelInfo model ? model : _modelRegistry.DefaultModel;
    }

    private void RefreshModeOptions(ModelInfo model)
    {
        var selected = _mode.SelectedItem is TranscriptionMode mode ? mode : TranscriptionMode.Auto;
        _mode.Items.Clear();
        _mode.Items.Add(TranscriptionMode.Auto);
        if (model.SupportsBatch)
        {
            _mode.Items.Add(TranscriptionMode.Batch);
        }

        if (model.SupportsStreaming)
        {
            _mode.Items.Add(TranscriptionMode.Streaming);
        }

        _mode.SelectedItem = ModeSupportedByModel(selected, model) ? selected : TranscriptionMode.Auto;
    }

    private void RefreshModelDownloadState(ModelInfo model)
    {
        var isDownloaded = ModelPathOverrideExists(model) || _isModelDownloaded(model);
        _downloadModel.Enabled = !isDownloaded;
        _downloadModel.Text = isDownloaded ? "Downloaded" : "Download";
        _modelStatus.Text = isDownloaded
            ? $"{model.LanguageNotes}. {model.Quantization} is ready locally."
            : $"{model.LanguageNotes}. {model.Quantization} downloads when you click Download or on first dictation.";
    }

    private async Task DownloadSelectedModelAsync()
    {
        var model = SelectedModelFromControl();
        var modelSelectorWasEnabled = _model.Enabled;
        _model.Enabled = false;
        _downloadModel.Enabled = false;
        _downloadModel.Text = "Downloading";
        _modelStatus.Text = $"Downloading {model.DisplayName}...";

        try
        {
            _modelPathOverride = await _downloadModelAsync(model, CancellationToken.None);
            _settings = BuildSettingsFromControls();
            await _settingsStore.SaveAsync(_settings, CancellationToken.None);
            SettingsSaved?.Invoke(this, _settings);
            _modelStatus.Text = $"{model.DisplayName} is ready locally.";
            RefreshModelDownloadState(model);
        }
        catch (Exception ex)
        {
            _modelStatus.Text = $"Download failed: {ex.Message}";
            _downloadModel.Enabled = true;
            _downloadModel.Text = "Download";
        }
        finally
        {
            _model.Enabled = modelSelectorWasEnabled;
        }
    }

    private static bool ModeSupportedByModel(TranscriptionMode mode, ModelInfo model)
    {
        return mode switch
        {
            TranscriptionMode.Batch => model.SupportsBatch,
            TranscriptionMode.Streaming => model.SupportsStreaming,
            _ => true
        };
    }

    private bool IsPreviousAutoModelPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var previousModel = _modelRegistry.Find(_settings.SelectedModelId);
        return previousModel is not null
            && string.Equals(
                Path.GetFileName(path),
                Path.GetFileName(previousModel.DownloadUrl.LocalPath),
                StringComparison.OrdinalIgnoreCase);
    }

    private bool ModelPathOverrideExists(ModelInfo model)
    {
        return string.Equals(model.Id, _settings.SelectedModelId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(_modelPathOverride)
            && File.Exists(_modelPathOverride);
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static Task<string> DownloadModelWithDefaultManagerAsync(ModelInfo model, CancellationToken cancellationToken)
    {
        var manager = new AssetManager(AppPaths.RootDirectory, new HttpFileDownloader());
        return manager.EnsureModelAsync(model, cancellationToken);
    }

    private static bool DefaultModelIsDownloaded(ModelInfo model)
    {
        var path = Path.Combine(AppPaths.RootDirectory, "models", Path.GetFileName(model.DownloadUrl.LocalPath));
        return File.Exists(path) && new FileInfo(path).Length >= model.MinimumBytes;
    }

    private void AddCorrection()
    {
        var heardAs = _correctionHeardAs.Text.Trim();
        var replaceWith = _correctionReplaceWith.Text.Trim();
        if (heardAs.Length == 0 || replaceWith.Length == 0)
        {
            return;
        }

        var replacement = new TranscriptCorrection(heardAs, replaceWith);
        var existing = _transcriptCorrections.FindIndex(
            correction => string.Equals(correction.HeardAs, heardAs, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            _transcriptCorrections[existing] = replacement;
        }
        else
        {
            _transcriptCorrections.Add(replacement);
        }

        RefreshCorrectionsList();
        RefreshCorrectionPreview();
    }

    private void DeleteSelectedCorrection()
    {
        if (_corrections.SelectedItem is not CorrectionListItem selected)
        {
            return;
        }

        _transcriptCorrections.Remove(selected.Correction);
        RefreshCorrectionsList();
        RefreshCorrectionPreview();
    }

    private void RefreshCorrectionsList()
    {
        _corrections.Items.Clear();
        foreach (var correction in _transcriptCorrections)
        {
            _corrections.Items.Add(new CorrectionListItem(correction));
        }
    }

    private void RefreshCorrectionPreview()
    {
        _correctionPreviewOutput.Text = new TranscriptCorrectionDictionary(_transcriptCorrections)
            .Apply(_correctionPreviewInput.Text);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal TranscriptionMode SelectedTranscriptionModeForTest
    {
        get => _mode.SelectedItem is TranscriptionMode mode ? mode : TranscriptionMode.Auto;
        set
        {
            if (!_mode.Items.Contains(value))
            {
                _mode.Items.Add(value);
            }

            _mode.SelectedItem = value;
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string SelectedModelIdForTest
    {
        get => SelectedModelIdFromControl();
        set
        {
            _model.SelectedItem = _modelRegistry.Find(value) ?? _modelRegistry.DefaultModel;
            RefreshModelDownloadState(SelectedModelFromControl());
        }
    }

    internal string SummaryTextForTest => _summary.Text;

    internal bool HasRuntimePathEditorForTest => false;

    internal bool HasModelPathEditorForTest => false;

    internal bool ModelDownloadEnabledForTest => _downloadModel.Enabled;

    internal string ModelDownloadTextForTest => _downloadModel.Text;

    internal string ModelStatusTextForTest => _modelStatus.Text;

    internal bool ModelSelectorEnabledForTest => _model.Enabled;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string CorrectionPreviewInputForTest
    {
        get => _correctionPreviewInput.Text;
        set => _correctionPreviewInput.Text = value;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string CorrectionPreviewOutputForTest => _correctionPreviewOutput.Text;

    internal void SetCorrectionDraftForTest(string heardAs, string replaceWith)
    {
        _correctionHeardAs.Text = heardAs;
        _correctionReplaceWith.Text = replaceWith;
    }

    internal void AddCorrectionForTest()
    {
        AddCorrection();
    }

    internal void DownloadSelectedModelForTest()
    {
        var task = DownloadSelectedModelAsync();
        while (!task.IsCompleted)
        {
            Application.DoEvents();
            Thread.Sleep(1);
        }

        task.GetAwaiter().GetResult();
    }

    internal Task DownloadSelectedModelTaskForTest()
    {
        return DownloadSelectedModelAsync();
    }

    internal void SaveForTest()
    {
        SaveAsync().GetAwaiter().GetResult();
    }

    internal AppSettings BuildSettingsForTest()
    {
        return BuildSettingsFromControls();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnFormClosing(e);
    }

    private sealed record CorrectionListItem(TranscriptCorrection Correction)
    {
        public string DisplayText => $"{Correction.HeardAs} -> {Correction.ReplaceWith}";
    }
}
