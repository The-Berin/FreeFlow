using FreeFlow.Core;

namespace FreeFlow.UI;

public sealed class HistoryForm : Form
{
    private readonly ListView _list = new();
    private readonly TextBox _preview = new();

    public HistoryForm()
    {
        Text = "FreeFlow History";
        Size = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = TrayIcons.Idle;

        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.Columns.Add("Time", 140);
        _list.Columns.Add("App", 110);
        _list.Columns.Add("Text", 520);
        _list.Dock = DockStyle.Fill;
        _list.SelectedIndexChanged += (_, _) =>
        {
            _preview.Text = _list.SelectedItems.Count > 0
                ? ((HistoryEntry)_list.SelectedItems[0].Tag!).Text
                : "";
        };

        _preview.Multiline = true;
        _preview.ReadOnly = true;
        _preview.ScrollBars = ScrollBars.Vertical;
        _preview.Dock = DockStyle.Bottom;
        _preview.Height = 120;

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 48 };
        var copy = Theme.PrimaryButton(new Button { Text = "Copy", Size = new Size(90, 30), Location = new Point(10, 9) });
        copy.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0)
                Clipboard.SetText(((HistoryEntry)_list.SelectedItems[0].Tag!).Text);
        };
        var clear = new Button { Text = "Clear history", Size = new Size(110, 30), Location = new Point(110, 9) };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show("Delete all dictation history?", "FreeFlow",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                HistoryStore.Clear();
                Reload();
            }
        };
        bottom.Controls.Add(copy);
        bottom.Controls.Add(clear);

        Controls.Add(_list);
        Controls.Add(_preview);
        Controls.Add(bottom);
        Theme.Apply(this);

        Reload();
    }

    private void Reload()
    {
        _list.Items.Clear();
        var entries = HistoryStore.ReadAll();
        entries.Reverse();
        foreach (var e in entries)
        {
            var item = new ListViewItem(e.Ts.ToString("yyyy-MM-dd HH:mm:ss")) { Tag = e };
            item.SubItems.Add(e.App);
            item.SubItems.Add(e.Text.Replace('\n', ' '));
            _list.Items.Add(item);
        }
    }
}
