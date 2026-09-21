namespace AfSdr;

internal sealed class MainForm : Form
{
    private readonly ComboBox devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly Button refresh = new() { Text = "再検索", AutoSize = true };
    private readonly Button connect = new() { Text = "接続", AutoSize = true };
    private readonly Button apply = new() { Text = "周波数を適用", AutoSize = true };
    private readonly ComboBox fftSize = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ComboBox fftWindow = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 165 };
    private readonly ComboBox rxBandwidth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly CheckBox showRxBandwidth = new() { Text = "RxBW表示", AutoSize = true, Checked = true, Padding = new Padding(8, 5, 0, 0) };
    private readonly NumericUpDown levelLower = new() { Minimum = -160, Maximum = -5, Value = -120, Increment = 5, Width = 80 };
    private readonly NumericUpDown levelUpper = new() { Minimum = -115, Maximum = 20, Value = 0, Increment = 5, Width = 80 };
    private readonly CheckBox fmEnabled = new() { Text = "FM音声（モノラル）", AutoSize = true, Padding = new Padding(12, 5, 0, 0) };
    private readonly TrackBar volume = new() { Minimum = 0, Maximum = 100, Value = 30, TickFrequency = 10, Width = 145, Height = 35 };
    private readonly Label volumeLabel = new() { Text = "音量 30%", AutoSize = true, Padding = new Padding(0, 6, 0, 0) };
    private readonly Label audioStatus = new() { AutoSize = true, Text = "FM OFF", Padding = new Padding(8, 6, 0, 0) };
    private readonly ComboBox sampleRate = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 175, DropDownWidth = 270 };
    private readonly ComboBox gainMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 85 };
    private readonly ComboBox rfGain = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
    private readonly ComboBox bandwidth = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
    private readonly TextBox frequency = new()
    {
        Text = "80M", Width = 150, MaxLength = 100, Margin = new Padding(3, 3, 22, 3),
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
        ClientSize = new Size(1280, 960);
        MinimumSize = new Size(1000, 800);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Yu Gothic UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 6, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var connectionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        connectionRow.Controls.AddRange([new Label { Text = "RTL-SDR", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, devices, refresh, connect]);
        connectionRow.Controls.AddRange([fmEnabled, volumeLabel, volume, audioStatus]);
        var tuningRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10, 0, 10, 8) };
        tuningRow.Controls.AddRange([new Label { Text = "中心周波数 (Hz / k / M)", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, frequency, apply,
            new Label { Text = "FFTポイント数", AutoSize = true, Padding = new Padding(12, 6, 0, 0) }, fftSize,
            new Label { Text = "FFT窓", AutoSize = true, Padding = new Padding(12, 6, 0, 0) }, fftWindow]);
        foreach (int size in Dsp.SpectrumProcessor.SupportedSizes) fftSize.Items.Add(size);
        fftSize.SelectedItem = Dsp.SpectrumProcessor.DefaultSize;
        foreach (var window in Enum.GetValues<Dsp.FftWindow>()) fftWindow.Items.Add(window);
        fftWindow.SelectedItem = Dsp.FftWindow.Hann;
        foreach (uint width in ReceiveSettings.RxBandwidths) rxBandwidth.Items.Add(new RateOption(width, $"{width / 1000} kHz"));
        rxBandwidth.SelectedIndex = Array.IndexOf(ReceiveSettings.RxBandwidths, 200_000u);
        var settingsRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10, 0, 10, 8) };
        Label SettingLabel(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
        settingsRow.Controls.AddRange([SettingLabel("サンプルレート"), sampleRate, SettingLabel("RFゲイン"), gainMode, rfGain,
            SettingLabel("表示帯域"), bandwidth, SettingLabel("FM RxBW"), rxBandwidth, showRxBandwidth]);
        var levelRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10, 0, 10, 8) };
        levelRow.Controls.AddRange([SettingLabel("表示レベル（スペクトラム＋色） 下限"), levelLower,
            SettingLabel("上限"), levelUpper, SettingLabel("dBFS / FFT bin    RxBW＝復調帯域・表示帯域とは別")]);
        foreach (uint rate in ReceiveSettings.Rates)
            sampleRate.Items.Add(new RateOption(rate, $"{rate / 1e6:0.###} MS/s" + (rate > 2_400_000 ? " ※欠落の可能性" : "")));
        sampleRate.SelectedIndex = Array.IndexOf(ReceiveSettings.Rates, Receiver.RequestedRate);
        gainMode.Items.AddRange(["自動", "手動"]);
        gainMode.SelectedIndex = 0;
        UpdateBandwidthOptions(Receiver.RequestedRate);
        root.Controls.Add(connectionRow, 0, 0);
        root.Controls.Add(tuningRow, 0, 1);
        root.Controls.Add(settingsRow, 0, 2);
        root.Controls.Add(levelRow, 0, 3);
        root.Controls.Add(spectrum, 0, 4);
        root.Controls.Add(status, 0, 5);
        Controls.Add(root);
        frequencyError.ContainerControl = this;
        frequency.TextChanged += (_, _) => frequencyError.SetError(frequency, string.Empty);
        refresh.Click += (_, _) => RefreshDevices();
        connect.Click += async (_, _) => await ChangeConnectionAsync(false);
        apply.Click += async (_, _) => await ChangeConnectionAsync(true);
        fmEnabled.CheckedChanged += async (_, _) => await ApplyReceiverSettingsAsync();
        volume.ValueChanged += (_, _) =>
        {
            volumeLabel.Text = $"音量 {volume.Value}%";
            if (receiver is not null) receiver.Volume = volume.Value / 100f;
        };
        sampleRate.SelectionChangeCommitted += async (_, _) => await ApplyReceiverSettingsAsync();
        fftSize.SelectionChangeCommitted += async (_, _) => await ApplyReceiverSettingsAsync();
        fftWindow.SelectionChangeCommitted += async (_, _) => await ApplyReceiverSettingsAsync();
        rxBandwidth.SelectionChangeCommitted += async (_, _) => await ApplyReceiverSettingsAsync();
        showRxBandwidth.CheckedChanged += (_, _) => { spectrum.ShowRxBandwidth = showRxBandwidth.Checked; spectrum.Invalidate(); };
        levelLower.ValueChanged += (_, _) => UpdateLevels();
        levelUpper.ValueChanged += (_, _) => UpdateLevels();
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
            spectrum.RxBandwidth = ((RateOption)rxBandwidth.SelectedItem!).Hertz;
            spectrum.FmEnabled = fmEnabled.Checked;
            spectrum.SampleRate = ((RateOption)sampleRate.SelectedItem!).Hertz;
            UpdateBandwidthOptions(spectrum.SampleRate);
            SetControls();
            return;
        }
        // Changing reception settings must not apply unfinished frequency text.
        await ChangeConnectionAsync(true, receiver.Frequency);
    }

    private void UpdateLevels()
    {
        levelLower.Maximum = levelUpper.Value - 5;
        levelUpper.Minimum = levelLower.Value + 5;
        spectrum.SetLevels((float)levelLower.Value, (float)levelUpper.Value);
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
            gainMode.SelectedIndex == 1 ? (rfGain.SelectedItem as GainOption)?.TenthsDb : null, (int)fftSize.SelectedItem!, fmEnabled.Checked,
            (Dsp.FftWindow)fftWindow.SelectedItem!, ((RateOption)rxBandwidth.SelectedItem!).Hertz);
        busy = true;
        SetControls();
        try
        {
            status.Text = retune ? "受信設定を変更しています…" : start ? "接続しています…" : "切断しています…";
            if (start && !closing)
            {
                if (receiver is null)
                {
                    receiver = new Receiver { Volume = volume.Value / 100f };
                    await receiver.StartAsync((uint)devices.SelectedIndex, requestedHz, settings);
                    spectrum.Clear();
                }
                else
                {
                    var changes = await receiver.UpdateAsync(requestedHz, settings);
                    if (changes.Spectrum) spectrum.Clear();
                }
                frequency.Text = FrequencyInput.Format(receiver.Frequency);
                spectrum.CenterFrequency = receiver.Frequency;
                spectrum.SampleRate = receiver.SampleRate;
                spectrum.RxBandwidth = settings.RxBandwidth;
                spectrum.FmEnabled = settings.FmEnabled;
                spectrum.Invalidate();
                PopulateGains(receiver.SupportedGains, receiver.AppliedGain);
                UpdateBandwidthOptions(receiver.SampleRate);
                lastBytes = 0;
                lastData = DateTime.UtcNow;
                status.Text = "受信データを待っています…";
            }
            else { await StopReceiverAsync(); status.Text = "未接続"; }
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
        audioStatus.Text = receiver.AudioFailure is { } audioError ? $"FM音声エラー: {audioError.Message}"
            : fmEnabled.Checked ? "FM ON / 48 kHz" : "FM OFF";
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
        sampleRate.Enabled = bandwidth.Enabled = fftSize.Enabled = fftWindow.Enabled = rxBandwidth.Enabled = enabled;
        gainMode.Enabled = enabled && receiver is not null && rfGain.Items.Count > 0;
        rfGain.Enabled = gainMode.Enabled && gainMode.SelectedIndex == 1;
        spectrum.CanTune = enabled && receiver is not null;
        fmEnabled.Enabled = enabled;
        volume.Enabled = !closing;
        if (receiver is null) audioStatus.Text = fmEnabled.Checked ? "FM ON（接続待ち）" : "FM OFF";
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
