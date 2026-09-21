using System.Globalization;

namespace AfSdr;

// Decimal arithmetic keeps wheel/spin/direct entry on exactly the same decimal grid.
// Not tied to RF frequency: digits, precision, limits and unit are constructor options.
internal sealed class DigitTuningControl : UserControl
{
    private sealed class DigitSurface : Control
    {
        internal DigitSurface() { SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); TabStop = true; }
        protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);
    }
    private readonly DigitSurface surface = new();
    private readonly TextBox editor = new() { Visible = false };
    private readonly ToolTip tips = new();
    private decimal minimum, maximum;
    private readonly bool showSign;
    private readonly int integerDigits, decimals;
    private readonly string unit;
    private decimal value;
    private int selected, wheelRemainder;
    internal event EventHandler? ValueChanged;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal decimal Value
    {
        get => value;
        set
        {
            if (value < minimum || value > maximum || decimal.Round(value, decimals) != value) throw new ArgumentOutOfRangeException(nameof(value));
            if (this.value == value) return;
            this.value = value; surface.Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    internal decimal SelectedStep => (decimal)Math.Pow(10, integerDigits - 1 - selected);
    internal bool DigitFocused => surface.Focused;
    internal void RestoreDigitFocus() { if (Visible && Enabled && !editor.Visible) surface.Focus(); }
    private string Number => (showSign ? (Value < 0 ? "−" : "+") : "") + Math.Abs(Value).ToString(new string('0', integerDigits) + (decimals > 0 ? "." + new string('0', decimals) : ""), CultureInfo.InvariantCulture);
    private int CellWidth => Math.Max(12, (int)Math.Ceiling(surface.Font.SizeInPoints * DeviceDpi / 72 * .7));
    private int CharacterIndex(int digit) => (showSign ? 1 : 0) + digit + (digit >= integerDigits && decimals > 0 ? 1 : 0);
    internal DigitTuningControl(decimal minimum, decimal maximum, int integerDigits = 6, int decimals = 1, string unit = "Hz", bool showSign = true)
    {
        this.minimum = minimum; this.maximum = maximum; this.integerDigits = integerDigits; this.decimals = decimals; this.unit = unit;
        this.showSign = showSign; value = Math.Clamp(0, minimum, maximum);
        selected = integerDigits - 1;
        Size = new Size(360, 42); MinimumSize = new Size(340, 42);
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        surface.Dock = DockStyle.Fill; surface.Font = new Font("Consolas", 15); surface.AccessibleName = "桁選択式数値調整";
        var up = new Button { Text = "▲", Dock = DockStyle.Fill, AccessibleName = "選択桁を増やす" };
        var down = new Button { Text = "▼", Dock = DockStyle.Fill, AccessibleName = "選択桁を減らす" };
        var direct = new Button { Text = "直接入力", Dock = DockStyle.Fill };
        row.Controls.Add(surface, 0, 0); row.Controls.Add(up, 1, 0); row.Controls.Add(down, 2, 0); row.Controls.Add(direct, 3, 0); Controls.Add(row);
        Layout += (_, _) =>
        {
            int spinWidth = Math.Max(32, Font.Height * 2);
            int directWidth = TextRenderer.MeasureText("直接入力", Font).Width + 24;
            row.ColumnStyles[1].Width = row.ColumnStyles[2].Width = spinWidth;
            row.ColumnStyles[3].Width = directWidth;
            Width = (integerDigits + decimals + 2) * CellWidth + TextRenderer.MeasureText(unit, Font).Width + 28 + 2 * spinWidth + directWidth;
            Height = Math.Max(42, surface.Font.Height + 16);
        };
        editor.Dock = DockStyle.Fill; surface.Controls.Add(editor);
        tips.SetToolTip(surface, "左クリックで桁を選択。ホイール／▲▼で即時変更。矢印キーも使用可能。ダブルクリック／F2で直接入力。");
        surface.Paint += (_, e) =>
        {
            string number = Number;
            for (int n = 0; n < number.Length; n++)
            {
                var rect = new Rectangle(3 + n * CellWidth, 2, CellWidth, surface.Height - 4);
                bool active = n == CharacterIndex(selected);
                if (active) e.Graphics.FillRectangle(SystemBrushes.Highlight, rect);
                TextRenderer.DrawText(e.Graphics, number[n].ToString(), surface.Font, rect, active ? SystemColors.HighlightText : SystemColors.ControlText, TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            TextRenderer.DrawText(e.Graphics, unit, Font, new Point(5 + number.Length * CellWidth, 10), SystemColors.ControlText);
            if (surface.Focused) ControlPaint.DrawFocusRectangle(e.Graphics, surface.ClientRectangle);
        };
        surface.MouseDown += (_, e) => { if (e.Button != MouseButtons.Left) return; surface.Focus(); SelectAt(e.X); };
        surface.MouseWheel += (_, e) => Wheel(e.Delta);
        surface.DoubleClick += (_, _) => BeginEntry();
        surface.GotFocus += (_, _) => surface.Invalidate(); surface.LostFocus += (_, _) => surface.Invalidate();
        surface.KeyDown += (_, e) =>
        {
            if (e.KeyCode is Keys.Up or Keys.Down) Adjust(e.KeyCode == Keys.Up ? 1 : -1);
            else if (e.KeyCode is Keys.Left or Keys.Right) SelectDigit(Math.Clamp(selected + (e.KeyCode == Keys.Left ? -1 : 1), 0, integerDigits + decimals - 1));
            else if (e.KeyCode is Keys.F2 or Keys.Enter) BeginEntry();
            else return;
            e.Handled = e.SuppressKeyPress = true;
        };
        up.Click += (_, _) => { Adjust(1); surface.Focus(); }; down.Click += (_, _) => { Adjust(-1); surface.Focus(); };
        direct.Click += (_, _) => BeginEntry();
        editor.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { if (CommitEntry(editor.Text)) EndEntry(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { EndEntry(); e.SuppressKeyPress = true; }
        };
        editor.Leave += (_, _) => { if (editor.Visible && CommitEntry(editor.Text)) editor.Hide(); };
    }
    internal void SetRange(decimal minimum, decimal maximum)
    {
        this.minimum = minimum; this.maximum = maximum;
        Value = Math.Clamp(Value, minimum, maximum);
    }
    internal void SelectDigit(int digit)
    {
        if (digit < 0 || digit >= integerDigits + decimals) throw new ArgumentOutOfRangeException(nameof(digit));
        selected = digit; wheelRemainder = 0; surface.Invalidate();
    }
    internal void SelectAt(int x)
    {
        int character = (x - 3) / CellWidth;
        for (int digit = 0; digit < integerDigits + decimals; digit++) if (CharacterIndex(digit) == character) { SelectDigit(digit); break; }
    }
    internal void Wheel(int delta)
    {
        wheelRemainder += delta;
        int ticks = wheelRemainder / 120; wheelRemainder %= 120;
        if (ticks != 0) Adjust(ticks);
    }
    internal void Adjust(int ticks) => Value = Math.Clamp(Value + ticks * SelectedStep, minimum, maximum);
    private void BeginEntry() { editor.Text = Value.ToString(CultureInfo.InvariantCulture); editor.BackColor = SystemColors.Window; editor.Show(); editor.Focus(); editor.SelectAll(); }
    private void EndEntry() { editor.Hide(); surface.Focus(); }
    internal bool CommitEntry(string text)
    {
        if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out decimal parsed)
            || parsed < minimum || parsed > maximum || decimal.Round(parsed, decimals) != parsed)
        {
            editor.BackColor = Color.MistyRose;
            tips.Show($"{minimum} ～ {maximum} {unit}、小数{decimals}桁以内で入力してください。Escで取り消し。", editor, 0, editor.Height, 4000);
            return false;
        }
        Value = parsed; return true;
    }
    protected override void Dispose(bool disposing) { if (disposing) { tips.Dispose(); surface.Font.Dispose(); } base.Dispose(disposing); }
}
