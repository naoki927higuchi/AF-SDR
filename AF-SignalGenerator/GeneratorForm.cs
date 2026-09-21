using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace AfSignalGenerator;

internal sealed class GeneratorForm : Form
{
    private readonly Dictionary<string, Control> fields = new();
    private readonly Dictionary<string, Control> rows = new();
    private readonly List<GroupBox> parameterGroups = new();
    private readonly TextBox outputFolder = new() { Dock = DockStyle.Fill };
    private readonly TextBox inputFolder = new() { Dock = DockStyle.Fill };
    private readonly Button generate = new() { Text = "Generate", AutoSize = true };
    private readonly Button cancel = new() { Text = "キャンセル", AutoSize = true, Enabled = false };
    private readonly Button reset = new() { Text = "初期値に戻す（Golden Signal）", AutoSize = true };
    private readonly Button import = new() { Text = "設定／付随JSONを開く…", AutoSize = true };
    private readonly Button random = new() { Text = "Random Seed", AutoSize = true };
    private readonly Button outputBrowse = new() { Text = "出力先…", AutoSize = true };
    private readonly ProgressBar progress = new() { Dock = DockStyle.Fill, Height = 20 };
    private readonly Label status = new() { Dock = DockStyle.Fill, AutoSize = true, Text = "Golden Signal: QPSK / 9600 baud / 250 kS/s / -20 dBFS / 10秒", Padding = new Padding(4) };
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 700 };
    private readonly string settingsPath;
    private bool updating = true, generating, allowClose, closing;
    private CancellationTokenSource? cancellation;
    private Task? generation;

    internal GeneratorForm(string? settingsPath = null)
    {
        this.settingsPath = settingsPath ?? SettingsStore.DefaultPath;
        Text = "AF-SignalGenerator 1.0.0"; Font = new Font("Yu Gothic UI", 10);
        ClientSize = new Size(1140, 900); MinimumSize = new Size(860, 620); StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        for (int n = 0; n < 3; n++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var columns = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, MinimumSize = new Size(1080, 0), Padding = new Padding(0, 0, 20, 0) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, MinimumSize = new Size(520, 0) };
        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, MinimumSize = new Size(520, 0) };
        columns.Controls.Add(left, 0, 0); columns.Controls.Add(right, 1, 0); scroll.Controls.Add(columns); root.Controls.Add(scroll, 0, 0);
        TableLayoutPanel Group(string name, FlowLayoutPanel column)
        {
            var group = new GroupBox { Text = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(520, 0), Width = 520, Padding = new Padding(10, 24, 10, 8) };
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
            group.Controls.Add(table); column.Controls.Add(group); parameterGroups.Add(group); return table;
        }
        void Field(TableLayoutPanel table, string key, string label, Control control)
        {
            control.Width = 205;
            var row = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, Margin = new Padding(0, 2, 0, 2) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 265)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
            row.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, 0, 0); row.Controls.Add(control, 1, 0);
            table.Controls.Add(row); fields.Add(key, control); rows.Add(key, row);
            if (control is NumericUpDown num) num.ValueChanged += (_, _) => Changed();
            else if (control is ComboBox box) box.SelectedIndexChanged += (_, _) => Changed();
            else if (control is CheckBox check) check.CheckedChanged += (_, _) => Changed();
        }
        void Number(TableLayoutPanel table, string key, string label, decimal min, decimal max, int decimals = 0, decimal step = 1)
            => Field(table, key, label, new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step, ThousandsSeparator = true });
        void Choice(TableLayoutPanel table, string key, string label, object[] values)
        {
            var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FormattingEnabled = true };
            box.Format += (_, e) => { if (e.ListItem is Modulation m) e.Value = m switch { Modulation.PSK8 => "8PSK", Modulation.Pi4QPSK => "π/4 Shift QPSK", Modulation.ASK => "ASK / OOK", _ => m.ToString() }; };
            box.Items.AddRange(values); Field(table, key, label, box);
        }
        void Flag(TableLayoutPanel table, string key, string label) => Field(table, key, label, new CheckBox { Text = "ON", AutoSize = true });
        var signal = Group("Signal", left);
        Choice(signal, nameof(SignalSettings.Modulation), "Modulation (PSK8 = 8PSK)", Enum.GetValues<Modulation>().Cast<object>().ToArray());
        Number(signal, "Fc", "Fc / 記録中心 [Hz]", 1, uint.MaxValue, 0, 1000);
        Number(signal, "Baud", "公称 Baud [symbols/s]", 100, 100000, 3, 100);
        Number(signal, "RrcAlpha", "RRC α（PSK・QAM）", 0, 1, 3, .05m);
        Number(signal, "SampleRate", "Sample Rate [S/s]", 250000, 3200000, 0, 1000);
        Number(signal, "LevelDbfs", "Signal Level [dBFS・複素RMS]", -100, 0, 2);
        Number(signal, "Duration", "Duration [秒]", .01m, 3600, 3);
        Choice(signal, "DataPattern", "送信データ系列", Enum.GetValues<DataPattern>().Cast<object>().ToArray());
        Number(signal, "Seed", "Random Seed", 0, uint.MaxValue);
        signal.Controls.Add(random);
        var options = Group("Modulation Options", left);
        Choice(options, "QamOrder", "QAM Order", [16, 64, 256]);
        Choice(options, "FskTones", "FSK Tones", [2, 4, 8]);
        Number(options, "FskSpacing", "FSK隣接トーン間隔 [Hz]", 1, 500000, 3, 100);
        Number(options, "Bt", "GMSK BT", .1m, 1, 3, .05m);
        Choice(options, "AmplitudeMode", "ASK / OOK", Enum.GetValues<AmplitudeMode>().Cast<object>().ToArray());
        Choice(options, "AskOrder", "ASK Levels", [2, 4, 8]);
        options.Controls.Add(new Label { Text = "MSK: h=0.5 / ASK・OOK: Gaussian NRZ (BT=0.5)\n不要な方式固有オプションは表示されません。", AutoSize = true });
        var carrier = Group("Carrier Impairments", right);
        Number(carrier, "FrequencyOffset", "Frequency Offset [Hz]", -1600000, 1600000, 3);
        Number(carrier, "FrequencyDrift", "Frequency Drift [Hz/s]", -100000, 100000, 3);
        Number(carrier, "FrequencyJitter", "Frequency Jitter [Hz RMS]", 0, 100000, 3);
        Number(carrier, "CarrierKnotMs", "Jitter相関スケール [ms]", 1, 10000, 3);
        Number(carrier, "InitialPhaseDegrees", "Initial Carrier Phase [deg]", -360, 360, 3);
        Flag(carrier, "RandomPhase", "Initial PhaseをRandom指定");
        var clock = Group("Symbol Clock Impairments", right);
        Number(clock, "BaudOffsetPpm", "Baud Offset [ppm]", -100000, 100000, 3);
        Number(clock, "BaudDriftPpmPerSecond", "Baud Drift [ppm/s]", -100000, 100000, 3);
        Number(clock, "BaudJitterPpm", "Baud Jitter [ppm RMS]", 0, 100000, 3);
        Number(clock, "BaudKnotMs", "Jitter相関スケール [ms]", 1, 10000, 3);
        Number(clock, "TimingOffsetSymbols", "Timing Offset [symbol・正で進む]", 0, 1, 4, .05m);
        Flag(clock, "RandomTiming", "Timing OffsetをRandom指定");
        var channel = Group("Channel", right);
        Flag(channel, "Awgn", "AWGN"); Number(channel, "SnrDb", "SNR [dB・全帯域電力比]", -20, 100, 2);
        var output = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3 };
        output.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        output.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); output.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        output.Controls.Add(new Label { Text = "Output / 出力先", AutoSize = true }, 0, 0); output.Controls.Add(outputFolder, 1, 0); output.Controls.Add(outputBrowse, 2, 0);
        output.Controls.Add(new Label { Text = "設定読込フォルダ", AutoSize = true }, 0, 1); output.Controls.Add(inputFolder, 1, 1); output.Controls.Add(import, 2, 1);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill }; buttons.Controls.AddRange([generate, cancel, reset]);
        output.Controls.Add(buttons, 0, 2); output.SetColumnSpan(buttons, 3);
        root.Controls.Add(output, 0, 1); root.Controls.Add(progress, 0, 2); root.Controls.Add(status, 0, 3); Controls.Add(root);
        status.MaximumSize = new Size(ClientSize.Width - 30, 0);
        Resize += (_, _) => status.MaximumSize = new Size(Math.Max(300, ClientSize.Width - 30), 0);
        string? error; var saved = SettingsStore.Load(this.settingsPath, out error);
        ApplyParameters(saved.Parameters); outputFolder.Text = saved.OutputFolder; inputFolder.Text = saved.InputFolder;
        if (error is not null) status.Text = error;
        random.Click += (_, _) => ((NumericUpDown)fields["Seed"]).Value = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
        reset.Click += (_, _) =>
        {
            if (MessageBox.Show(this, "信号パラメータをGolden Signalへ戻します。入出力フォルダは保持します。", "初期値に戻す", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
            { ApplyParameters(new()); Save(); status.Text = "Golden Signalに戻しました。入出力フォルダは保持しています。"; }
        };
        outputBrowse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { SelectedPath = outputFolder.Text }; if (dialog.ShowDialog(this) == DialogResult.OK) outputFolder.Text = dialog.SelectedPath; };
        import.Click += (_, _) => Import();
        outputFolder.TextChanged += (_, _) => Changed(); inputFolder.TextChanged += (_, _) => Changed();
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        generate.Click += async (_, _) => { generation = GenerateAsync(); await generation; };
        cancel.Click += (_, _) => cancellation?.Cancel();
        FormClosing += async (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true; if (closing) return; closing = true; cancellation?.Cancel();
            if (generation is not null) await generation;
            Save(); allowClose = true; Close();
        };
        FormClosed += (_, _) => saveTimer.Dispose();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { saveTimer.Dispose(); cancellation?.Cancel(); }
        base.Dispose(disposing);
    }
    internal SignalSettings ReadParameters()
    {
        var result = new SignalSettings();
        foreach (var (key, field) in fields)
        {
            var property = typeof(SignalSettings).GetProperty(key)!;
            object value = field switch { NumericUpDown n => Convert.ChangeType(n.Value, property.PropertyType), ComboBox b => b.SelectedItem!, CheckBox c => c.Checked, _ => throw new InvalidOperationException() };
            if (value is null) throw new ArgumentException(key + "を選択してください。");
            property.SetValue(result, value);
        }
        return result;
    }
    internal void ApplyParameters(SignalSettings settings)
    {
        updating = true; var invalid = new List<string>();
        try
        {
            foreach (var (key, field) in fields)
            {
                object value = typeof(SignalSettings).GetProperty(key)!.GetValue(settings)!;
                try
                {
                    if (field is NumericUpDown n) n.Value = Convert.ToDecimal(value);
                    else if (field is ComboBox b) { b.SelectedItem = value; if (b.SelectedIndex < 0) throw new ArgumentException(); }
                    else if (field is CheckBox c) c.Checked = (bool)value;
                }
                catch (Exception ex) when (ex is ArgumentException or OverflowException)
                {
                    invalid.Add(key);
                    value = typeof(SignalSettings).GetProperty(key)!.GetValue(new SignalSettings())!;
                    if (field is NumericUpDown n) n.Value = Convert.ToDecimal(value); else if (field is ComboBox b) b.SelectedItem = value;
                }
            }
        }
        finally { updating = false; }
        UpdateOptions();
        if (invalid.Count > 0) status.Text = "復元できない値を初期値へ戻しました: " + string.Join(", ", invalid);
    }
    private void UpdateOptions()
    {
        var s = ReadParameters();
        rows["QamOrder"].Visible = s.Modulation == Modulation.QAM;
        rows["FskTones"].Visible = rows["FskSpacing"].Visible = s.Modulation == Modulation.FSK;
        rows["Bt"].Visible = s.Modulation == Modulation.GMSK;
        rows["AmplitudeMode"].Visible = s.Modulation == Modulation.ASK;
        rows["AskOrder"].Visible = s.Modulation == Modulation.ASK && s.AmplitudeMode == AmplitudeMode.ASK;
        fields["RrcAlpha"].Enabled = s.UsesRrc;
        fields["InitialPhaseDegrees"].Enabled = !s.RandomPhase;
        fields["TimingOffsetSymbols"].Enabled = !s.RandomTiming;
        fields["SnrDb"].Enabled = s.Awgn;
    }
    private void Changed()
    {
        if (updating) return;
        UpdateOptions(); saveTimer.Stop(); saveTimer.Start();
    }
    internal AppSettings CaptureSettings() => new() { Parameters = ReadParameters(), InputFolder = inputFolder.Text, OutputFolder = outputFolder.Text };
    private void Save()
    {
        try { SettingsStore.Save(settingsPath, CaptureSettings()); }
        catch (Exception ex) { status.Text = "設定保存エラー: " + ex.Message; }
    }
    private void Import()
    {
        using var dialog = new OpenFileDialog { Filter = "設定／付随JSON (*.json)|*.json", InitialDirectory = inputFolder.Text };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 16 * 1048576) throw new InvalidDataException("JSONが大きすぎます。");
            using var json = JsonDocument.Parse(File.ReadAllText(dialog.FileName));
            var parameters = json.RootElement.GetProperty("Parameters").Deserialize<SignalSettings>(SettingsStore.Json) ?? throw new InvalidDataException("生成条件がありません。");
            ApplyParameters(parameters); inputFolder.Text = Path.GetDirectoryName(dialog.FileName)!; Save();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "設定読込エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private async Task GenerateAsync()
    {
        if (generating) return;
        var s = ReadParameters(); var errors = s.Validate();
        if (errors.Length > 0) { MessageBox.Show(this, string.Join("\n", errors), "生成条件を確認してください", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        if (string.IsNullOrWhiteSpace(outputFolder.Text)) { status.Text = "出力先フォルダを指定してください。"; return; }
        string folder = outputFolder.Text; Save(); generating = true;
        foreach (var group in parameterGroups) group.Enabled = false;
        generate.Enabled = reset.Enabled = import.Enabled = outputBrowse.Enabled = outputFolder.Enabled = inputFolder.Enabled = false; cancel.Enabled = true;
        cancellation = new(); progress.Value = 0; status.Text = "生成中: 前半で信号電力・条件を測定、後半でWAVとJSONを書き出します。";
        try
        {
            var report = new Progress<int>(p => { if (!IsDisposed) progress.Value = Math.Clamp(p, 0, 100); });
            var result = await Task.Run(() => SignalWriter.Generate(s, folder, report, cancellation.Token));
            progress.Value = 100;
            status.Text = "完了: " + result.WavePath + $"\n{result.Frames:N0} I/Qペア / {result.SignalDbfs:F2} dBFS / Peak {result.Peak:F3}"
                + (result.MeasuredSnrDb is double snr ? $" / 実測SNR {snr:F2} dB" : " / AWGN OFF")
                + (result.Peak > 1 ? "\n±1を超えるfloat値あり（クリップせず保存）。必要ならレベルを下げてください。" : "");
        }
        catch (OperationCanceledException) { status.Text = "生成をキャンセルしました。未完成の出力は削除しました。"; }
        catch (Exception ex) { status.Text = "生成エラー: " + ex.Message; }
        finally
        {
            cancellation.Dispose(); cancellation = null; generating = false;
            foreach (var group in parameterGroups) group.Enabled = true;
            generate.Enabled = reset.Enabled = import.Enabled = outputBrowse.Enabled = outputFolder.Enabled = inputFolder.Enabled = true; cancel.Enabled = false; UpdateOptions();
        }
    }
}
