namespace AfSdr;

internal sealed class MainForm : Form
{
    private readonly ComboBox devices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
    private readonly Button refresh = new() { Text = "再検索", AutoSize = true };
    private readonly Button connect = new() { Text = "接続", AutoSize = true };
    private readonly Button apply = new() { Text = "周波数を適用", AutoSize = true };
    private readonly NumericUpDown frequency = new()
    {
        Minimum = 1, Maximum = uint.MaxValue, Value = 80_000_000,
        Increment = 100_000, ThousandsSeparator = true, Width = 170
    };
    private readonly SpectrumView spectrum = new();
    private readonly Label status = new() { AutoSize = true, Text = "未接続", Margin = new Padding(12, 8, 12, 8) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 40 };
    private Receiver? receiver;
    private bool busy, closing, allowClose;
    private long lastBytes;
    private DateTime lastData = DateTime.UtcNow;

    public MainForm()
    {
        Text = "AF-SDR";
        ClientSize = new Size(1120, 820);
        MinimumSize = new Size(820, 640);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Yu Gothic UI", 10);
        AutoScaleMode = AutoScaleMode.Dpi;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var connectionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10) };
        connectionRow.Controls.AddRange([new Label { Text = "RTL-SDR", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, devices, refresh, connect]);
        var tuningRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10, 0, 10, 8) };
        tuningRow.Controls.AddRange([new Label { Text = "中心周波数 (Hz)", AutoSize = true, Padding = new Padding(0, 6, 8, 0) }, frequency, apply,
            new Label { Text = "帯域 2.048 MHz  /  FFT 4096  /  自動ゲイン", AutoSize = true, Padding = new Padding(12, 6, 0, 0) }]);
        root.Controls.Add(connectionRow, 0, 0);
        root.Controls.Add(tuningRow, 0, 1);
        root.Controls.Add(spectrum, 0, 2);
        root.Controls.Add(status, 0, 3);
        Controls.Add(root);
        refresh.Click += (_, _) => RefreshDevices();
        connect.Click += async (_, _) => await ChangeConnectionAsync(false);
        apply.Click += async (_, _) => await ChangeConnectionAsync(true);
        spectrum.FrequencySelected += async hz =>
        {
            if (busy || closing || receiver is null) return;
            frequency.Value = hz;
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
        FormClosed += (_, _) => timer.Dispose();
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

    private async Task ChangeConnectionAsync(bool retune)
    {
        if (busy || closing) return;
        busy = true;
        SetControls();
        try
        {
            bool start = receiver is null || retune;
            status.Text = retune ? "中心周波数を変更しています…" : start ? "接続しています…" : "切断しています…";
            await StopReceiverAsync();
            if (start && !closing)
            {
                // A full stop/reopen prevents old-frequency samples appearing on the new axis.
                frequency.Validate();
                receiver = new Receiver();
                await receiver.StartAsync((uint)devices.SelectedIndex, (uint)frequency.Value);
                spectrum.CenterFrequency = receiver.Frequency;
                spectrum.SampleRate = receiver.SampleRate;
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
            : $"受信中  |  {receiver.Frequency / 1e6:F6} MHz  |  {receiver.SampleRate / 1e6:F3} MS/s  |  受信量 {bytes / 1048576.0:F1} MiB";
    }

    private void SetControls()
    {
        bool enabled = !busy && !closing;
        connect.Enabled = enabled && (receiver is not null || devices.SelectedIndex >= 0);
        connect.Text = receiver is null ? "接続" : "切断";
        refresh.Enabled = devices.Enabled = enabled && receiver is null;
        apply.Enabled = enabled && receiver is not null;
        frequency.Enabled = enabled;
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
