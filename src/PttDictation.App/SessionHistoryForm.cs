using PttDictation.Core;

namespace PttDictation.App;

internal sealed class SessionHistoryForm : Form
{
    private readonly SessionHistory _history;
    private readonly TextBox _items = new();

    public event EventHandler? QuitRequested;

    public SessionHistoryForm(SessionHistory history)
    {
        _history = history;

        Text = "PTT Dictation - Session History";
        MinimumSize = new Size(520, 420);
        Size = new Size(640, 520);

        DarkTheme.Apply(this);
        BuildLayout();
        RefreshItems();
    }

    public void RefreshItems()
    {
        if (_history.Items.Count == 0)
        {
            _items.Text = "No transcripts yet.";
            return;
        }

        _items.Text = string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            _history.Items.Reverse());
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(18),
            BackColor = DarkTheme.Background
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var title = new Label
        {
            Text = "Session History",
            AutoSize = true,
            Font = DarkTheme.HeaderFont,
            ForeColor = DarkTheme.Text,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 12)
        };

        _items.Dock = DockStyle.Fill;
        _items.BorderStyle = BorderStyle.FixedSingle;
        _items.Multiline = true;
        _items.ReadOnly = true;
        _items.WordWrap = true;
        _items.ScrollBars = ScrollBars.Vertical;
        _items.BackColor = DarkTheme.SurfaceRaised;
        _items.ForeColor = DarkTheme.Text;

        var closeButton = DarkTheme.Button("Close");
        closeButton.Width = 96;
        closeButton.Anchor = AnchorStyles.Right;
        closeButton.Click += (_, _) => Hide();

        var quitButton = DarkTheme.Button("Quit App");
        quitButton.Width = 96;
        quitButton.BackColor = DarkTheme.Danger;
        quitButton.Anchor = AnchorStyles.Right;
        quitButton.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        var buttons = new FlowLayoutPanel
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = DarkTheme.Background
        };
        buttons.Controls.Add(quitButton);
        buttons.Controls.Add(closeButton);

        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(_items, 0, 1);
        layout.Controls.Add(buttons, 0, 2);

        Controls.Add(layout);
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
