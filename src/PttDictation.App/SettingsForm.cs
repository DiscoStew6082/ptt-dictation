using PttDictation.Core;
using System.ComponentModel;

namespace PttDictation.App;

internal sealed class SettingsForm : Form
{
    private const int TwoColumnMinimumContentWidth = 1200;
    private readonly AppSettingsStore _settingsStore;
    private readonly ModelRegistry _modelRegistry;
    private readonly Func<ModelInfo, CancellationToken, Task<string>> _downloadModelAsync;
    private readonly Func<ModelInfo, bool> _isModelDownloaded;
    private readonly ComboBox _model = new();
    private readonly Button _downloadModel = DarkTheme.Button("Download");
    private readonly Label _summary = new();
    private readonly Label _modelStatus = DarkTheme.HelpText(string.Empty);
    private readonly ComboBox _holdHotkey = new();
    private readonly ComboBox _toggleHotkey = new();
    private readonly ComboBox _mode = new();
    private readonly ComboBox _device = new();
    private readonly CheckBox _notifications = new();
    private readonly CheckBox _sounds = new();
    private readonly ListBox _corrections = new();
    private readonly TextBox _correctionHeardAs = new();
    private readonly TextBox _correctionReplaceWith = new();
    private readonly TextBox _correctionPreviewInput = new();
    private readonly TextBox _correctionPreviewOutput = new();
    private readonly Button _save = DarkTheme.Button("Save");
    private readonly Button _cancel = DarkTheme.Button("Cancel");
    private readonly Button _quit = DarkTheme.Button("Quit app");
    private readonly Panel _contentHost = new();
    private readonly List<string> _sectionTitles = [];
    private TableLayoutPanel? _primarySections;
    private TableLayoutPanel? _recordingSection;
    private TableLayoutPanel? _transcriptionSection;
    private TableLayoutPanel? _correctionLayout;
    private TableLayoutPanel? _correctionFields;
    private TableLayoutPanel? _correctionPreview;
    private bool? _stackedLayout;
    private bool _initialWindowSizeApplied;
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

        Text = "PTT Dictation - Settings";
        MinimumSize = new Size(800, 700);
        Size = new Size(1100, 900);

