using PttDictation.Core;
using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace PttDictation.App;

internal sealed class StatusOverlayForm : Form
{
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private static readonly Size CompactOverlaySize = new(560, 160);
    private static readonly Size ListeningOverlaySize = new(560, 326);
    private static readonly Size FinalPreviewOverlaySize = new(900, 520);
    private const int StandardTitleHeight = 36;
    private const int StandardTextPanelHeight = 104;
    private const int FinalPreviewTitleHeight = 48;
    private const int FinalPreviewTextPanelHeight = 492;

    private readonly Panel _accent = new();
    private readonly Panel _textPanel = new();
    private readonly Label _title = new();
    private readonly Label _message = new();
    private readonly ActivityMeterControl _activityMeter = new();
    private readonly System.Windows.Forms.Timer _hideTimer = new();
    private readonly System.Windows.Forms.Timer _liveActivityTimer = new();
    private DateTimeOffset _listeningStartedAt;
    private ListeningTriggerMode _listeningTriggerMode = ListeningTriggerMode.PushToTalk;
    private string? _liveTranscriptText;
    private bool _activityMeterRequestedVisible;
    private DictationStatusKind _statusKind;

    public event Action? TranscriptEditRequested;
    public event Action? DictationCancelRequested;

    public StatusOverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Size = CompactOverlaySize;
        MinimumSize = Size;
        MaximumSize = Size;
        Padding = new Padding(1);
        BackColor = DarkTheme.Border;
        ForeColor = DarkTheme.Text;
        Font = DarkTheme.BodyFont;

        _accent.Dock = DockStyle.Left;
        _accent.Width = 6;
        _accent.BackColor = DarkTheme.Accent;

