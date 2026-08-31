namespace PttDictation.App;

internal sealed class TranscriptReviewForm : Form
{
    private readonly TextBox _editor = new();
    private readonly Button _pasteButton;
    private readonly Button _cancelButton;

    public TranscriptReviewForm(string transcript)
    {
        Text = "PTT Dictation - Review Transcript";
        MinimumSize = new Size(520, 360);
        Size = new Size(900, 600);
        ShowInTaskbar = false;
        KeyPreview = true;

        DarkTheme.Apply(this);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(20),
            BackColor = DarkTheme.Background
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Review transcript before paste",
            AutoSize = true,
            Font = DarkTheme.HeaderFont,
            ForeColor = DarkTheme.Text,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8)
        };

        var help = new Label
        {
            Text = "Correct any recognition mistakes. Press Ctrl+Enter to paste, or Escape to cancel.",
            AutoSize = true,
            ForeColor = DarkTheme.MutedText,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 12)
        };

        _editor.Dock = DockStyle.Fill;
        _editor.Multiline = true;
        _editor.AcceptsReturn = true;
        _editor.AcceptsTab = false;
        _editor.WordWrap = true;
        _editor.ScrollBars = ScrollBars.Vertical;
        _editor.BorderStyle = BorderStyle.FixedSingle;
        _editor.BackColor = DarkTheme.SurfaceRaised;
        _editor.ForeColor = DarkTheme.Text;
        _editor.Font = new Font("Segoe UI", 11F, FontStyle.Regular, GraphicsUnit.Point);
        _editor.Text = transcript;
        _editor.Margin = new Padding(0, 0, 0, 14);
        DarkTheme.ApplyNativeDarkTheme(_editor);

        _pasteButton = DarkTheme.Button("Paste corrected text");
        _pasteButton.Width = 172;
        _pasteButton.DialogResult = DialogResult.OK;

        _cancelButton = DarkTheme.Button("Cancel");
        _cancelButton.Width = 96;
        _cancelButton.DialogResult = DialogResult.Cancel;

        var buttons = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = DarkTheme.Background,
            Margin = Padding.Empty
        };
        buttons.Controls.Add(_cancelButton);
        buttons.Controls.Add(_pasteButton);

        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(help, 0, 1);
        layout.Controls.Add(_editor, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);

        CancelButton = _cancelButton;
        Shown += (_, _) =>
        {
            FitToWorkingArea();
            _editor.Focus();
            _editor.SelectionStart = _editor.TextLength;
        };
    }

    public string Transcript => _editor.Text;

    internal TextBox EditorForTest => _editor;

    internal Button PasteButtonForTest => _pasteButton;

    internal Button CancelButtonForTest => _cancelButton;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter))
        {
            DialogResult = DialogResult.OK;
            Close();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void FitToWorkingArea()
    {
        var area = Screen.GetWorkingArea(Cursor.Position);
        var width = Math.Min(900, Math.Max(MinimumSize.Width, area.Width - 80));
        var height = Math.Min(600, Math.Max(MinimumSize.Height, area.Height - 80));
        Size = new Size(width, height);
        Location = new Point(
            area.Left + (area.Width - Width) / 2,
            area.Top + (area.Height - Height) / 2);
    }
}
