using PttDictation.Core;
using System.ComponentModel;

namespace PttDictation.App;

internal sealed class SettingsForm : Form
{
    private const int TwoColumnMinimumContentWidth = 1200;
    private const int PreferredWindowWidth = 1400;
    private const int WorkingAreaMargin = 32;
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
    private readonly DataGridView _corrections = new();
    private readonly TextBox _correctionHeardAs = new();
    private readonly TextBox _correctionReplaceWith = new();
    private readonly TextBox _correctionPreviewInput = new();
    private readonly Label _correctionPreviewResultLabel = DarkTheme.Label("Corrected result");
    private readonly Label _correctionPreviewOutput = new();
    private readonly Button _correctionAction = DarkTheme.Button("Add rule");
    private readonly Button _newCorrection = DarkTheme.Button("New rule");
    private readonly Button _deleteCorrection = DarkTheme.Button("Remove selected");
    private readonly Label _saveStatus = DarkTheme.HelpText("Changes are not active until saved.");
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
    private TranscriptCorrection? _selectedCorrection;
    private bool _refreshingCorrectionEditor;

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
            Padding = new Padding(20, 12, 20, 8),
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
            "Replace exact words or phrases after transcription. This does not retrain the speech model.",
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
        _summary.Margin = new Padding(0, 0, 0, 6);
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
            Margin = new Padding(0, 0, 0, 8)
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
            30,
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

        ConfigureActionButton(_cancel, "Close", 96);
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

