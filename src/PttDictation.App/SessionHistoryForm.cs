using PttDictation.Core;

namespace PttDictation.App;

internal sealed class SessionHistoryForm : Form
{
    private readonly SessionHistory _history;
    private readonly TextBox _items = new();
    private readonly ComboBox _sessions = new();
    private readonly ComboBox _view = new();
    private bool _refreshing;

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
        var selected = _sessions.SelectedItem as SessionChoice;
        _refreshing = true;
        _sessions.Items.Clear();
        _sessions.Items.Add("All transcripts");
        for (var index = _history.Entries.Count - 1; index >= 0; index--)
        {
            _sessions.Items.Add(new SessionChoice(index, _history.Entries[index].Transcript));
        }
        _sessions.SelectedIndex = selected is not null && selected.Index < _history.Entries.Count
            ? _history.Entries.Count - selected.Index
            : 0;
        _refreshing = false;
        RefreshSelectedText();
    }

    private void RefreshSelectedText()
    {
        if (_refreshing) return;
        _view.Enabled = _sessions.SelectedItem is SessionChoice;
        if (_history.Items.Count == 0)
        {
            _items.Text = "No transcripts yet.";
            return;
        }

        if (_sessions.SelectedItem is SessionChoice selection)
        {
            var entry = _history.Entries[selection.Index];
            _items.Text = _view.SelectedIndex == 1 ? FormatComparison(entry) : entry.Transcript;
            return;
        }

        _items.Text = string.Join(
            $"{Environment.NewLine}{Environment.NewLine}",
            _history.Items.Reverse());
    }

    private static string FormatComparison(SessionHistoryEntry entry)
    {
        var comparison = entry.Comparison;
        if (comparison is null)
        {
            return $"No recognition comparison was captured for this transcript.{Environment.NewLine}{Environment.NewLine}{entry.Transcript}";
        }

        static string Content(string text) => string.IsNullOrEmpty(text) ? "(No text captured)" : text;
        static string Change(string before, string after) => string.Equals(before, after, StringComparison.Ordinal) ? "unchanged" : "changed";
        return string.Join($"{Environment.NewLine}{Environment.NewLine}",
            "Raw preview recognition" + Environment.NewLine + Content(comparison.RawPreview),
            $"Last preview — preview corrections and formatting: {Change(comparison.RawPreview, comparison.LastPreview)}" + Environment.NewLine + Content(comparison.LastPreview),
            $"Final recognition — compared with raw preview: {Change(comparison.RawPreview, comparison.RawFinal)}" + Environment.NewLine + Content(comparison.RawFinal),
            $"Saved phrase replacements: {Change(comparison.RawFinal, comparison.CorrectedFinal)}" + Environment.NewLine + Content(comparison.CorrectedFinal),
            $"Final text — formatting: {Change(comparison.CorrectedFinal, comparison.FinalText)}" + Environment.NewLine + Content(comparison.FinalText));
    }

    private sealed record SessionChoice(int Index, string Transcript)
    {
        public override string ToString()
        {
            var singleLine = Transcript.ReplaceLineEndings(" ");
            return $"{Index + 1}: {(singleLine.Length > 72 ? singleLine[..72] + "…" : singleLine)}";
        }
    }

    private void BuildLayout()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(22, 20, 22, 18),
            BackColor = DarkTheme.Background
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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

        var selectors = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 0, 0, 12),
            BackColor = DarkTheme.Background
        };
        selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        selectors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        selectors.Controls.Add(new Label { Text = "Session", AutoSize = true, Margin = new Padding(0, 0, 0, 4) }, 0, 0);
        selectors.Controls.Add(new Label { Text = "View", AutoSize = true, Margin = new Padding(0, 0, 0, 4) }, 1, 0);
        foreach (var selector in new[] { _sessions, _view })
        {
            selector.Dock = DockStyle.Fill;
            selector.DropDownStyle = ComboBoxStyle.DropDownList;
            selector.FlatStyle = FlatStyle.Flat;
            selector.BackColor = DarkTheme.SurfaceRaised;
            selector.ForeColor = DarkTheme.Text;
            selector.Margin = Padding.Empty;
            DarkTheme.ApplyNativeDarkTheme(selector);
            selector.SelectedIndexChanged += (_, _) => RefreshSelectedText();
        }
        _sessions.AccessibleName = "Session";
        _sessions.Margin = new Padding(0, 0, 10, 0);
        _sessions.DropDownWidth = 520;
        _view.AccessibleName = "History view";
        _view.Items.AddRange(["Transcript", "Recognition and corrections"]);
        _view.DropDownWidth = 250;
        _view.SelectedIndex = 0;
        selectors.Controls.Add(_sessions, 0, 1);
        selectors.Controls.Add(_view, 1, 1);

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
        layout.Controls.Add(selectors, 0, 1);
        layout.Controls.Add(_items, 0, 2);
        layout.Controls.Add(buttons, 0, 3);

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
