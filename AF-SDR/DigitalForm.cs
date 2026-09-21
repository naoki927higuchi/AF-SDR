using AfSdr.Dsp;
namespace AfSdr;
internal sealed class DigitalForm : Form
{
    private readonly CheckBox enabled = new() { Text = "表示 ON", AutoSize = true };
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private readonly NumericUpDown baud = new() { Minimum = 1000, Maximum = 100000, Increment = 100, Width = 100 };
    private readonly ComboBox rolloff = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 75 };
    private readonly Button apply = new() { Text = "適用", AutoSize = true };
    private readonly ConstellationView view = new();
    internal event Func<Task>? SettingsChanged;
    internal DigitalSettings Settings { get; private set; }
    internal DigitalForm(DigitalSettings settings)
    {
        Settings = settings with { Enabled = false };
        Text = "AF-SDR コンスタレーション";
        ClientSize = new Size(680, 700); MinimumSize = new Size(550, 480);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Yu Gothic UI", 10);
        mode.Items.AddRange(["I/Q（同期なし）", "BPSK", "QPSK"]);
        mode.SelectedIndex = (int)Settings.Mode;
        foreach (double value in new[] { 0.2, 0.35, 0.5, 1.0 }) rolloff.Items.Add(value);
        rolloff.SelectedItem = Settings.Rolloff;
        baud.Value = Settings.SymbolRate;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        row.Controls.AddRange([enabled, mode, new Label { Text = "baud", AutoSize = true }, baud, new Label { Text = "RRC α", AutoSize = true }, rolloff, apply]);
        root.Controls.Add(row, 0, 0);
        root.Controls.Add(new Label { Text = "主画面で信号の中心へ選局。実際のシンボルレートを指定してください。\n単一搬送波 BPSK/QPSK用。復号・方式自動判定は行いません。", AutoSize = true, Padding = new Padding(8) }, 0, 1);
        root.Controls.Add(view, 0, 2); Controls.Add(root);
        apply.Click += async (_, _) => await ApplyAsync();
        enabled.CheckedChanged += async (_, _) => await ApplyAsync();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            if (!Enabled) return;
            Hide();
            if (enabled.Checked) enabled.Checked = false;
        };
    }
    private async Task ApplyAsync()
    {
        Settings = new DigitalSettings(enabled.Checked, (DigitalMode)mode.SelectedIndex, (int)baud.Value, (double)rolloff.SelectedItem!);
        if (SettingsChanged is not null) await SettingsChanged();
    }
    internal void SetSampleRate(uint rate)
    {
        baud.Maximum = Math.Min(100000, rate / 8);
        if (Settings.SymbolRate > baud.Maximum) Settings = Settings with { SymbolRate = (int)baud.Maximum };
    }
    internal void Display(ConstellationFrame? frame, uint frequency, Exception? error, bool connected)
    {
        string caption = !connected ? "未接続" : !Settings.Enabled ? "コンスタレーション OFF"
            : error is not null ? "処理エラー: " + error.Message
            : $"中心 {FrequencyInput.Format(frequency)} Hz / {Settings.Mode} / {Settings.SymbolRate} baud / 帯域目安 {(1 + Settings.Rolloff) * Settings.SymbolRate / 1000:0.##} kHz\n"
                + (frame is null ? "データ待ち" : $"位相追従補正 {frame.FrequencyErrorHz:0.0} Hz / 欠落リセット {frame.Discontinuities} 回（同期保証なし）");
        view.Display(Settings.Enabled && connected ? frame?.Points : null, caption);
    }
}
