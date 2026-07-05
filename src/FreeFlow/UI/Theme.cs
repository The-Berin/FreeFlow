namespace FreeFlow.UI;

/// <summary>Shared dark-theme palette and control styling.</summary>
public static class Theme
{
    public static readonly Color Back = Color.FromArgb(24, 24, 30);
    public static readonly Color Panel = Color.FromArgb(32, 32, 40);
    public static readonly Color Field = Color.FromArgb(44, 44, 54);
    public static readonly Color Border = Color.FromArgb(58, 58, 70);
    public static readonly Color Text = Color.FromArgb(232, 232, 238);
    public static readonly Color SubText = Color.FromArgb(150, 150, 165);
    public static readonly Color Accent = Color.FromArgb(124, 92, 255);
    public static readonly Color AccentSoft = Color.FromArgb(94, 70, 200);
    public static readonly Color Danger = Color.FromArgb(220, 80, 80);
    public static readonly Color Ok = Color.FromArgb(80, 200, 120);

    public static void Apply(Control root)
    {
        ApplyTo(root);
        foreach (Control c in root.Controls)
            Apply(c);
    }

    private static void ApplyTo(Control c)
    {
        switch (c)
        {
            case Form f:
                f.BackColor = Back;
                f.ForeColor = Text;
                break;
            case Button b:
                b.FlatStyle = FlatStyle.Flat;
                b.FlatAppearance.BorderColor = Border;
                b.BackColor = Field;
                b.ForeColor = Text;
                b.Cursor = Cursors.Hand;
                break;
            case TextBox tb:
                tb.BackColor = Field;
                tb.ForeColor = Text;
                tb.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ComboBox cb:
                cb.BackColor = Field;
                cb.ForeColor = Text;
                cb.FlatStyle = FlatStyle.Flat;
                break;
            case NumericUpDown n:
                n.BackColor = Field;
                n.ForeColor = Text;
                break;
            case CheckBox chk:
                chk.ForeColor = Text;
                break;
            case Label l:
                if (l.ForeColor == SystemColors.ControlText || l.ForeColor == Color.Empty)
                    l.ForeColor = Text;
                break;
            case ListBox lb:
                lb.BackColor = Panel;
                lb.ForeColor = Text;
                lb.BorderStyle = BorderStyle.None;
                break;
            case ListView lv:
                lv.BackColor = Panel;
                lv.ForeColor = Text;
                lv.BorderStyle = BorderStyle.FixedSingle;
                break;
            case DataGridView g:
                StyleGrid(g);
                break;
            case Panel p:
                p.BackColor = c.Parent is Form ? Back : p.BackColor;
                break;
            case ProgressBar:
                break;
        }
    }

    public static void StyleGrid(DataGridView g)
    {
        g.DataError += (_, e) => e.ThrowException = false; // bad config values must never crash the UI
        g.BackgroundColor = Panel;
        g.BorderStyle = BorderStyle.None;
        g.EnableHeadersVisualStyles = false;
        g.ColumnHeadersDefaultCellStyle.BackColor = Field;
        g.ColumnHeadersDefaultCellStyle.ForeColor = Text;
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = Field;
        g.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        g.DefaultCellStyle.BackColor = Panel;
        g.DefaultCellStyle.ForeColor = Text;
        g.DefaultCellStyle.SelectionBackColor = AccentSoft;
        g.DefaultCellStyle.SelectionForeColor = Color.White;
        g.GridColor = Border;
        g.RowHeadersVisible = false;
        g.AllowUserToResizeRows = false;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
    }

    public static Button PrimaryButton(Button b)
    {
        b.BackColor = Accent;
        b.ForeColor = Color.White;
        b.FlatAppearance.BorderSize = 0;
        return b;
    }
}
