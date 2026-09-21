using System.Diagnostics;
using System.Numerics;
using AfSdr.Dsp;
using AfSignalGenerator;

namespace AfSdr.Checks;

internal static class IqTrajectoryChecks
{
    private static void Require(bool pass, string message) { if (!pass) throw new Exception("IQ trajectory: " + message); }
    private static float[] Read(string path)
    {
        using var reader = new IqWaveReader(path); var result = new List<float>(); float[] block;
        while ((block = reader.Read(16384)).Length != 0) result.AddRange(block);
        return result.ToArray();
    }
    private static ConstellationFrame Process(float[] iq, double alpha = .35, int baud = 9600, double offset = 0, int blockSize = 2468, uint rate = 250000)
    {
        var dsp = new ConstellationProcessor(rate, new(true, DigitalMode.Iq, baud, alpha, FineFrequencyOffset: offset));
        for (int n = 0; n < iq.Length; n += blockSize) dsp.Process(iq.AsSpan(n, Math.Min(blockSize, iq.Length - n)));
        return dsp.Snapshot(0);
    }
    private static double Evm(ConstellationFrame frame)
    {
        double rms = Math.Sqrt(frame.Points.Average(p => p.X * p.X + p.Y * p.Y));
        // Remove one constant orientation only for the metric; no receiver carrier loop.
        Complex moment = frame.Points.Aggregate(Complex.Zero, (sum, p) => sum - Complex.Pow(new Complex(p.X, p.Y) / rms, 4));
        Complex rotate = Complex.FromPolarCoordinates(1, -moment.Phase / 4);
        return Math.Sqrt(frame.Points.Average(p =>
        {
            var z = new Complex(p.X, p.Y) / rms * rotate;
            return Math.Pow(Math.Abs(z.Real) - Math.Sqrt(.5), 2) + Math.Pow(Math.Abs(z.Imaginary) - Math.Sqrt(.5), 2);
        }));
    }
    internal static void Run()
    {
        VerifyResampler();
        string folder = Path.Combine(Path.GetTempPath(), "AF-IQTrajectory-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var signal = new SignalSettings { Duration = .8 };
            var goldenPath = SignalWriter.Generate(signal, folder).WavePath;
            var iq = Read(goldenPath);
            VerifyFileAndForm(goldenPath);
            var frame = Process(iq);
            var whole = Process(iq, blockSize: iq.Length);
            Require(frame.Points.SequenceEqual(whole.Points) && frame.IqTrajectory!.SequenceEqual(whole.IqTrajectory!)
                && frame.Symbols == whole.Symbols, "arbitrary input block boundaries preserve all samples and representative points");
            Require(frame.Points.Length == 1024 && frame.IqTrajectory!.Length == 8192 && frame.IqSamplesPerSymbol == 8, "bounded integer 8 SPS history");
            int representative = Enumerable.Range(0, 8).Single(p => frame.Points.Select((v, n) => v == frame.IqTrajectory![n * 8 + p]).All(v => v));
            Require(representative >= 0 && Evm(frame) < .08, "stable representative phase on Golden QPSK");
            Console.WriteLine($"IQ observation Golden: EVM {Evm(frame):P3}, representative phase {representative}/8, {frame.IqTrajectory!.Length} trajectory samples");
            // The path between representative points must deviate from the straight chord.
            double squaredDistance = 0; int segments = 0;
            for (int symbol = 0; symbol < frame.Points.Length - 1; symbol++)
            {
                var a = frame.Points[symbol]; var b = frame.Points[symbol + 1];
                double dx = b.X - a.X, dy = b.Y - a.Y, length = dx * dx + dy * dy;
                if (length < .5) continue;
                for (int k = 1; k < 8; k++)
                {
                    var p = frame.IqTrajectory[symbol * 8 + representative + k];
                    double cross = dx * (p.Y - a.Y) - dy * (p.X - a.X);
                    squaredDistance += cross * cross / length; segments++;
                }
            }
            double curvature = Math.Sqrt(squaredDistance / segments);
            Console.WriteLine($"IQ trajectory RMS distance from symbol chord: {curvature:F5}");
            Require(curvature > .025, "intermediate IQ follows pulse-shaped waveform rather than straight symbol chords");
            var low = Process(iq, .2); var high = Process(iq, 1);
            double difference = Math.Sqrt(low.IqTrajectory!.Zip(high.IqTrajectory!, (a, b) => Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2)).Average());
            Console.WriteLine($"Same recording, receiver RRC .2 vs 1.0 trajectory RMS difference: {difference:F5}");
            Require(difference > .02, "receiver alpha actually changes intermediate samples");
            foreach (double alpha in new[] { .2, 1.0 })
            {
                var matched = Process(Read(SignalWriter.Generate(signal with { RrcAlpha = alpha }, folder).WavePath), alpha);
                Console.WriteLine($"IQ matched alpha {alpha}: EVM {Evm(matched):P3}");
                Require(Evm(matched) < .08, "representative phase stable for matched alpha " + alpha);
                Render(matched, alpha, $"iq-trajectory-alpha-{alpha:0.0}-check.png");
            }
            var wrong = Process(iq, baud: 9700);
            Require(Evm(wrong) > Evm(frame) + .10, "fixed requested baud does not track away intentional clock error");
            var offsetIq = Read(SignalWriter.Generate(signal with { FrequencyOffset = 100 }, folder).WavePath);
            Require(Evm(Process(offsetIq, offset: 100)) < .08 && Evm(Process(offsetIq)) > .2, "manual correction feeds IQ trajectory; no hidden carrier synchronization");
            foreach (double timing in new[] { .17, .63 })
            {
                var shifted = Process(Read(SignalWriter.Generate(signal with { TimingOffsetSymbols = timing }, folder).WavePath));
                Require(Evm(shifted) < .08, "initial phase selection handles unknown timing");
            }
            foreach (uint rate in new uint[] { 1024000, 3200000 })
            {
                var dsp = new ConstellationProcessor(rate, new(true, DigitalMode.Iq));
                dsp.Process(ConstellationChecks.Signal(rate, 9600, DigitalMode.Qpsk, 0, .45));
                Require(Evm(dsp.Snapshot(0)) < .08, "IQ display after existing live decimation chain at " + rate);
            }
            using var view = new ConstellationView { Size = new Size(800, 760) }; _ = view.Handle;
            view.Display(frame.Points, "IQ 8 SPS", new(true, DigitalMode.Iq), iqPath: frame.IqTrajectory, samplesPerSymbol: 8);
            foreach (int count in DigitalViewSettings.SymbolCounts)
            {
                view.SetPresentation(count, true);
                Require(view.DisplayedPoints.SequenceEqual(frame.Points.AsSpan(1024 - count))
                    && view.DisplayedTrajectory.SequenceEqual(frame.IqTrajectory.AsSpan(8192 - count * 8)), "N points and matching N*8 samples in order");
            }
            using var image = new Bitmap(view.Width, view.Height);
            var clock = Stopwatch.StartNew();
            for (int n = 0; n < 50; n++) view.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            Console.WriteLine($"8192-sample IQ trajectory draw: {clock.Elapsed.TotalMilliseconds / 50:F2} ms/frame");
            view.SetPresentation(256, false); view.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save("iq-points-only-check.png");
            // Dedicated modes keep the existing symbol-to-symbol auxiliary line.
            foreach (var mode in Enum.GetValues<DigitalMode>().Where(m => m != DigitalMode.Iq))
            {
                view.Display(frame.Points, mode.ToString(), new(true, mode));
                Require(view.DisplayedTrajectory.SequenceEqual(view.DisplayedPoints), "dedicated trajectory unchanged: " + mode);
            }
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("PASS: IQ-only 8 SPS resampling, anti-aliasing, initial phase, raw intermediate path, RRC response, fixed baud, fine correction, bounded drawing and block continuity.");
    }
    private static void VerifyFileAndForm(string path)
    {
        Task.Run(async () =>
        {
            var receiver = new FileReceiver(path, 100000000, new ReceiveSettings(250000, Digital: new(true, DigitalMode.Iq)));
            try
            {
                await receiver.SetPausedAsync(false);
                var clock = Stopwatch.StartNew();
                while (receiver.Constellation is not { Points.Length: >= 512 } && clock.Elapsed.TotalSeconds < 3) await Task.Delay(10);
                await receiver.SetPausedAsync(true);
                Require(receiver.Failure is null && receiver.DigitalFailure is null && receiver.Constellation is { IqTrajectory.Length: >= 4096 }, "real-time WAV publishes IQ samples as well as points");
            }
            finally { await receiver.StopAsync(); }
        }).GetAwaiter().GetResult();
        using var form = new DigitalForm(new(true, DigitalMode.Iq));
        var fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        ((CheckBox)typeof(DigitalForm).GetField("enabled", fields)!.GetValue(form)!).Checked = true;
        var frame = Process(Read(path)); form.Display(frame, 100000000, null, true);
        var view = (ConstellationView)typeof(DigitalForm).GetField("view", fields)!.GetValue(form)!;
        Require(view.DisplayedPoints.Length == 256 && view.DisplayedTrajectory.Length == 2048, "digital form forwards separate symbol/path buffers");
        ((CheckBox)typeof(DigitalForm).GetField("trajectory", fields)!.GetValue(form)!).Checked = true;
        ((ComboBox)typeof(DigitalForm).GetField("symbolCount", fields)!.GetValue(form)!).SelectedItem = 512;
        Require(view.DisplayedPoints.Length == 512 && view.DisplayedTrajectory.Length == 4096, "existing UI changes IQ history length immediately");
        ((CheckBox)typeof(DigitalForm).GetField("enabled", fields)!.GetValue(form)!).Checked = false;
        form.Display(frame, 100000000, null, true);
        Require(view.DisplayedPoints.IsEmpty && view.DisplayedTrajectory.IsEmpty, "OFF clears both display paths");
    }
    private static void Render(ConstellationFrame frame, double alpha, string path)
    {
        using var view = new ConstellationView { Size = new Size(800, 760) }; _ = view.Handle;
        view.SetPresentation(256, true);
        view.Display(frame.Points, $"I/Q observation / QPSK 9600 baud / RRC α={alpha} / 8 SPS", new(true, DigitalMode.Iq, Rolloff: alpha),
            iqPath: frame.IqTrajectory, samplesPerSymbol: 8);
        using var bitmap = new Bitmap(view.Width, view.Height); view.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(path);
    }
    private static void VerifyResampler()
    {
        foreach (double inputRate in new[] { 125000.0, 128000.0, 200000.0 })
        {
            const int baud = 17000; const double tone = 1400;
            var dsp = new IqDisplayProcessor(inputRate, baud);
            int samples = (int)inputRate;
            for (int n = 0; n < samples; n++) dsp.Push(Complex.FromPolarCoordinates(.7, 2 * Math.PI * tone * n / inputRate));
            var frame = dsp.Snapshot(0); var path = frame.IqTrajectory!;
            double sum = 0;
            for (int n = 1; n < path.Length; n++) sum += (new Complex(path[n].X, path[n].Y) * Complex.Conjugate(new Complex(path[n - 1].X, path[n - 1].Y))).Phase;
            double measured = sum / (path.Length - 1) * baud * 8 / (2 * Math.PI);
            Require(Math.Abs(measured - tone) < .02 && path.All(p => Math.Abs(Math.Sqrt(p.X * p.X + p.Y * p.Y) - .7) < .001), "fractional resampling retains tone frequency/amplitude");
            double expected = ((samples - 1 - 32) - 64 * inputRate / baud) / (inputRate / (baud * 8.0));
            Require(Math.Abs(dsp.ResampledCount - expected) < 3, "output sample clock has integer 8 SPS rate");
        }
        var alias = new IqDisplayProcessor(125000, 9600);
        for (int n = 0; n < 125000; n++) alias.Push(Complex.FromPolarCoordinates(1, 2 * Math.PI * 45000 * n / 125000));
        Require(alias.Snapshot(0).IqTrajectory!.Average(p => p.X * p.X + p.Y * p.Y) < 1e-6, "fractional FIR suppresses out-of-band energy before downsampling");
    }
}