        _title.AutoSize = false;
        _title.Dock = DockStyle.Top;
        _title.Height = StandardTitleHeight;
        _title.Font = new Font("Segoe UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point);
        _title.ForeColor = DarkTheme.Text;
        _title.BackColor = Color.Transparent;
        _title.TextAlign = ContentAlignment.MiddleLeft;

        _message.AutoSize = false;
        _message.Dock = DockStyle.Fill;
        _message.Font = DarkTheme.BodyFont;
        _message.ForeColor = DarkTheme.MutedText;
        _message.BackColor = Color.Transparent;
        _message.TextAlign = ContentAlignment.MiddleLeft;
        _message.AutoEllipsis = true;

        _textPanel.Dock = DockStyle.Top;
        _textPanel.Height = StandardTextPanelHeight;
        _textPanel.BackColor = Color.Transparent;
        _textPanel.Controls.Add(_message);
        _textPanel.Controls.Add(_title);
        _title.Click += OnStatusPanelClicked;
        _message.Click += OnStatusPanelClicked;
        _textPanel.Click += OnStatusPanelClicked;

        _activityMeter.Dock = DockStyle.Bottom;
        _activityMeter.Height = 194;
        _activityMeter.Visible = false;

        var content = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 12, 16, 14),
            BackColor = DarkTheme.Surface
        };
        content.Controls.Add(_activityMeter);
        content.Controls.Add(_textPanel);

        Controls.Add(content);
        Controls.Add(_accent);

        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };

        _liveActivityTimer.Interval = 200;
        _liveActivityTimer.Tick += (_, _) => UpdateLiveActivity();
    }

    protected override bool ShowWithoutActivation => true;

    internal static int NoActivateExtendedStyleForTest => WsExNoActivate;

    internal static Size DefaultSizeForTest => CompactOverlaySize;

    internal static Size ListeningSizeForTest => ListeningOverlaySize;

    internal static Size FinalPreviewSizeForTest => FinalPreviewOverlaySize;

    internal bool ShowWithoutActivationForTest => ShowWithoutActivation;

    internal int ExtendedWindowStyleForTest => CreateParams.ExStyle;

    internal bool AutoHideTimerEnabledForTest => _hideTimer.Enabled;

    internal bool LiveActivityTimerEnabledForTest => _liveActivityTimer.Enabled;

    internal bool ActivityMeterVisibleForTest => _activityMeterRequestedVisible;

    internal double LatestActivityLevelForTest => _activityMeter.Level;

    internal bool HasActivityHistoryForTest => _activityMeter.HasHistory;

    internal int[] ActivityMeterBarHeightsForTest => _activityMeter.BarHeightsForTest;

    internal string TitleTextForTest => _title.Text;

    internal string MessageTextForTest => _message.Text;

    internal ContentAlignment TitleAlignmentForTest => _title.TextAlign;

    internal ContentAlignment MessageAlignmentForTest => _message.TextAlign;

    internal int TitleHeightForTest => _title.Height;

    internal int MessageHeightForTest => _message.Height;

    internal int TitlePreferredHeightForTest => _title.GetPreferredSize(new Size(_title.Width, 0)).Height;

    internal int MessagePreferredHeightForTest => _message.GetPreferredSize(new Size(_message.Width, 0)).Height;

    internal int TextPanelHeightForTest => _textPanel.Height;

    internal int TextPanelBottomForTest => _textPanel.Bottom;

    internal int ActivityMeterHeightForTest => _activityMeter.Height;

    internal int ActivityMeterTopForTest => _activityMeter.Top;

    internal bool MessageAutoEllipsisForTest => _message.AutoEllipsis;

    internal void RequestTranscriptEditForTest() => OnStatusPanelClicked(this, EventArgs.Empty);

    internal void RequestDictationCancelForTest() => OnStatusPanelClicked(this, EventArgs.Empty);

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }

    public void ShowStatus(DictationStatus status)
    {
        ShowStatus(status, ListeningTriggerMode.PushToTalk);
    }

    public void ShowStatus(DictationStatus status, ListeningTriggerMode mode)
    {
        _hideTimer.Stop();
        ApplyStatus(status, mode);
        PositionBottomCenter();

        if (!Visible)
        {
            Show();
        }

        StartAutoHideIfNeeded(status);
    }

    internal void ApplyStatusForTest(DictationStatus status)
    {
        ApplyStatusForTest(status, ListeningTriggerMode.PushToTalk);
    }

    internal void ApplyStatusForTest(DictationStatus status, ListeningTriggerMode mode)
    {
        _hideTimer.Stop();
        ApplyStatus(status, mode);
        StartAutoHideIfNeeded(status);
    }

    internal void UpdateActivityLevelForTest(double level)
    {
        UpdateActivityLevel(level);
    }

    internal void AdvanceLiveActivityForTest()
    {
        UpdateLiveActivity();
    }

    public void UpdateActivityLevel(double level)
    {
        if (!_activityMeterRequestedVisible || IsDisposed)
        {
            return;
        }

        _activityMeter.Level = level;
    }

    public void ShowListeningTranscript(string transcript, ListeningTriggerMode mode)
    {
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return;
        }

        _hideTimer.Stop();
        if (!_activityMeterRequestedVisible)
        {
            StartLiveActivity(DictationStatusCatalog.Listening, mode);
        }

        _listeningTriggerMode = mode;
        _liveTranscriptText = transcript.Trim();
        UpdateLiveActivity();
        PositionBottomCenter();
        if (!Visible)
        {
            Show();
        }
    }

    internal void ApplyListeningTranscriptForTest(string transcript, ListeningTriggerMode mode)
    {
        _hideTimer.Stop();
        if (!_activityMeterRequestedVisible)
        {
            StartLiveActivity(DictationStatusCatalog.Listening, mode);
        }

        _listeningTriggerMode = mode;
        _liveTranscriptText = transcript.Trim();
        UpdateLiveActivity();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
            _liveActivityTimer.Dispose();
            _textPanel.Dispose();
            _title.Dispose();
            _message.Dispose();
            _accent.Dispose();
        }

        base.Dispose(disposing);
    }

    private static Color AccentFor(DictationStatusKind kind)
    {
        return kind switch
        {
            DictationStatusKind.Listening => DarkTheme.Accent,
            DictationStatusKind.Transcribing => Color.FromArgb(245, 171, 64),
            DictationStatusKind.TranscriptPreview => Color.FromArgb(88, 180, 120),
            DictationStatusKind.Pasted => Color.FromArgb(88, 180, 120),
            DictationStatusKind.Cancelled => DarkTheme.MutedText,
            DictationStatusKind.EmptyTranscript => DarkTheme.MutedText,
            DictationStatusKind.Error => DarkTheme.Danger,
            _ => DarkTheme.Accent
        };
    }

    private void ApplyStatus(DictationStatus status, ListeningTriggerMode mode)
    {
        _statusKind = status.Kind;
        if (status.Kind == DictationStatusKind.Listening)
        {
            StartLiveActivity(status, mode);
            return;
        }

        StopLiveActivity();
        _accent.BackColor = AccentFor(status.Kind);
        if (status.Kind == DictationStatusKind.TranscriptPreview)
        {
            ConfigureFinalTranscriptPreview(status.Message);
            return;
        }

        ConfigureStandardTextPanel();
        _title.Text = status.Title;
        _message.Text = status.Message;
    }

    private void StartAutoHideIfNeeded(DictationStatus status)
    {
        if (!status.AutoHide)
        {
            return;
        }

        _hideTimer.Interval = status.Kind == DictationStatusKind.Error ? 3500 : 1500;
        _hideTimer.Start();
    }

    private void StartLiveActivity(DictationStatus status, ListeningTriggerMode mode)
    {
        ConfigureStandardTextPanel();
        UseOverlaySize(ListeningOverlaySize);
        _accent.BackColor = AccentFor(status.Kind);
        _title.Text = status.Title;
        _listeningStartedAt = DateTimeOffset.UtcNow;
        _listeningTriggerMode = mode;
        _liveTranscriptText = null;
        _activityMeterRequestedVisible = true;
        _activityMeter.Visible = true;
        _activityMeter.Reset();
        UpdateLiveActivity();
        _liveActivityTimer.Start();
    }

    private void StopLiveActivity()
    {
        _liveActivityTimer.Stop();
        _activityMeterRequestedVisible = false;
        _activityMeter.Visible = false;
        _liveTranscriptText = null;
        UseOverlaySize(CompactOverlaySize);
    }

    private void ConfigureFinalTranscriptPreview(string transcript)
    {
        UseOverlaySize(FinalPreviewOverlaySize);
        _textPanel.Height = FinalPreviewTextPanelHeight;
        _title.Height = FinalPreviewTitleHeight;
        _title.Text = "Corrected transcript — click to edit · pasting in 3 seconds";
        _title.Cursor = Cursors.Hand;
        _message.Cursor = Cursors.Hand;
        _textPanel.Cursor = Cursors.Hand;
        _message.AutoEllipsis = false;
        _message.TextAlign = ContentAlignment.TopLeft;
        _message.Text = transcript;
    }

    private void ConfigureStandardTextPanel()
    {
        _textPanel.Height = StandardTextPanelHeight;
        _title.Height = StandardTitleHeight;
        _title.Cursor = Cursors.Default;
        _message.Cursor = Cursors.Default;
        _textPanel.Cursor = Cursors.Default;
        _message.AutoEllipsis = true;
        _message.TextAlign = ContentAlignment.MiddleLeft;
        if (_statusKind == DictationStatusKind.Transcribing)
        {
            _title.Cursor = Cursors.Hand;
            _message.Cursor = Cursors.Hand;
            _textPanel.Cursor = Cursors.Hand;
        }
    }

    private void OnStatusPanelClicked(object? sender, EventArgs e)
    {
        if (_statusKind == DictationStatusKind.TranscriptPreview)
        {
            TranscriptEditRequested?.Invoke();
        }
        else if (_statusKind == DictationStatusKind.Transcribing)
        {
            DictationCancelRequested?.Invoke();
        }
    }

    private void UpdateLiveActivity()
    {
        var elapsed = DateTimeOffset.UtcNow - _listeningStartedAt;
        if (string.IsNullOrWhiteSpace(_liveTranscriptText))
        {
            _message.Text = ListeningStatusFormatter.Format(elapsed, _listeningTriggerMode);
        }
        else
        {
            _title.Text = ListeningStatusFormatter.FormatElapsed(elapsed);
            var latestWords = LiveTranscriptPreviewFormatter.LatestWords(_liveTranscriptText);
            _message.Text = $"{ListeningStatusFormatter.FormatHint(_listeningTriggerMode)}{Environment.NewLine}{latestWords}";
        }

        _activityMeter.Decay();
    }

    private void PositionBottomCenter()
    {
        var area = Screen.GetWorkingArea(Cursor.Position);
        Location = CalculateBottomCenterLocation(area, Size);
    }

    internal static Point CalculateBottomCenterLocationForTest(Rectangle workingArea, Size overlaySize)
    {
        return CalculateBottomCenterLocation(workingArea, overlaySize);
    }

    private static Point CalculateBottomCenterLocation(Rectangle workingArea, Size overlaySize)
    {
        const int margin = 20;
        var centeredX = workingArea.Left + (workingArea.Width - overlaySize.Width) / 2;
        var minX = workingArea.Left + margin;
        var maxX = workingArea.Right - overlaySize.Width - margin;
        var x = maxX < minX ? minX : Math.Clamp(centeredX, minX, maxX);
        return new Point(
            x,
            Math.Max(workingArea.Top + margin, workingArea.Bottom - overlaySize.Height - margin));
    }

    private void UseOverlaySize(Size size)
    {
        Size = size;
        MinimumSize = size;
        MaximumSize = size;
    }

    private sealed class ActivityMeterControl : Control
    {
        private const int BarCount = 18;
        private const int Gap = 5;
        private const int MinimumBarHeight = 5;
        private double _level;

        public ActivityMeterControl()
        {
            DoubleBuffered = true;
            BackColor = DarkTheme.Surface;
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double Level
        {
            get => _level;
            set
            {
                _level = Math.Clamp(value, 0, 1);
                Invalidate();
            }
        }

        public bool HasHistory => _level > 0;

        public int[] BarHeightsForTest => CalculateBarHeights();

        public void Reset()
        {
            _level = 0;
            Invalidate();
        }

        public void Decay()
        {
            const double decay = 0.82;
            _level *= decay;

            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            var availableWidth = Math.Max(1, Width - (BarCount - 1) * Gap);
            var barWidth = Math.Max(4, availableWidth / BarCount);
            var yCenter = Height / 2;
            var barHeights = CalculateBarHeights();
            var visualLevel = VisualLevel(_level);

            using var inactive = new SolidBrush(Color.FromArgb(54, DarkTheme.MutedText));

            for (var i = 0; i < BarCount; i++)
            {
                var barHeight = barHeights[i];
                var x = i * (barWidth + Gap);
                var y = yCenter - barHeight / 2;
                using var active = new SolidBrush(Color.FromArgb((int)(70 + visualLevel * 185), DarkTheme.Accent));
                var brush = _level > 0.03 ? active : inactive;
                FillRoundedRectangle(e.Graphics, brush, new Rectangle(x, y, barWidth, barHeight), 3);
            }
        }

        private int[] CalculateBarHeights()
        {
            var maxHeight = Math.Max(8, Height);
            var visualLevel = VisualLevel(_level);
            var heights = new int[BarCount];

            for (var i = 0; i < heights.Length; i++)
            {
                var centerWeight = Math.Sin(Math.PI * (i + 0.5) / heights.Length);
                var gain = 0.14 + Math.Pow(centerWeight, 1.7) * 0.86;
                heights[i] = Math.Max(MinimumBarHeight, (int)Math.Round(MinimumBarHeight + visualLevel * gain * (maxHeight - MinimumBarHeight)));
            }

            return heights;
        }

        private static double VisualLevel(double level)
        {
            return Math.Pow(Math.Clamp(level, 0, 1), 0.18);
        }

        private static void FillRoundedRectangle(Graphics graphics, Brush brush, Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            using var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            graphics.FillPath(brush, path);
        }
    }
}