        DarkTheme.Apply(this);
        BuildLayout();
        HandleCreated += (_, _) =>
        {
            ApplyInitialWindowSize();
            ApplyResponsiveLayout();
        };
        Shown += (_, _) => ApplyResponsiveLayout();
        DpiChanged += (_, _) => BeginInvoke(ApplyResponsiveLayout);
        _contentHost.ClientSizeChanged += (_, _) => ApplyResponsiveLayout();
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
            Padding = new Padding(20, 16, 20, 12),
            BackColor = DarkTheme.Background
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            BackColor = DarkTheme.Background,
            Margin = Padding.Empty
        };
        content.Controls.Add(BuildPrimarySections());
        content.Controls.Add(CreateSection(
            "Corrections",
            "Teach Parakeet how to replace names, acronyms, and frequently misheard phrases.",
            BuildCorrectionFields(),
            Padding.Empty));

        _contentHost.Dock = DockStyle.Fill;
        _contentHost.AutoScroll = true;
        _contentHost.BackColor = DarkTheme.Background;
        _contentHost.Margin = Padding.Empty;
        DarkTheme.ApplyNativeDarkTheme(_contentHost);
        _contentHost.Controls.Add(content);

        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(_contentHost, 0, 1);
        root.Controls.Add(BuildFooter(), 0, 2);
        Controls.Add(root);
        AcceptButton = _save;
    }

    private TableLayoutPanel BuildHeader()
    {
        var header = CreateStack(DarkTheme.Background);
        header.Controls.Add(new Label
        {
            Text = "PTT DICTATION",
            AutoSize = true,
            Font = new Font("Segoe UI Variable Text", 8F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = DarkTheme.Accent,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 3)
        });
        header.Controls.Add(new Label
        {
            Text = "Settings",
            AutoSize = true,
            Font = new Font("Segoe UI Variable Display", 18F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = DarkTheme.Text,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8)
        });

        RefreshHotkeySummary();
        _summary.AutoSize = false;
        _summary.Height = 36;
        _summary.Dock = DockStyle.Top;
        _summary.ForeColor = DarkTheme.Text;
        _summary.BackColor = DarkTheme.Surface;
        _summary.Padding = new Padding(12, 0, 12, 0);
        _summary.TextAlign = ContentAlignment.MiddleLeft;
        _summary.Margin = new Padding(0, 0, 0, 10);
        header.Controls.Add(_summary);
        return header;
    }

    private TableLayoutPanel BuildPrimarySections()
    {
        ConfigureControls();

        var recording = CreateStack(DarkTheme.Surface);
        AddField(recording, "Hold-to-talk key", _holdHotkey);
        AddField(recording, "Toggle-to-talk key", _toggleHotkey);
        recording.Controls.Add(_notifications);
        recording.Controls.Add(_sounds);

        var transcription = CreateStack(DarkTheme.Surface);
        AddModelField(transcription);
        AddTranscriptionOptions(transcription);

        _primarySections = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = DarkTheme.Background,
            Margin = new Padding(0, 0, 0, 12)
        };
        _primarySections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        _primarySections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        _primarySections.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _recordingSection = CreateSection(
            "Recording",
            "Hotkeys and feedback while you dictate.",
            recording,
            new Padding(0, 0, 6, 0));
        _transcriptionSection = CreateSection(
            "Transcription",
            "Local model and processing preferences.",
            transcription,
            new Padding(6, 0, 0, 0));
        _primarySections.Controls.Add(_recordingSection, 0, 0);
        _primarySections.Controls.Add(_transcriptionSection, 1, 0);
        return _primarySections;
    }

    private void ConfigureControls()
    {
        ConfigureHotkeySelector(_holdHotkey, AppSettings.Default.HoldHotkey);
        ConfigureHotkeySelector(_toggleHotkey, AppSettings.Default.ToggleHotkey);
        _holdHotkey.SelectedIndexChanged += (_, _) => RefreshHotkeySummary();
        _toggleHotkey.SelectedIndexChanged += (_, _) => RefreshHotkeySummary();

        StyleSelector(_model);
        _model.Dock = DockStyle.Top;
        _model.DisplayMember = nameof(ModelInfo.DisplayName);
        _model.Items.AddRange(_modelRegistry.Models.Cast<object>().ToArray());
        _model.SelectedIndexChanged += (_, _) =>
        {
            var selectedModel = SelectedModelFromControl();
            RefreshModeOptions(selectedModel);
            RefreshModelDownloadState(selectedModel);
        };

        StyleSelector(_mode);
        _mode.Dock = DockStyle.Top;

        StyleSelector(_device);
        _device.Dock = DockStyle.Top;
        _device.Items.AddRange(Enum.GetValues<DevicePreference>().Cast<object>().ToArray());

        ConfigureCheckBox(_notifications, "Show tray notifications", 30, new Padding(0, 10, 0, 0));
        ConfigureCheckBox(
            _sounds,
            "Play status sounds",
            42,
            Padding.Empty);
    }

    private static void ConfigureHotkeySelector(ComboBox selector, DictationHotkey selected)
    {
        StyleSelector(selector);
        selector.Dock = DockStyle.Top;
        selector.DisplayMember = nameof(DictationHotkeyOption.DisplayName);
        selector.Items.AddRange(DictationHotkeyCatalog.Options.Cast<object>().ToArray());
        selector.SelectedItem = DictationHotkeyCatalog.Option(selected);
    }

    private static void ConfigureCheckBox(CheckBox checkBox, string text, int height, Padding margin)
    {
        checkBox.Text = text;
        checkBox.AutoSize = false;
        checkBox.Dock = DockStyle.Top;
        checkBox.Height = height;
        checkBox.BackColor = Color.Transparent;
        checkBox.ForeColor = DarkTheme.Text;
        checkBox.Margin = margin;
    }

    private TableLayoutPanel BuildFooter()
    {
        ConfigureActionButton(_save, "Save", 96);
        _save.BackColor = DarkTheme.Accent;
        _save.Click += async (_, _) => await SaveAsync();
        _save.Margin = new Padding(8, 0, 0, 0);

        ConfigureActionButton(_cancel, "Cancel", 96);
        _cancel.Click += (_, _) => Hide();
        _cancel.Margin = Padding.Empty;

        ConfigureActionButton(_quit, "Quit app", 96);
        _quit.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);
        _quit.Margin = Padding.Empty;

        var rightButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            BackColor = DarkTheme.Background,
            Margin = Padding.Empty
        };
        rightButtons.Controls.Add(_save);
        rightButtons.Controls.Add(_cancel);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            BackColor = DarkTheme.Background,
            Padding = new Padding(0, 12, 0, 0),
            Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_quit, 0, 0);
        footer.Controls.Add(rightButtons, 1, 0);
        return footer;
    }

    private static void ConfigureActionButton(Button button, string text, int minimumWidth)
    {
        button.Text = text;
        button.AutoSize = true;
        button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        button.MinimumSize = new Size(minimumWidth, 36);
        button.Padding = new Padding(12, 0, 12, 0);
    }

    private TableLayoutPanel CreateSection(string title, string description, Control content, Padding margin)
    {
        _sectionTitles.Add(title);
        var section = CreateStack(DarkTheme.Surface);
        section.Dock = DockStyle.Fill;
        section.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        section.Padding = new Padding(14, 10, 14, 12);
        section.Margin = margin;
        section.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = new Font("Segoe UI Variable Display", 11F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = DarkTheme.Text,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 3)
        });
        section.Controls.Add(new Label
        {
            Text = description,
            AutoSize = false,
            Dock = DockStyle.Top,
            Height = title == "Corrections" ? 28 : 42,
            ForeColor = DarkTheme.MutedText,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 6)
        });
        section.Controls.Add(content);
        return section;
    }

    private static TableLayoutPanel CreateStack(Color background)
    {
        return new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = background,
            Margin = Padding.Empty
        };
    }

    private static void AddField(TableLayoutPanel fields, string labelText, Control control)
    {
        fields.Controls.Add(DarkTheme.Label(labelText));
        control.Height = 34;
        control.Margin = new Padding(0, 0, 0, 6);
        fields.Controls.Add(control);
    }

    private void AddModelField(TableLayoutPanel fields)
    {
        fields.Controls.Add(DarkTheme.Label("Model"));

        var row = new WidthConstrainedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = DarkTheme.Surface,
            Margin = new Padding(0, 0, 0, 2)
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _downloadModel.Dock = DockStyle.Fill;
        _downloadModel.AutoSize = true;
        _downloadModel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _downloadModel.MinimumSize = new Size(104, 36);
        _downloadModel.Padding = new Padding(10, 0, 10, 0);
        _downloadModel.Margin = new Padding(8, 0, 0, 0);
        _downloadModel.Click += async (_, _) => await DownloadSelectedModelAsync();

        row.Controls.Add(_model, 0, 0);
        row.Controls.Add(_downloadModel, 1, 0);
        fields.Controls.Add(row);
        _modelStatus.Height = 42;
        _modelStatus.BackColor = Color.Transparent;
        _modelStatus.Margin = new Padding(0, 4, 0, 8);
        fields.Controls.Add(_modelStatus);
    }

    private void AddTranscriptionOptions(TableLayoutPanel fields)
    {
        var options = new WidthConstrainedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = DarkTheme.Surface,
            Margin = Padding.Empty
        };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        var modeLabel = DarkTheme.Label("Mode");
        modeLabel.Margin = new Padding(0, 0, 6, 3);
        var deviceLabel = DarkTheme.Label("Device");
        deviceLabel.Margin = new Padding(6, 0, 0, 3);
        _mode.Margin = new Padding(0, 0, 6, 0);
        _device.Margin = new Padding(6, 0, 0, 0);

        options.Controls.Add(modeLabel, 0, 0);
        options.Controls.Add(deviceLabel, 1, 0);
        options.Controls.Add(_mode, 0, 1);
        options.Controls.Add(_device, 1, 1);
        fields.Controls.Add(options);
    }

    private TableLayoutPanel BuildCorrectionFields()
    {
        _correctionFields = CreateStack(DarkTheme.Surface);
        StyleInput(_corrections);

        _corrections.Dock = DockStyle.Top;
        _corrections.Height = 76;
        _corrections.DisplayMember = nameof(CorrectionListItem.DisplayText);
        _corrections.Margin = new Padding(0, 0, 0, 8);
        _correctionFields.Controls.Add(_corrections);

        var editRow = new WidthConstrainedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = DarkTheme.Surface,
            Margin = new Padding(0, 0, 0, 8)
        };
        editRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        editRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        editRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        editRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        StyleInput(_correctionHeardAs);
        _correctionHeardAs.Dock = DockStyle.Fill;
        _correctionHeardAs.PlaceholderText = "Heard as";
        _correctionHeardAs.Margin = new Padding(0, 0, 4, 0);
        StyleInput(_correctionReplaceWith);
        _correctionReplaceWith.Dock = DockStyle.Fill;
        _correctionReplaceWith.PlaceholderText = "Replace with";
        _correctionReplaceWith.Margin = new Padding(4, 0, 0, 0);

        var add = DarkTheme.Button("Add");
        ConfigureActionButton(add, "Add", 64);
        add.Margin = new Padding(8, 0, 0, 0);
        add.Click += (_, _) => AddCorrection();

        var delete = DarkTheme.Button("Delete");
        ConfigureActionButton(delete, "Delete", 72);
        delete.Margin = new Padding(8, 0, 0, 0);
        delete.Click += (_, _) => DeleteSelectedCorrection();

        editRow.Controls.Add(_correctionHeardAs, 0, 0);
        editRow.Controls.Add(_correctionReplaceWith, 1, 0);
        editRow.Controls.Add(add, 2, 0);
        editRow.Controls.Add(delete, 3, 0);
        _correctionFields.Controls.Add(editRow);

        _correctionLayout = new WidthConstrainedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = DarkTheme.Surface,
            Margin = Padding.Empty
        };
        _correctionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _correctionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _correctionFields.Margin = new Padding(0, 0, 8, 0);
        _correctionPreview = BuildCorrectionPreview();
        _correctionLayout.Controls.Add(_correctionFields, 0, 0);
        _correctionLayout.Controls.Add(_correctionPreview, 1, 0);
        return _correctionLayout;
    }

    private TableLayoutPanel BuildCorrectionPreview()
    {
        StyleInput(_correctionPreviewInput);
        _correctionPreviewInput.Dock = DockStyle.Fill;
        _correctionPreviewInput.Multiline = true;
        _correctionPreviewInput.MinimumSize = new Size(0, 42);
        _correctionPreviewInput.PlaceholderText = "Type a phrase to test your corrections";
        _correctionPreviewInput.TextChanged += (_, _) => RefreshCorrectionPreview();

        StyleInput(_correctionPreviewOutput);
        _correctionPreviewOutput.Dock = DockStyle.Fill;
        _correctionPreviewOutput.Multiline = true;
        _correctionPreviewOutput.MinimumSize = new Size(0, 42);
        _correctionPreviewOutput.ReadOnly = true;
        _correctionPreviewOutput.PlaceholderText = "Corrected result";

        var preview = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            MinimumSize = new Size(0, 160),
            BackColor = DarkTheme.Surface,
            Margin = new Padding(8, 0, 0, 0)
        };
        preview.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        preview.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        preview.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        preview.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

        var beforeLabel = DarkTheme.Label("Test phrase");
        beforeLabel.Margin = new Padding(0, 0, 0, 3);
        var afterLabel = DarkTheme.Label("Corrected result");
        afterLabel.Margin = new Padding(0, 4, 0, 3);
        _correctionPreviewInput.Margin = Padding.Empty;
        _correctionPreviewOutput.Margin = Padding.Empty;
        preview.Controls.Add(beforeLabel, 0, 0);
        preview.Controls.Add(_correctionPreviewInput, 0, 1);
        preview.Controls.Add(afterLabel, 0, 2);
        preview.Controls.Add(_correctionPreviewOutput, 0, 3);
        return preview;
    }

    private static void StyleInput(Control control)
    {
        control.BackColor = DarkTheme.SurfaceRaised;
        control.ForeColor = DarkTheme.Text;
        control.Font = DarkTheme.BodyFont;
    }

    private static void StyleSelector(ComboBox selector)
    {
        StyleInput(selector);
        selector.DropDownStyle = ComboBoxStyle.DropDownList;
        selector.FlatStyle = FlatStyle.Flat;
    }

    private void ApplyInitialWindowSize()
    {
        if (_initialWindowSizeApplied)
        {
            return;
        }

        _initialWindowSizeApplied = true;
        if (DeviceDpi < 144)
        {
            return;
        }

        var workingArea = Screen.FromControl(this).WorkingArea;
        var width = Math.Max(MinimumSize.Width, Math.Min(1400, workingArea.Width - 64));
        var height = Math.Max(MinimumSize.Height, Math.Min(1000, workingArea.Height - 64));
        Size = new Size(width, height);
        Location = new Point(
            workingArea.Left + Math.Max(0, (workingArea.Width - width) / 2),
            workingArea.Top + Math.Max(0, (workingArea.Height - height) / 2));
    }

    private void ApplyResponsiveLayout()
    {
        ApplyResponsiveLayout(_contentHost.ClientSize.Width < TwoColumnMinimumContentWidth);
    }

    private void ApplyResponsiveLayout(bool stacked)
    {
        if (_stackedLayout == stacked
            || _primarySections is null
            || _recordingSection is null
            || _transcriptionSection is null
            || _correctionLayout is null
            || _correctionFields is null
            || _correctionPreview is null)
        {
            return;
        }

        SuspendLayout();
        _contentHost.SuspendLayout();
        try
        {
            ConfigurePrimarySectionLayout(stacked);
            ConfigureCorrectionLayout(stacked);
            _stackedLayout = stacked;
        }
        finally
        {
            _contentHost.ResumeLayout(performLayout: true);
            ResumeLayout(performLayout: true);
        }
    }

    private void ConfigurePrimarySectionLayout(bool stacked)
    {
        if (_primarySections is null || _recordingSection is null || _transcriptionSection is null)
        {
            return;
        }

        _primarySections.ColumnStyles.Clear();
        _primarySections.RowStyles.Clear();
        _primarySections.ColumnCount = stacked ? 1 : 2;
        _primarySections.RowCount = stacked ? 2 : 1;
        if (stacked)
        {
            _primarySections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _primarySections.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _primarySections.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _recordingSection.Margin = new Padding(0, 0, 0, 6);
            _transcriptionSection.Margin = new Padding(0, 6, 0, 0);
            _primarySections.SetCellPosition(_recordingSection, new TableLayoutPanelCellPosition(0, 0));
            _primarySections.SetCellPosition(_transcriptionSection, new TableLayoutPanelCellPosition(0, 1));
        }
        else
        {
            _primarySections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            _primarySections.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            _primarySections.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _recordingSection.Margin = new Padding(0, 0, 6, 0);
            _transcriptionSection.Margin = new Padding(6, 0, 0, 0);
            _primarySections.SetCellPosition(_recordingSection, new TableLayoutPanelCellPosition(0, 0));
            _primarySections.SetCellPosition(_transcriptionSection, new TableLayoutPanelCellPosition(1, 0));
        }
    }

    private void ConfigureCorrectionLayout(bool stacked)
    {
        if (_correctionLayout is null || _correctionFields is null || _correctionPreview is null)
        {
            return;
        }

        _correctionLayout.ColumnStyles.Clear();
        _correctionLayout.RowStyles.Clear();
        _correctionLayout.ColumnCount = stacked ? 1 : 2;
        _correctionLayout.RowCount = stacked ? 2 : 1;
        if (stacked)
        {
            _correctionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _correctionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _correctionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _correctionFields.Margin = new Padding(0, 0, 0, 8);
            _correctionPreview.Margin = new Padding(0, 8, 0, 12);
            _correctionLayout.SetCellPosition(_correctionFields, new TableLayoutPanelCellPosition(0, 0));
            _correctionLayout.SetCellPosition(_correctionPreview, new TableLayoutPanelCellPosition(0, 1));
        }
        else
        {
            _correctionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _correctionLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            _correctionLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            _correctionFields.Margin = new Padding(0, 0, 8, 0);
            _correctionPreview.Margin = new Padding(8, 0, 0, 12);
            _correctionLayout.SetCellPosition(_correctionFields, new TableLayoutPanelCellPosition(0, 0));
            _correctionLayout.SetCellPosition(_correctionPreview, new TableLayoutPanelCellPosition(1, 0));
        }
    }

    private sealed class WidthConstrainedTableLayoutPanel : TableLayoutPanel
    {
        public override Size GetPreferredSize(Size proposedSize)
        {
            var preferred = base.GetPreferredSize(proposedSize);
            return new Size(0, preferred.Height);
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        SelectHotkey(_holdHotkey, settings.HoldHotkey);
        SelectHotkey(_toggleHotkey, settings.ToggleHotkey);
        RefreshHotkeySummary();
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
        try
        {
            _settings = BuildSettingsFromControls();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(this, ex.Message, "Choose different hotkeys", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

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

        var holdHotkey = SelectedHotkeyFromControl(_holdHotkey, AppSettings.Default.HoldHotkey);
        var toggleHotkey = SelectedHotkeyFromControl(_toggleHotkey, AppSettings.Default.ToggleHotkey);
        if (holdHotkey == toggleHotkey)
        {
            throw new InvalidOperationException("Choose different keys for hold-to-talk and toggle-to-talk.");
        }

        return _settings with
        {
            HoldHotkey = holdHotkey,
            ToggleHotkey = toggleHotkey,
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

    private void RefreshHotkeySummary()
    {
        var hold = SelectedHotkeyFromControl(_holdHotkey, AppSettings.Default.HoldHotkey);
        var toggle = SelectedHotkeyFromControl(_toggleHotkey, AppSettings.Default.ToggleHotkey);
        _summary.Text = $"Hold {DictationHotkeyCatalog.DisplayName(hold)} to record   •   {DictationHotkeyCatalog.DisplayName(toggle)} toggles recording";
    }

    private static void SelectHotkey(ComboBox selector, DictationHotkey hotkey)
    {
        selector.SelectedItem = DictationHotkeyCatalog.Option(hotkey);
    }

    private static DictationHotkey SelectedHotkeyFromControl(ComboBox selector, DictationHotkey fallback)
    {
        return selector.SelectedItem is DictationHotkeyOption option ? option.Value : fallback;
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

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal DictationHotkey SelectedHoldHotkeyForTest
    {
        get => SelectedHotkeyFromControl(_holdHotkey, AppSettings.Default.HoldHotkey);
        set => SelectHotkey(_holdHotkey, value);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal DictationHotkey SelectedToggleHotkeyForTest
    {
        get => SelectedHotkeyFromControl(_toggleHotkey, AppSettings.Default.ToggleHotkey);
        set => SelectHotkey(_toggleHotkey, value);
    }

    internal bool HasRuntimePathEditorForTest => false;

    internal bool HasModelPathEditorForTest => false;

    internal string[] SectionTitlesForTest => [.. _sectionTitles];

    internal bool SelectorsUseDarkFlatStyleForTest =>
        new[] { _holdHotkey, _toggleHotkey, _model, _mode, _device }.All(selector =>
            selector.FlatStyle == FlatStyle.Flat
            && selector.BackColor == DarkTheme.SurfaceRaised
            && selector.ForeColor == DarkTheme.Text);

    internal bool HotkeySelectorsUseDarkFlatStyleForTest =>
        new[] { _holdHotkey, _toggleHotkey }.All(selector =>
            selector.FlatStyle == FlatStyle.Flat
            && selector.BackColor == DarkTheme.SurfaceRaised
            && selector.ForeColor == DarkTheme.Text);

    internal Button ModelDownloadButtonForTest => _downloadModel;

    internal Button SaveButtonForTest => _save;

    internal Button CancelButtonForTest => _cancel;

    internal Button QuitButtonForTest => _quit;

    internal Color SummaryBackColorForTest => _summary.BackColor;

    internal Color SaveBackColorForTest => _save.BackColor;

    internal bool ContentHasHorizontalScrollForTest => _contentHost.HorizontalScroll.Visible;

    internal bool PrimarySectionsFitContentForTest =>
        _recordingSection is not null
        && _transcriptionSection is not null
        && DescendantsFit(_recordingSection)
        && DescendantsFit(_transcriptionSection);

    internal string ContentLayoutForTest =>
        $"host={_contentHost.ClientSize}, content={_contentHost.Controls[0].Size}, display={_contentHost.DisplayRectangle.Size}";

    internal int PrimarySectionColumnCountForTest => _primarySections?.ColumnCount ?? 0;

    internal int CorrectionColumnCountForTest => _correctionLayout?.ColumnCount ?? 0;

    internal bool CorrectionPreviewFitsForTest =>
        _correctionPreview is not null
        && _correctionPreviewInput.Height >= 42
        && _correctionPreviewOutput.Height >= 42
        && _correctionPreview.Margin.Bottom >= 12
        && DescendantsFit(_correctionPreview);

    internal void ApplyHighDpiLayoutForTest()
    {
        ApplyResponsiveLayout(stacked: true);
    }

    internal void ApplyWideLayoutForTest()
    {
        ApplyResponsiveLayout(stacked: false);
    }

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

    private static bool DescendantsFit(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child.Bounds.Left < 0
                || child.Bounds.Top < 0
                || child.Bounds.Right > parent.ClientSize.Width
                || child.Bounds.Bottom > parent.ClientSize.Height
                || !DescendantsFit(child))
            {
                return false;
            }
        }

        return true;
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
