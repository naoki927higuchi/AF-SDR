namespace AfSdr;

internal sealed partial class MainForm
{
    private readonly ComboBox inputSource = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 155 };
    private readonly Label frequencyLabel = new() { Text = "中心周波数 (Hz / k / M)", AutoSize = true, Padding = new Padding(0, 6, 8, 0) };
    private readonly TextBox filePath = new() { Width = 380 };
    private readonly Button browseFile = new() { Text = "IQ WAVを開く…", AutoSize = true };
    private readonly Button openPath = new() { Text = "読み込み", AutoSize = true };
    private readonly TextBox recordFrequency = new() { Width = 125, PlaceholderText = "記録中心 Hz/k/M" };
    private readonly Button applyRecord = new() { Text = "記録中心を補正", AutoSize = true };
    private readonly CheckBox swapIq = new() { Text = "I/Q入替", AutoSize = true };
    private readonly Button playFile = new() { Text = "再生", AutoSize = true };
    private readonly Button stopFile = new() { Text = "停止（先頭）", AutoSize = true };
    private readonly CheckBox loopFile = new() { Text = "ループ", AutoSize = true };
    private readonly TrackBar seekFile = new() { Minimum = 0, Maximum = 10000, TickStyle = TickStyle.None, Width = 240, Height = 30 };
    private readonly Label filePosition = new() { AutoSize = true, Text = "0.000 / 0.000 秒" };
    private readonly Label fileInfo = new() { AutoSize = true, MaximumSize = new Size(1150, 0) };
    private FlowLayoutPanel filePanel = null!;
    private IqWaveInfo? selectedInfo;
    private string? loadedPath;
    private bool seeking;
    private DateTime fileErrorUntil;
    private int lastFileRevision;
    private uint liveRate, liveFrequency;
    private bool FileMode => inputSource.SelectedIndex == 1;

    private Control BuildFileControls()
    {
        inputSource.Items.AddRange(["RTL-SDR", "IQ WAVファイル"]);
        inputSource.SelectedIndex = 0;
        filePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(10, 0, 10, 8), Visible = false };
        filePanel.Controls.AddRange([filePath, browseFile, openPath, recordFrequency, applyRecord, swapIq,
            playFile, stopFile, loopFile, seekFile, filePosition, fileInfo]);
        filePanel.SetFlowBreak(swapIq, true);
        filePanel.SetFlowBreak(filePosition, true);
        inputSource.SelectionChangeCommitted += async (_, _) =>
        {
            if (busy || closing) return;
            busy = true; SetControls();
            try
            {
                if (FileMode) { liveRate = ((RateOption)sampleRate.SelectedItem!).Hertz; liveFrequency = receiver?.Frequency ?? lastFrequency; }
                await StopReceiverAsync(); selectedInfo = null; loadedPath = null;
                fileInfo.Text = "未読み込み"; filePosition.Text = "0.000 / 0.000 秒"; seekFile.Value = 0;
                spectrum.TuneFrequency = null;
                if (!FileMode)
                {
                    SetFileRate(liveRate); frequency.Text = FrequencyInput.Format(liveFrequency); lastFrequency = liveFrequency;
                    spectrum.CenterFrequency = liveFrequency; RefreshDevices();
                }
                else status.Text = "IQ WAVファイルを選択してください。";
            }
            catch (Exception ex) { ShowError(ex); }
            finally { busy = false; SetControls(); }
        };
        browseFile.Click += async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "IQ WAV (*.wav)|*.wav|すべてのファイル (*.*)|*.*", CheckFileExists = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) { filePath.Text = dialog.FileName; await LoadIqFileAsync(); }
        };
        openPath.Click += async (_, _) => await LoadIqFileAsync();
        playFile.Click += async (_, _) => await FileOperationAsync(async () =>
        {
            var file = await EnsureFileReceiverAsync();
            await file.SetPausedAsync(!file.Paused);
        });
        stopFile.Click += async (_, _) => await FileOperationAsync(async () => { if (receiver is FileReceiver file) await file.SeekAsync(0, true); });
        applyRecord.Click += async (_, _) => await ReconfigureFileAsync();
        swapIq.CheckedChanged += async (_, _) => { if (!updatingSettings) await ReconfigureFileAsync(); };
        loopFile.CheckedChanged += (_, _) => { if (receiver is FileReceiver file) file.Loop = loopFile.Checked; };
        seekFile.MouseDown += (_, _) => seeking = true;
        seekFile.MouseUp += async (_, _) => { seeking = false; await SeekFileAsync(); };
        seekFile.KeyUp += async (_, _) => await SeekFileAsync();
        return filePanel;
    }

    private void RestoreFileSettings()
    {
        liveRate = savedSettings.SampleRate; liveFrequency = savedSettings.Frequency;
        filePath.Text = savedSettings.LastIqPath;
        inputSource.SelectedIndex = savedSettings.FileInput ? 1 : 0;
        // Restoring a path does not open network files or start DSP/audio.
    }

    private void SetFileRate(uint rate)
    {
        updatingSettings = true;
        try
        {
            sampleRate.Items.Clear();
            if (FileMode) sampleRate.Items.Add(new RateOption(rate, $"{rate / 1e6:0.######} MS/s (ファイル)"));
            else foreach (uint r in ReceiveSettings.Rates) sampleRate.Items.Add(new RateOption(r, $"{r / 1e6:0.###} MS/s"));
            sampleRate.SelectedIndex = FileMode ? 0 : Math.Max(0, Array.IndexOf(ReceiveSettings.Rates, rate));
            spectrum.SampleRate = rate;
            if (rate is >= 250_000 and <= 3_200_000) digitalForm.SetSampleRate(rate);
        }
        finally { updatingSettings = false; }
        UpdateBandwidthOptions(rate);
    }

    private ReceiveSettings FileSettings() => new(selectedInfo!.Rate, null, (int)fftSize.SelectedItem!, fmEnabled.Checked,
        (Dsp.FftWindow)fftWindow.SelectedItem!, ((RateOption)rxBandwidth.SelectedItem!).Hertz, digitalForm.Settings);

    private async Task LoadIqFileAsync() => await FileOperationAsync(async () =>
    {
        string path = filePath.Text;
        var info = await Task.Run(() => { using var read = new IqWaveReader(path); return read.Info; });
        await StopReceiverAsync();
        selectedInfo = info; loadedPath = path;
        recordFrequency.Text = info.FilenameFrequency is uint hz ? FrequencyInput.Format(hz) : "";
        frequency.Text = recordFrequency.Text;
        spectrum.CenterFrequency = info.FilenameFrequency ?? 0; spectrum.TuneFrequency = info.FilenameFrequency;
        SetFileRate(info.Rate);
        fileInfo.Text = info + (info.FilenameFrequency is null ? " | 記録中心を入力してください" : " | 記録中心: ファイル名由来")
            + (info.Playable ? "" : " | 再生非対応: 250 kS/s～3.2 MS/sが必要です");
        filePosition.Text = $"0.000 / {info.Seconds:F3} 秒"; seekFile.Value = 0;
        status.Text = "読み込み完了。再生ボタンで開始します（FMは放送用モノラル）。";
    });

    private async Task<FileReceiver> EnsureFileReceiverAsync()
    {
        if (receiver is FileReceiver current) return current;
        if (selectedInfo is null || loadedPath is null) throw new InvalidOperationException("先にIQ WAVを読み込んでください。");
        if (!FrequencyInput.TryParse(recordFrequency.Text, out uint center, out string error)) throw new ArgumentException(error);
        var settings = FileSettings();
        bool swap = swapIq.Checked;
        string path = loadedPath;
        var file = await Task.Run(() => new FileReceiver(path, center, settings, swap));
        if (file.Info != selectedInfo) { await file.StopAsync(); throw new IOException("ファイル情報が変更されました。読み込み直してください。"); }
        receiver = file; file.Volume = volume.Value / 100f; file.Loop = loopFile.Checked;
        frequency.Text = FrequencyInput.Format(center); spectrum.CenterFrequency = center; spectrum.TuneFrequency = center;
        spectrum.RxBandwidth = file.Settings.RxBandwidth; spectrum.FmEnabled = file.Settings.FmEnabled;
        lastFileRevision = file.DisplayRevision; spectrum.Clear();
        return file;
    }

    private async Task ApplyFileSettingsAsync(uint? frequencyOverride = null)
    {
        if (receiver is not FileReceiver file) { SetControls(); return; }
        await FileOperationAsync(async () =>
        {
            uint hz = frequencyOverride ?? file.Frequency;
            // Explicit Apply/Enter uses text; setting changes pass the current frequency.
            if (frequencyOverride is null && !FrequencyInput.TryParse(frequency.Text, out hz, out string error)) throw new ArgumentException(error);
            try
            {
                await file.UpdateAsync(hz, FileSettings());
                frequency.Text = FrequencyInput.Format(file.Frequency);
                spectrum.TuneFrequency = file.Frequency; spectrum.RxBandwidth = FileSettings().RxBandwidth; spectrum.FmEnabled = fmEnabled.Checked;
                spectrum.Invalidate();
            }
            catch
            {
                frequency.Text = FrequencyInput.Format(file.Frequency);
                updatingSettings = true;
                try
                {
                    fmEnabled.Checked = file.Settings.FmEnabled;
                    fftSize.SelectedItem = file.Settings.FftSize;
                    fftWindow.SelectedItem = file.Settings.Window;
                    rxBandwidth.SelectedIndex = Array.IndexOf(ReceiveSettings.RxBandwidths, file.Settings.RxBandwidth);
                    digitalForm.RestoreOptions(file.Settings.DigitalOptions);
                }
                finally { updatingSettings = false; }
                throw;
            }
        });
    }

    private async Task ReconfigureFileAsync()
    {
        if (!FileMode || busy || updatingSettings) return;
        await FileOperationAsync(async () =>
        {
            if (!FrequencyInput.TryParse(recordFrequency.Text, out uint center, out string error)) throw new ArgumentException(error);
            if (receiver is FileReceiver file) await file.ReconfigureAsync(center, swapIq.Checked);
            frequency.Text = recordFrequency.Text = FrequencyInput.Format(center);
            spectrum.CenterFrequency = center; spectrum.TuneFrequency = center;
            fileInfo.Text = selectedInfo + " | 記録中心: 手動設定";
        });
    }

    private async Task SeekFileAsync() => await FileOperationAsync(async () =>
    {
        var file = await EnsureFileReceiverAsync();
        await file.SeekAsync((long)(file.Info.Frames * (seekFile.Value / 10000.0)));
    });

    private async Task FileOperationAsync(Func<Task> action)
    {
        if (busy || closing) return;
        busy = true; SetControls();
        try { await action(); }
        catch (Exception ex) { ShowError(ex); fileErrorUntil = DateTime.UtcNow.AddSeconds(5); }
        finally { busy = false; SetControls(); }
    }

    private void SetFileControls(bool enabled)
    {
        inputSource.Enabled = enabled;
        filePanel.Visible = FileMode;
        frequencyLabel.Text = FileMode ? "選局周波数 (Hz / k / M)" : "中心周波数 (Hz / k / M)";
        if (!FileMode) return;
        connect.Enabled = devices.Enabled = refresh.Enabled = sampleRate.Enabled = gainMode.Enabled = rfGain.Enabled = false;
        filePath.Enabled = browseFile.Enabled = openPath.Enabled = enabled;
        recordFrequency.Enabled = applyRecord.Enabled = swapIq.Enabled = enabled && selectedInfo is not null;
        playFile.Enabled = stopFile.Enabled = seekFile.Enabled = enabled && selectedInfo?.Playable == true;
        playFile.Text = receiver is FileReceiver { Paused: false } ? "一時停止" : "再生";
        loopFile.Enabled = enabled;
    }

    private void UpdateFileDisplay(FileReceiver file)
    {
        if (lastFileRevision != file.DisplayRevision) { lastFileRevision = file.DisplayRevision; spectrum.Clear(); }
        spectrum.DisplayFrame(file.Spectrum);
        if (digitalForm.Visible) digitalForm.Display(file.Constellation, file.Frequency, file.DigitalFailure, true);
        filePosition.Text = $"{file.Position / (double)file.SampleRate:F3} / {file.Info.Seconds:F3} 秒";
        if (!seeking) seekFile.Value = file.Info.Frames == 0 ? 0 : (int)Math.Clamp(file.Position * 10000.0 / file.Info.Frames, 0, 10000);
        playFile.Text = file.Paused ? "再生" : "一時停止";
        audioStatus.Text = file.AudioFailure is { } ex ? $"FM音声エラー: {ex.Message}" : fmEnabled.Checked ? "FM / 48 kHz" : "FM OFF";
        if (DateTime.UtcNow < fileErrorUntil) return;
        status.Text = $"{(file.Ended ? "再生終了" : file.Paused ? "一時停止" : file.Buffering ? "バッファ待ち / 処理遅延" : "IQ再生中（1倍）")} | 記録中心 {FrequencyInput.Format(file.RecordCenter)} | 選局 {FrequencyInput.Format(file.Frequency)} | {file.SampleRate:N0} S/s";
    }
}