        _saveStatus.Dock = DockStyle.Fill;
        _saveStatus.Height = 36;
        _saveStatus.TextAlign = ContentAlignment.MiddleRight;
        _saveStatus.Margin = new Padding(12, 0, 12, 0);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            AutoSize = true,
            BackColor = DarkTheme.Background,
            Padding = new Padding(0, 12, 0, 0),
            Margin = Padding.Empty
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_quit, 0, 0);
        footer.Controls.Add(_saveStatus, 1, 0);
        footer.Controls.Add(rightButtons, 2, 0);
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
            Height = 28,
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
        control.Height = 32;
        control.Margin = new Padding(0, 0, 0, 4);
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
        _modelStatus.Height = 30;
        _modelStatus.BackColor = Color.Transparent;
        _modelStatus.Margin = new Padding(0, 2, 0, 6);
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
        _correctionFields.Controls.Add(DarkTheme.Label("Replacement rules"));

        ConfigureCorrectionTable();
        _corrections.Dock = DockStyle.Top;
        _corrections.Height = 172;
        _corrections.Margin = new Padding(0, 3, 0, 10);
        _correctionFields.Controls.Add(_corrections);

        var editor = new WidthConstrainedTableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = DarkTheme.Surface,
            Margin = Padding.Empty
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heardAsLabel = DarkTheme.Label("Heard as");
        heardAsLabel.Margin = new Padding(0, 0, 4, 3);
        var replaceWithLabel = DarkTheme.Label("Replace with");
        replaceWithLabel.Margin = new Padding(4, 0, 0, 3);

        StyleInput(_correctionHeardAs);
        _correctionHeardAs.Dock = DockStyle.Fill;
        _correctionHeardAs.PlaceholderText = "For example: steward";
        _correctionHeardAs.Margin = new Padding(0, 0, 4, 0);
        _correctionHeardAs.TextChanged += (_, _) => CorrectionDraftChanged();
        _correctionHeardAs.KeyDown += CorrectionEditorKeyDown;
        StyleInput(_correctionReplaceWith);
        _correctionReplaceWith.Dock = DockStyle.Fill;
        _correctionReplaceWith.PlaceholderText = "For example: Stewart";
        _correctionReplaceWith.Margin = new Padding(4, 0, 0, 0);
        _correctionReplaceWith.TextChanged += (_, _) => CorrectionDraftChanged();
        _correctionReplaceWith.KeyDown += CorrectionEditorKeyDown;

        ConfigureActionButton(_correctionAction, "Add rule", 92);
        _correctionAction.Margin = Padding.Empty;
        _correctionAction.Click += (_, _) => AddOrUpdateCorrection();

        ConfigureActionButton(_newCorrection, "New rule", 88);
        _newCorrection.Margin = new Padding(8, 0, 0, 0);
        _newCorrection.Click += (_, _) =>
        {
            StartNewCorrection();
            _saveStatus.Text = "Enter a phrase and its replacement, then test or add the rule.";
        };

        ConfigureActionButton(_deleteCorrection, "Remove selected", 128);
        _deleteCorrection.Margin = new Padding(8, 0, 0, 0);
        _deleteCorrection.Click += (_, _) => DeleteSelectedCorrection();

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = DarkTheme.Surface,
            Margin = new Padding(0, 8, 0, 0)
        };
        actions.Controls.Add(_correctionAction);
        actions.Controls.Add(_newCorrection);
        actions.Controls.Add(_deleteCorrection);

        editor.Controls.Add(heardAsLabel, 0, 0);
        editor.Controls.Add(replaceWithLabel, 1, 0);
        editor.Controls.Add(_correctionHeardAs, 0, 1);
        editor.Controls.Add(_correctionReplaceWith, 1, 1);
        editor.Controls.Add(actions, 0, 2);
        editor.SetColumnSpan(actions, 2);
        _correctionFields.Controls.Add(editor);

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

    private void ConfigureCorrectionTable()
    {
        _corrections.AllowUserToAddRows = false;
        _corrections.AllowUserToDeleteRows = false;
        _corrections.AllowUserToResizeRows = false;
        _corrections.AutoGenerateColumns = false;
        _corrections.BackgroundColor = DarkTheme.SurfaceRaised;
        _corrections.BorderStyle = BorderStyle.FixedSingle;
        _corrections.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _corrections.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _corrections.ColumnHeadersDefaultCellStyle.BackColor = DarkTheme.Surface;
        _corrections.ColumnHeadersDefaultCellStyle.ForeColor = DarkTheme.Text;
        _corrections.ColumnHeadersDefaultCellStyle.SelectionBackColor = DarkTheme.Surface;
        _corrections.ColumnHeadersDefaultCellStyle.SelectionForeColor = DarkTheme.Text;
        _corrections.ColumnHeadersHeight = 30;
        _corrections.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _corrections.DefaultCellStyle.BackColor = DarkTheme.SurfaceRaised;
        _corrections.DefaultCellStyle.ForeColor = DarkTheme.Text;
        _corrections.DefaultCellStyle.SelectionBackColor = Color.FromArgb(54, 75, 112);
        _corrections.DefaultCellStyle.SelectionForeColor = DarkTheme.Text;
        _corrections.EnableHeadersVisualStyles = false;
        _corrections.GridColor = DarkTheme.Border;
        _corrections.MultiSelect = false;
        _corrections.ReadOnly = true;
        _corrections.RowHeadersVisible = false;
        _corrections.RowTemplate.Height = 28;
        _corrections.SelectionMode = DataGridViewSelectionMode.FullRowSelect;

        _corrections.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Heard as",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 50,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _corrections.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Replace with",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            FillWeight = 50,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        _corrections.SelectionChanged += (_, _) => LoadSelectedCorrection();
    }

    private TableLayoutPanel BuildCorrectionPreview()
    {
        StyleInput(_correctionPreviewInput);
        _correctionPreviewInput.Dock = DockStyle.Fill;
        _correctionPreviewInput.Multiline = true;
        _correctionPreviewInput.MinimumSize = new Size(0, 42);
        _correctionPreviewInput.PlaceholderText = "Try a sentence here before you save";
        _correctionPreviewInput.TextChanged += (_, _) => RefreshCorrectionPreview();

        StyleInput(_correctionPreviewOutput);
        _correctionPreviewOutput.AutoSize = false;
        _correctionPreviewOutput.Dock = DockStyle.Top;
        _correctionPreviewOutput.Height = 38;
        _correctionPreviewOutput.BorderStyle = BorderStyle.FixedSingle;
        _correctionPreviewOutput.Padding = new Padding(8, 0, 8, 0);
        _correctionPreviewOutput.TextAlign = ContentAlignment.MiddleLeft;
        _correctionPreviewOutput.AutoEllipsis = true;
        _correctionPreviewOutput.UseMnemonic = false;

        var preview = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            MinimumSize = new Size(0, 198),
            BackColor = DarkTheme.Surface,
            Margin = new Padding(8, 0, 0, 0)
        };
        preview.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        preview.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        preview.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var beforeLabel = DarkTheme.Label("Test your rules");
        beforeLabel.Margin = new Padding(0, 0, 0, 3);
        _correctionPreviewResultLabel.Margin = new Padding(0, 6, 0, 3);
        _correctionPreviewInput.Margin = Padding.Empty;
        _correctionPreviewOutput.Margin = Padding.Empty;
        preview.Controls.Add(beforeLabel, 0, 0);
        preview.Controls.Add(_correctionPreviewInput, 0, 1);
        preview.Controls.Add(_correctionPreviewResultLabel, 0, 2);
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
        var workingArea = Screen.FromControl(this).WorkingArea;
        var maximumWidth = Math.Max(MinimumSize.Width, workingArea.Width - WorkingAreaMargin);
        var maximumHeight = Math.Max(MinimumSize.Height, workingArea.Height - WorkingAreaMargin);
        var width = Math.Min(PreferredWindowWidth, maximumWidth);

        Size = new Size(width, Math.Min(Math.Max(Height, MinimumSize.Height), maximumHeight));
        ApplyResponsiveLayout(_contentHost.ClientSize.Width < TwoColumnMinimumContentWidth);
        PerformLayout();

        var content = _contentHost.Controls.Count == 0 ? null : _contentHost.Controls[0];
        var fixedWindowHeight = Height - _contentHost.ClientSize.Height;
        var requiredContentHeight = content is null
            ? _contentHost.ClientSize.Height
            : Math.Max(content.Height, content.GetPreferredSize(new Size(_contentHost.ClientSize.Width, 0)).Height);
        var requiredWindowHeight = fixedWindowHeight + requiredContentHeight + 2;
        var height = Math.Clamp(requiredWindowHeight, MinimumSize.Height, maximumHeight);

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
            _correctionPreview.Margin = new Padding(8, 0, 0, 0);
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
        StartNewCorrection();
        RefreshCorrectionPreview();
        _saveStatus.Text = "All changes saved.";
    }

    private async Task SaveAsync()
    {
        if (HasIncompleteCorrectionDraft())
        {
            _saveStatus.Text = "Finish both correction fields before saving, or click New rule to clear them.";
            return;
        }

        if (HasChangedCorrectionDraft())
        {
            AddOrUpdateCorrection();
        }

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
        _saveStatus.Text = "Saved. New dictations use these rules.";
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

    private void AddOrUpdateCorrection()
    {
        var heardAs = _correctionHeardAs.Text.Trim();
        var replaceWith = _correctionReplaceWith.Text.Trim();
        if (heardAs.Length == 0 || replaceWith.Length == 0)
        {
            _saveStatus.Text = "Enter both the text you hear and the replacement you want.";
            return;
        }

        var replacement = new TranscriptCorrection(heardAs, replaceWith);
        var wasEditing = _selectedCorrection is not null;
        if (_selectedCorrection is not null)
        {
            _transcriptCorrections.Remove(_selectedCorrection);
        }

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
        StartNewCorrection();
        _saveStatus.Text = existing >= 0 || wasEditing
            ? "Rule updated. Click Save to use it in new dictations."
            : "Rule added. Click Save to use it in new dictations.";
        RefreshCorrectionPreview();
    }

    private void DeleteSelectedCorrection()
    {
        if (_selectedCorrection is null)
        {
            _saveStatus.Text = "Select a rule in the table before removing it.";
            return;
        }

        _transcriptCorrections.Remove(_selectedCorrection);
        RefreshCorrectionsList();
        StartNewCorrection();
        _saveStatus.Text = "Rule removed. Click Save to make the removal permanent.";
        RefreshCorrectionPreview();
    }

    private void RefreshCorrectionsList()
    {
        _refreshingCorrectionEditor = true;
        try
        {
            _corrections.Rows.Clear();
            foreach (var correction in _transcriptCorrections)
            {
                var row = _corrections.Rows[_corrections.Rows.Add(correction.HeardAs, correction.ReplaceWith)];
                row.Tag = correction;
            }

            _corrections.ClearSelection();
            _corrections.CurrentCell = null;
            _selectedCorrection = null;
            _saveStatus.Text = _transcriptCorrections.Count == 0
                ? "No rules yet. Add one below, then test it on the right."
                : $"{_transcriptCorrections.Count} rule{(_transcriptCorrections.Count == 1 ? string.Empty : "s")}. Select a row to edit it.";
        }
        finally
        {
            _refreshingCorrectionEditor = false;
        }
    }

    private void LoadSelectedCorrection()
    {
        if (_refreshingCorrectionEditor
            || _corrections.SelectedRows.Count == 0
            || _corrections.SelectedRows[0].Tag is not TranscriptCorrection selected)
        {
            return;
        }

        _refreshingCorrectionEditor = true;
        try
        {
            _selectedCorrection = selected;
            _correctionHeardAs.Text = selected.HeardAs;
            _correctionReplaceWith.Text = selected.ReplaceWith;
            _correctionAction.Text = "Update rule";
            _deleteCorrection.Enabled = true;
            _saveStatus.Text = "Editing selected rule. Change either field, then click Update rule.";
        }
        finally
        {
            _refreshingCorrectionEditor = false;
        }

        RefreshCorrectionPreview();
    }

    private void StartNewCorrection()
    {
        _refreshingCorrectionEditor = true;
        try
        {
            _selectedCorrection = null;
            _corrections.ClearSelection();
            _corrections.CurrentCell = null;
            _correctionHeardAs.Clear();
            _correctionReplaceWith.Clear();
            _correctionAction.Text = "Add rule";
            _correctionAction.Enabled = false;
            _deleteCorrection.Enabled = false;
        }
        finally
        {
            _refreshingCorrectionEditor = false;
        }

        RefreshCorrectionPreview();
    }

    private void CorrectionDraftChanged()
    {
        if (_refreshingCorrectionEditor)
        {
            return;
        }

        _correctionAction.Enabled = !string.IsNullOrWhiteSpace(_correctionHeardAs.Text)
            && !string.IsNullOrWhiteSpace(_correctionReplaceWith.Text);
        _saveStatus.Text = _selectedCorrection is null
            ? "Draft rule. The test result includes it before you add it."
            : "Editing selected rule. The test result includes your unsaved edits.";
        RefreshCorrectionPreview();
    }

    private void CorrectionEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter || !_correctionAction.Enabled)
        {
            return;
        }

        AddOrUpdateCorrection();
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private bool HasIncompleteCorrectionDraft()
    {
        var hasHeardAs = !string.IsNullOrWhiteSpace(_correctionHeardAs.Text);
        var hasReplacement = !string.IsNullOrWhiteSpace(_correctionReplaceWith.Text);
        return hasHeardAs != hasReplacement;
    }

    private bool HasChangedCorrectionDraft()
    {
        var heardAs = _correctionHeardAs.Text.Trim();
        var replaceWith = _correctionReplaceWith.Text.Trim();
        if (heardAs.Length == 0 || replaceWith.Length == 0)
        {
            return false;
        }

        return _selectedCorrection is null
            || !string.Equals(_selectedCorrection.HeardAs, heardAs, StringComparison.Ordinal)
            || !string.Equals(_selectedCorrection.ReplaceWith, replaceWith, StringComparison.Ordinal);
    }

    private void RefreshCorrectionPreview()
    {
        var input = _correctionPreviewInput.Text;
        var output = new TranscriptCorrectionDictionary(CorrectionsForPreview()).Apply(input);
        var changed = input.Length > 0 && !string.Equals(input, output, StringComparison.Ordinal);
        _correctionPreviewOutput.Text = output;
        _correctionPreviewResultLabel.Visible = changed;
        _correctionPreviewOutput.Visible = changed;
    }

    private IReadOnlyList<TranscriptCorrection> CorrectionsForPreview()
    {
        var heardAs = _correctionHeardAs.Text.Trim();
        var replaceWith = _correctionReplaceWith.Text.Trim();
        if (heardAs.Length == 0 || replaceWith.Length == 0)
        {
            return _transcriptCorrections;
        }

        var previewCorrections = _transcriptCorrections.ToList();
        if (_selectedCorrection is not null)
        {
            previewCorrections.Remove(_selectedCorrection);
        }

        var existing = previewCorrections.FindIndex(
            correction => string.Equals(correction.HeardAs, heardAs, StringComparison.OrdinalIgnoreCase));
        var draft = new TranscriptCorrection(heardAs, replaceWith);
        if (existing >= 0)
        {
            previewCorrections[existing] = draft;
        }
        else
        {
            previewCorrections.Add(draft);
        }

        return previewCorrections;
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

    internal bool ContentHasVerticalScrollForTest => _contentHost.VerticalScroll.Visible;

    internal bool PrimarySectionsFitContentForTest =>
        _recordingSection is not null
        && _transcriptionSection is not null
        && DescendantsFit(_recordingSection)
        && DescendantsFit(_transcriptionSection);

    internal string ContentLayoutForTest =>
        $"host={_contentHost.ClientSize}, content={_contentHost.Controls[0].Size}, display={_contentHost.DisplayRectangle.Size}, "
        + $"primary={_primarySections?.Bounds}, corrections={_correctionLayout?.Bounds}";

    internal int PrimarySectionColumnCountForTest => _primarySections?.ColumnCount ?? 0;

    internal int CorrectionColumnCountForTest => _correctionLayout?.ColumnCount ?? 0;

    internal bool CorrectionPreviewFitsForTest =>
        _correctionPreview is not null
        && _correctionPreviewInput.Height >= 42
        && (!_correctionPreviewOutput.Visible || _correctionPreviewOutput.Height >= 38)
        && _correctionPreview.Margin.Bottom >= 12
        && DescendantsFit(_correctionPreview);

    internal Rectangle CorrectionFieldsBoundsForTest =>
        _correctionFields?.Bounds ?? Rectangle.Empty;

    internal Rectangle CorrectionPreviewBoundsForTest =>
        _correctionPreview?.Bounds ?? Rectangle.Empty;

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

    internal bool CorrectionPreviewResultVisibleForTest => _correctionPreviewOutput.Visible;

    internal string[] CorrectionColumnHeadersForTest =>
        _corrections.Columns.Cast<DataGridViewColumn>().Select(column => column.HeaderText).ToArray();

    internal int CorrectionVisibleRowCapacityForTest =>
        Math.Max(0, (_corrections.Height - _corrections.ColumnHeadersHeight - 2) / _corrections.RowTemplate.Height);

    internal int CorrectionRuleCountForTest => _corrections.Rows.Count;

    internal string CorrectionHeardAsForTest => _correctionHeardAs.Text;

    internal string CorrectionReplaceWithForTest => _correctionReplaceWith.Text;

    internal string CorrectionActionTextForTest => _correctionAction.Text;

    internal string SaveStatusTextForTest => _saveStatus.Text;

    internal Control CorrectionEditorForTest =>
        _correctionLayout ?? throw new InvalidOperationException("Correction editor has not been created.");

    internal void SelectCorrectionForTest(int index)
    {
        _corrections.ClearSelection();
        _corrections.CurrentCell = _corrections.Rows[index].Cells[0];
        _corrections.Rows[index].Selected = true;
        LoadSelectedCorrection();
    }

    internal void StartNewCorrectionForTest()
    {
        StartNewCorrection();
    }

    internal void SetCorrectionDraftForTest(string heardAs, string replaceWith)
    {
        _correctionHeardAs.Text = heardAs;
        _correctionReplaceWith.Text = replaceWith;
    }

    internal void AddCorrectionForTest()
    {
        AddOrUpdateCorrection();
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

}
