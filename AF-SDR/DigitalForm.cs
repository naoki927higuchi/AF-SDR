using AfSdr.Dsp;
namespace AfSdr;
internal sealed class DigitalForm : Form
{
    private readonly CheckBox enabled = new() { Text = "表示 ON", AutoSize = true };
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
    private readonly DigitTuningControl baud = new(1000, 100000, integerDigits: 8, decimals: 0, unit: "baud", showSign: false);
    private readonly CheckBox trajectory = new() { Text = "軌跡（補助表示）", AutoSize = true };
    private readonly ComboBox symbolCount = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly Label adjustmentError = new() { AutoSize = true, ForeColor = Color.Firebrick };
    private readonly DigitalViewSettings savedView;
    private bool windowLoaded, lastMaximized;
    private readonly ComboBox rolloff = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 75 };
    private readonly ComboBox order = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 75 };
    private readonly NumericUpDown spacing = new() { Minimum = 100, Maximum = 500000, Increment = 100, DecimalPlaces = 1, Width = 110 };
    private readonly DigitTuningControl fine = new(-10000m, 10000m);
    private readonly Button apply = new() { Text = "適用", AutoSize = true };
    private readonly Label description = new() { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(8) };
    private readonly ConstellationView view = new();
    private uint sampleRate = Receiver.RequestedRate;
    private bool updating;
    internal event Func<Task>? SettingsChanged;
    internal DigitalSettings Settings { get; private set; }
    internal DigitalForm(DigitalSettings settings, DigitalViewSettings? display = null)
    {
        savedView = (display ?? new()).Validated();
        Settings = settings with { Enabled = false };
        Text = "AF-SDR デジタル信号表示";
        ClientSize = new Size(820, 900); MinimumSize = new Size(660, 680);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Yu Gothic UI", 10);
        mode.Items.AddRange(["I/Q（同期なし）", "BPSK", "QPSK", "QAM", "π/4 Shift QPSK", "ASK / OOK", "FSK", "MSK"]);
        mode.SelectedIndex = (int)Settings.Mode;
        foreach (double value in new[] { 0.2, 0.35, 0.5, 1.0 }) rolloff.Items.Add(value);
        rolloff.SelectedItem = Settings.Rolloff; baud.Value = Settings.SymbolRate;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        description.MaximumSize = new Size(ClientSize.Width - 20, 0);
        Resize += (_, _) =>
        {
            description.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 20), 0);
            adjustmentError.MaximumSize = description.MaximumSize;
        };
        for (int n = 0; n < 6; n++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var row = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8) };
        row.Controls.AddRange([enabled, mode, new Label { Text = "RRC α", AutoSize = true }, rolloff, apply]);
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 0, 8, 0) };
        options.Controls.AddRange([new Label { Text = "多値数", AutoSize = true }, order,
            new Label { Text = "FSK隣接トーン間隔 Hz", AutoSize = true }, spacing]);
        var tuning = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 0, 8, 0) };
        tuning.Controls.AddRange([new Label { Text = "手動周波数補正", AutoSize = true, Margin = new Padding(3, 10, 3, 3) }, fine,
            new Label { Text = "＋偏差を＋値で除去（±10 kHz）。0IF基準／即時反映／デジタル解析のみ。", AutoSize = true }]);
        fine.Value = (decimal)Settings.FineFrequencyOffset;
        fine.ValueChanged += async (_, _) =>
        {
            if (updating) return;
            bool restoreFocus = fine.DigitFocused;
            Settings = Settings with { FineFrequencyOffset = (double)fine.Value };
            if (SettingsChanged is not null) await SettingsChanged();
            if (restoreFocus) fine.RestoreDigitFocus();
        };
        var baudRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 0, 8, 0) };
        baudRow.Controls.AddRange([new Label { Text = "Symbol Rate", AutoSize = true, Margin = new Padding(3, 10, 3, 3) }, baud, adjustmentError]);
        var appearance = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(8, 0, 8, 0) };
        foreach (int count in DigitalViewSettings.SymbolCounts) symbolCount.Items.Add(count);
        symbolCount.SelectedItem = savedView.SymbolCount; trajectory.Checked = savedView.Trajectory;
        appearance.Controls.AddRange([new Label { Text = "表示シンボル数（最新）", AutoSize = true }, symbolCount, trajectory]);
        symbolCount.SelectedIndexChanged += (_, _) => UpdatePresentation();
        trajectory.CheckedChanged += (_, _) => UpdatePresentation();
        UpdatePresentation();
        root.Controls.Add(baudRow, 0, 2); root.Controls.Add(tuning, 0, 3); root.Controls.Add(appearance, 0, 4);
        root.Controls.Add(row, 0, 0); root.Controls.Add(options, 0, 1); root.Controls.Add(description, 0, 5);
        root.Controls.Add(view, 0, 6); Controls.Add(root);
        ConfigureMode();
        mode.SelectedIndexChanged += (_, _) => ConfigureMode();
        baud.ValueChanged += async (_, _) => await AdjustBaudAsync();
        order.SelectedIndexChanged += (_, _) => UpdateConstraints();
        apply.Click += async (_, _) => await ApplyAsync();
        enabled.CheckedChanged += async (_, _) =>
        {
            if (updating) return;
            Settings = Settings with { Enabled = enabled.Checked };
            if (SettingsChanged is not null) await SettingsChanged();
        };
        FormClosing += (_, e) =>
        {
            if (e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true; if (!Enabled) return;
            Hide(); if (enabled.Checked) enabled.Checked = false;
        };
    }
    private void ConfigureMode()
    {
        updating = true;
        var selected = (DigitalMode)mode.SelectedIndex;
        order.Items.Clear();
        if (selected == DigitalMode.Qam) order.Items.AddRange([16, 64]);
        else if (selected is DigitalMode.Ask or DigitalMode.Fsk) order.Items.AddRange([2, 4]);
        else order.Items.Add(selected is DigitalMode.Bpsk or DigitalMode.Msk ? 2 : 4);
        order.SelectedItem = selected == DigitalMode.Qam ? Settings.QamOrder : selected == DigitalMode.Ask ? Settings.AskOrder : selected == DigitalMode.Fsk ? Settings.FskOrder : order.Items[0];
        order.Enabled = selected is DigitalMode.Qam or DigitalMode.Ask or DigitalMode.Fsk;
        rolloff.Enabled = selected is DigitalMode.Iq or DigitalMode.Bpsk or DigitalMode.Qpsk or DigitalMode.Qam or DigitalMode.Pi4Qpsk;
        spacing.Enabled = selected == DigitalMode.Fsk;
        description.Text = "主画面で信号中心へ選局。解析条件は「適用」、Symbol Rate・手動周波数補正は即時反映。復号・方式自動判定なし。\n" + (selected switch
        {
            DigitalMode.Qam => "16/64QAM: RRC＋タイミング同期＋搬送波取得・追従。取得に2048シンボル必要。",
            DigitalMode.Pi4Qpsk => "π/4 Shift QPSK: 隣接シンボルの差動位相（±45°・±135°）を4点で表示。",
            DigitalMode.Ask => "ASK: 2値（OOK）/4値の包絡線振幅と時間波形。搬送波位相には同期しません。",
            DigitalMode.Fsk => "FSK: 2/4値の周波数偏移分布と時間波形。間隔は隣接トーン間のHz値。",
            DigitalMode.Msk => "MSK: h=0.5、周波数偏移±baud/4を表示。GMSK専用の整合処理はありません。",
            DigitalMode.Iq => "RRC＋表示用8 SPS。初期に代表位相を選択。方式固有の判定・搬送波同期・baud追従なし。",
            _ => "RRC＋タイミング同期＋搬送波同期後の実際のI/Q値を表示。"
        }) + "\n緑＝受信値、灰色＋＝基準位置。基準位置への丸め・データ復号は行いません。";
        updating = false; UpdateConstraints();
        if (selected == DigitalMode.Fsk) spacing.Value = Math.Clamp((decimal)Settings.FskSpacing, spacing.Minimum, spacing.Maximum);
    }
    private void UpdateConstraints()
    {
        if (updating) return;
        int tones = order.SelectedItem is int value && (DigitalMode)mode.SelectedIndex == DigitalMode.Fsk ? value : Settings.FskOrder;
        var draft = Settings with { SymbolRate = (int)baud.Value, FskOrder = tones };
        spacing.Maximum = (decimal)Math.Min(500000, draft.MaximumSpacing(sampleRate));
        if ((DigitalMode)mode.SelectedIndex == DigitalMode.Msk) spacing.Value = Math.Clamp(baud.Value / 2, spacing.Minimum, spacing.Maximum);
    }
    private async Task ApplyAsync()
    {
        if (updating) return;
        var selected = (DigitalMode)mode.SelectedIndex;
        Settings = new DigitalSettings(enabled.Checked, selected, (int)baud.Value, (double)rolloff.SelectedItem!,
            selected == DigitalMode.Qam ? (int)order.SelectedItem! : Settings.QamOrder,
            selected == DigitalMode.Ask ? (int)order.SelectedItem! : Settings.AskOrder,
            selected == DigitalMode.Fsk ? (int)order.SelectedItem! : Settings.FskOrder,
            selected == DigitalMode.Fsk ? (double)spacing.Value : Settings.FskSpacing, Settings.FineFrequencyOffset);
        Settings = Settings.Normalize(sampleRate) with { Enabled = enabled.Checked };
        if (SettingsChanged is not null) await SettingsChanged();
    }
    internal void SetSampleRate(uint rate)
    {
        sampleRate = rate;
        Settings = Settings.Normalize(rate) with { Enabled = Settings.Enabled };
        updating = true;
        try { baud.SetRange(1000, Math.Min(100000, rate / 8)); baud.Value = Settings.SymbolRate; }
        finally { updating = false; }
        UpdateConstraints();
    }
    internal void RestoreOptions(DigitalSettings settings)
    {
        Settings = settings;
        mode.SelectedIndex = (int)settings.Mode;
        updating = true;
        baud.Value = settings.SymbolRate;
        rolloff.SelectedItem = settings.Rolloff;
        ConfigureMode();
        updating = true;
        try { enabled.Checked = settings.Enabled; fine.Value = (decimal)settings.FineFrequencyOffset; }
        finally { updating = false; }
    }
    private async Task AdjustBaudAsync()
    {
        if (updating) return;
        bool restoreFocus = baud.DigitFocused;
        var next = Settings with { SymbolRate = (int)baud.Value };
        try { next.Validate(sampleRate); }
        catch (ArgumentException ex)
        {
            updating = true; try { baud.Value = Settings.SymbolRate; } finally { updating = false; }
            adjustmentError.Text = ex.Message; return;
        }
        adjustmentError.Text = "";
        Settings = next;
        UpdateConstraints();
        if (SettingsChanged is not null) await SettingsChanged();
        if (restoreFocus) baud.RestoreDigitFocus();
    }
    private void UpdatePresentation() => view.SetPresentation((int)symbolCount.SelectedItem!, trajectory.Checked);
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var areas = Screen.AllScreens.OrderByDescending(s => s.Primary).Select(s => s.WorkingArea).ToArray();
        RestoreWindow(areas);
    }
    internal void RestoreWindow(Rectangle[] areas)
    {
        var initial = savedView.WindowBounds;
        if (initial.Width <= 0 || initial.Height <= 0)
        {
            var area = areas[0];
            initial = new Rectangle(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2, Width, Height);
        }
        var bounds = SettingsStore.FitWindow(initial, areas, MinimumSize);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, bounds.Width), Math.Min(MinimumSize.Height, bounds.Height));
        StartPosition = FormStartPosition.Manual; Bounds = bounds;
        if (savedView.Maximized) WindowState = FormWindowState.Maximized;
        lastMaximized = savedView.Maximized; windowLoaded = true;
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (windowLoaded && WindowState != FormWindowState.Minimized) lastMaximized = WindowState == FormWindowState.Maximized;
    }
    internal DigitalViewSettings CaptureViewSettings() => new()
    {
        WindowBounds = !windowLoaded ? savedView.WindowBounds : WindowState == FormWindowState.Normal ? Bounds : RestoreBounds,
        Maximized = !windowLoaded ? savedView.Maximized : WindowState == FormWindowState.Maximized || (WindowState == FormWindowState.Minimized && lastMaximized),
        SymbolCount = (int)symbolCount.SelectedItem!, Trajectory = trajectory.Checked
    };
    internal void Display(ConstellationFrame? frame, uint frequency, Exception? error, bool connected)
    {
        string measurement = Settings.FrequencyMode ? $"トーン間隔 {Settings.Spacing:0.#} Hz / 周波数偏移表示"
            : Settings.Mode == DigitalMode.Ask ? "包絡線振幅（非コヒーレント）"
            : Settings.Mode == DigitalMode.Iq ? "同期なし（残差推定なし）"
            : frame?.CarrierAcquired == false ? "搬送波取得中…" : $"推定残差（手動補正後） {frame?.FrequencyErrorHz ?? 0:0.0} Hz（ロック判定なし）";
        string caption = !connected ? "未接続" : !Settings.Enabled ? "デジタル信号表示 OFF"
            : error is not null ? "処理エラー: " + error.Message
            : $"中心 {FrequencyInput.Format(frequency)} Hz / {Settings.Mode} / {Settings.SymbolRate} baud\n"
                + (frame is null ? "データ待ち" : measurement + $" / 欠落 {frame.Discontinuities} 回");
        view.Display(Settings.Enabled && connected ? frame?.Points : null, caption, Settings,
            Settings.Enabled && connected ? frame?.Trace : null,
            Settings.Enabled && connected ? frame?.IqTrajectory : null, frame?.IqSamplesPerSymbol ?? 0);
    }
}
