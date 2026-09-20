namespace AfSdr;

internal sealed class MainForm : Form
{
    private readonly ComboBox devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly Button refresh = new() { Text = "再検索", AutoSize = true };
    private readonly Button connect = new() { Text = "接続", AutoSize = true };
    private readonly Button apply = new() { Text = "周波数を適用", AutoSize = true };
    private readonly ComboBox sampleRate = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175, DropDownWidth = 270 };
    private readonly ComboBox gainMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 85 };
    private readonly ComboBox rfGain = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ComboBox bandwidth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox frequency = new()
    {
        Text = "80,000,000", Width = 170, MaxLength = 100, Margin = new Padding(3, 3, 22, 3),
        PlaceholderText = "例: 78.4M / 8400k"
    };
    private readonly ErrorProvider frequencyError = new() { BlinkStyle = ErrorBlinkStyle.NeverBlink };
    private readonly SpectrumView spectrum = new();
    private readonly Label status = new() { AutoSize = true, Text = "未接続", Margin = new Padding(12, 8, 12, 8) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 40 };
    private Receiver? receiver;
    private bool busy, closing, allowClose;
    private bool updatingSettings;
    private long lastBytes;
    private DateTime lastData = DateTime.UtcNow;

    public MainForm()
    {
        Text = "AF-SDR";
        ClientSize = new Size(1180, 880);
        MinimumSize = new Size(880, 740);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Yu Gothic UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var connectionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        connectionRow.Controls.AddRange([new Label { Text = "RTL-SDR", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, devices, refresh, connect]);
        var tuningRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10, 0, 10, 8) };
        tuningRow.Controls.AddRange([new Label { Text = "中心周波数 (Hz / k / M)", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, frequency, apply,
            new Label { Text = "FFT 4096", AutoSize = true, Padding = new Padding(12, 6, 0, 0) }]);
        var settingsRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10, 0, 10, 8) };
        Label SettingLabel(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
        settingsRow.Controls.AddRange([SettingLabel("サンプルレート"), sampleRate, SettingLabel("RFゲイン"), gainMode, rfGain,
            SettingLabel("表示帯域"), bandwidth]);
        foreach (uint rate in ReceiveSettings.Rates)
            sampleRate.Items.Add(new RateOption(rate, $"{rate / 1e6:0.###} MS/s" + (rate > 2_400_000 ? " ※欠落の可能性" : "")));
        sampleRate.SelectedIndex = Array.IndexOf(ReceiveSettings.Rates, Receiver.RequestedRate);
        gainMode.Items.AddRange(["自動", "手動"]);
        gainMode.SelectedIndex = 0;
        UpdateBandwidthOptions(Receiver.RequestedRate);
        root.Controls.Add(connectionRow, 0, 0);
        root.Controls.Add(tuningRow, 0, 1);
        root.Controls.Add(settingsRow, 0, 2);
        root.Controls.Add(spectrum, 0, 3);
        root.Controls.Add(status, 0, 4);
        Controls.Add(root);
        frequencyError.ContainerControl = this;
        frequency.TextChanged += (_, _) => frequencyError.SetError(frequency, string.Empty);
        refresh.Click += (_, _) => RefreshDevices();
        connect.Click += async (_, _) => await ChangeConnectionAsync(false);
        apply.Click += async (_, _) => await ChangeConnectionAsync(true);
        sampleRate.SelectionChangeCommitted += async (_, _) => await ApplyReceiverSettingsAsync();
        gainMode.SelectionChangeCommitted += async (_, _) => await ApplyReceiverSettingsAsync();
        rfGain.SelectionChangeCommitted += async (_, _) => await ApplyReceiverSettingsAsync();
        bandwidth.SelectedIndexChanged += (_, _) =>
        {
            if (updatingSettings) return;
            spectrum.RequestedBandwidth = (bandwidth.SelectedItem as RateOption)?.Hertz ?? 0;
            spectrum.Invalidate();
        };
        devices.SelectedIndexChanged += (_, _) =>
        {
            rfGain.Items.Clear();
            gainMode.SelectedIndex = 0;
            SetControls();
        };
        spectrum.FrequencySelected += async hz =>
        {
            if (busy || closing || receiver is null) return;
            frequency.Text = FrequencyInput.Format(hz);
            await ChangeConnectionAsync(true);
        };
        frequency.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && apply.Enabled)
            {
                e.SuppressKeyPress = true;
                await ChangeConnectionAsync(true);
            }
        };
        timer.Tick += async (_, _) => await UpdateDisplayAsync();
        Shown += (_, _) => { RefreshDevices(); timer.Start(); };
        FormClosing += OnClosing;
        FormClosed += (_, _) => { timer.Dispose(); frequencyError.Dispose(); };
        SetControls();
    }

    private void RefreshDevices()
    {
        try
        {
            devices.Items.Clear();
            devices.Items.AddRange(Receiver.ListDevices());
            if (devices.Items.Count > 0) devices.SelectedIndex = 0;
            status.Text = devices.Items.Count > 0 ? "デバイスを選択し、接続してください。" : "RTL-SDRデバイスが見つかりません。";
        }
        catch (Exception ex) { ShowError(ex); }
        SetControls();
    }

    private async Task ApplyReceiverSettingsAsync()
    {
        if (busy || closing || updatingSettings) return;
        if (receiver is null)
        {
            spectrum.SampleRate = ((RateOption)sampleRate.SelectedItem!).Hertz;
            UpdateBandwidthOptions(spectrum.SampleRate);
            SetControls();
            return;
        }
        // Changing reception settings must not apply unfinished frequency text.
        await ChangeConnectionAsync(true, receiver.Frequency);
    }

    private void UpdateBandwidthOptions(uint actualRate)
    {
        uint previous = (bandwidth.SelectedItem as RateOption)?.Hertz ?? 0;
        updatingSettings = true;
        try
        {
            bandwidth.Items.Clear();
            bandwidth.Items.Add(new RateOption(0, $"全帯域 ({actualRate / 1e6:0.###} MHz)"));
            foreach (uint width in new uint[] { 2_000_000, 1_000_000, 500_000, 250_000, 100_000, 50_000 })
                if (width < actualRate) bandwidth.Items.Add(new RateOption(width, width >= 1_000_000 ? $"{width / 1e6:0.###} MHz" : $"{width / 1000} kHz"));
            bandwidth.SelectedIndex = 0;
            for (int i = 0; i < bandwidth.Items.Count; i++)
                if (bandwidth.Items[i] is RateOption option && option.Hertz == previous) bandwidth.SelectedIndex = i;
            spectrum.RequestedBandwidth = ((RateOption)bandwidth.SelectedItem!).Hertz;
            spectrum.Invalidate();
        }
        finally { updatingSettings = false; }
    }

    private void PopulateGains(int[] supported, int? applied)
    {
        int? previous = applied ?? (rfGain.SelectedItem as GainOption)?.TenthsDb;
        rfGain.Items.Clear();
        foreach (int gain in supported) rfGain.Items.Add(new GainOption(gain));
        if (supported.Length == 0) return;
        int preferred = previous.HasValue && supported.Contains(previous.Value)
            ? previous.Value : supported.MinBy(g => Math.Abs(g - 200));
        rfGain.SelectedIndex = Array.IndexOf(supported, preferred);
    }

    private async Task ChangeConnectionAsync(bool retune, uint? frequencyOverride = null)
    {
        if (busy || closing) return;
        bool start = receiver is null || retune;
        uint requestedHz = frequencyOverride ?? 0;
        // Validate before stopping reception: invalid text must not interrupt the signal/history.
        if (start && frequencyOverride is null && !FrequencyInput.TryParse(frequency.Text, out requestedHz, out string error))
        {
            frequencyError.SetError(frequency, error);
            status.Text = error;
            frequency.Focus();
            return;
        }
        frequencyError.SetError(frequency, string.Empty);
        var settings = new ReceiveSettings(((RateOption)sampleRate.SelectedItem!).Hertz,
            gainMode.SelectedIndex == 1 ? (rfGain.SelectedItem as GainOption)?.TenthsDb : null);
        busy = true;
        SetControls();
        try
        {
            status.Text = retune ? "受信設定を変更しています…" : start ? "接続しています…" : "切断しています…";
            await StopReceiverAsync();
            if (start && !closing)
            {
                // A full stop/reopen prevents old-frequency samples appearing on the new axis.
                receiver = new Receiver();
                await receiver.StartAsync((uint)devices.SelectedIndex, requestedHz, settings);
                frequency.Text = FrequencyInput.Format(receiver.Frequency);
                spectrum.CenterFrequency = receiver.Frequency;
                spectrum.SampleRate = receiver.SampleRate;
                PopulateGains(receiver.SupportedGains, receiver.AppliedGain);
                UpdateBandwidthOptions(receiver.SampleRate);
                lastBytes = 0;
                lastData = DateTime.UtcNow;
                status.Text = "受信データを待っています…";
            }
            else status.Text = "未接続";
        }
        catch (Exception ex) { await StopReceiverAsync(); ShowError(ex); }
        finally { busy = false; SetControls(); }
    }

    private async Task StopReceiverAsync()
    {
        if (receiver is not null)
        {
            await receiver.StopAsync();
            receiver = null;
        }
        spectrum.Clear();
    }

    private async Task UpdateDisplayAsync()
    {
        if (busy || closing || receiver is null) return;
        if (receiver.Failure is { } error)
        {
            busy = true; SetControls();
            try { await StopReceiverAsync(); ShowError(error); }
            finally { busy = false; SetControls(); }
            return;
        }
        long bytes = receiver.ReceivedBytes;
        if (bytes != lastBytes) { lastBytes = bytes; lastData = DateTime.UtcNow; }
        spectrum.DisplayFrame(receiver.Spectrum);
        status.Text = (DateTime.UtcNow - lastData).TotalSeconds > 3
            ? "受信データが届いていません。切断・再接続してください。"
            : $"受信中  |  {receiver.Frequency / 1e6:F6} MHz  |  {receiver.SampleRate / 1e6:F6} MS/s  |  RFゲイン "
                + (receiver.AppliedGain is int gain ? $"{gain / 10.0:F1} dB" : "自動")
                + $"  |  表示 {spectrum.Range.Bandwidth / 1000.0:0.###} kHz  |  {bytes / 1048576.0:F1} MiB";
    }

    private void SetControls()
    {
        bool enabled = !busy && !closing;
        connect.Enabled = enabled && (receiver is not null || devices.SelectedIndex >= 0);
        connect.Text = receiver is null ? "接続" : "切断";
        refresh.Enabled = devices.Enabled = enabled && receiver is null;
        apply.Enabled = enabled && receiver is not null;
        frequency.Enabled = enabled;
        sampleRate.Enabled = bandwidth.Enabled = enabled;
        gainMode.Enabled = enabled && receiver is not null && rfGain.Items.Count > 0;
        rfGain.Enabled = gainMode.Enabled && gainMode.SelectedIndex == 1;
        spectrum.CanTune = enabled && receiver is not null;
    }

    private void ShowError(Exception ex)
    {
        status.Text = ex is DllNotFoundException or BadImageFormatException
            ? "x64版 rtlsdr.dll と依存DLLを読み込めません。実行ファイルと同じフォルダーを確認してください。"
            : ex.Message;
    }

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true;
        if (closing) return;
        closing = true; timer.Stop(); SetControls();
        status.Text = "受信を停止しています…";
        while (busy) await Task.Delay(25);
        await StopReceiverAsync();
        allowClose = true;
        Close();
    }
}
