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
            Padding = new Padding(22, 20, 22, 18),
            BackColor = DarkTheme.Background
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));

        var title = new Label
        {
            Text = "Session History",
            AutoSize = true,
            Font = new Font("Segoe UI Variable Display", 15F, FontStyle.Bold, GraphicsUnit.Point),
            ForeColor = DarkTheme.Text,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 16)
        };

        _items.Dock = DockStyle.Fill;
        _items.BorderStyle = BorderStyle.FixedSingle;
        _items.Multiline = true;
        _items.ReadOnly = true;
        _items.TabStop = false;
        _items.WordWrap = true;
        _items.ScrollBars = ScrollBars.Vertical;
        _items.BackColor = DarkTheme.SurfaceRaised;
        _items.ForeColor = DarkTheme.Text;
        _items.Font = new Font("Segoe UI Variable Text", 10F, FontStyle.Regular, GraphicsUnit.Point);
        _items.Margin = Padding.Empty;
        DarkTheme.ApplyNativeDarkTheme(_items);
        DarkTheme.ApplyTextEditingMenu(_items);

        var closeButton = DarkTheme.Button("Close");
        closeButton.Size = new Size(104, 36);
        closeButton.BackColor = DarkTheme.Accent;
        closeButton.FlatAppearance.BorderColor = DarkTheme.Accent;
        closeButton.Font = new Font("Segoe UI Variable Text", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        closeButton.Margin = Padding.Empty;
        closeButton.Click += (_, _) => Hide();

        var quitButton = DarkTheme.Button("Quit App");
        quitButton.Size = new Size(104, 36);
        quitButton.BackColor = DarkTheme.SurfaceRaised;
        quitButton.ForeColor = DarkTheme.Danger;
        quitButton.FlatAppearance.BorderColor = DarkTheme.Danger;
        quitButton.Font = new Font("Segoe UI Variable Text", 9.5F, FontStyle.Regular, GraphicsUnit.Point);
        quitButton.Margin = new Padding(0, 0, 10, 0);
        quitButton.Click += (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            BackColor = DarkTheme.Background,
            Margin = Padding.Empty,
            Padding = new Padding(0, 16, 0, 0)
        };
        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(quitButton);

        layout.Controls.Add(title, 0, 0);
        layout.Controls.Add(_items, 0, 1);
        layout.Controls.Add(buttons, 0, 2);

        Controls.Add(layout);
        CancelButton = closeButton;
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
