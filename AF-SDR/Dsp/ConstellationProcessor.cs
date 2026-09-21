using System.Numerics;

namespace AfSdr.Dsp;

// Append values to preserve 1.8.0 JSON enum values.
internal enum DigitalMode { Iq, Bpsk, Qpsk, Qam, Pi4Qpsk, Ask, Fsk, Msk }
internal sealed record DigitalSettings(bool Enabled = false, DigitalMode Mode = DigitalMode.Qpsk, int SymbolRate = 9600, double Rolloff = 0.35,
    int QamOrder = 16, int AskOrder = 2, int FskOrder = 2, double FskSpacing = 4800, double FineFrequencyOffset = 0)
{
    internal const double MaximumFineOffset = 10000;
    internal bool RequiresReset(DigitalSettings next) => this with { FineFrequencyOffset = next.FineFrequencyOffset } != next;
    internal bool FrequencyMode => Mode is DigitalMode.Fsk or DigitalMode.Msk;
    internal double Spacing => Mode == DigitalMode.Msk ? SymbolRate / 2.0 : FskSpacing;
    internal int Tones => Mode == DigitalMode.Msk ? 2 : FskOrder;
    internal double ChannelCutoff => FrequencyMode ? (Tones - 1) * Spacing / 2 + SymbolRate
        : Mode == DigitalMode.Ask ? 1.5 * SymbolRate : (1 + Rolloff) * SymbolRate / 2;
    internal double MaximumSpacing(uint rate) => Math.Max(100, Math.Floor((0.4 * rate - SymbolRate) * 20 / (FskOrder - 1)) / 10);
    internal DigitalSettings Normalize(uint rate)
    {
        var result = this with
        {
            Enabled = false,
            FineFrequencyOffset = double.IsFinite(FineFrequencyOffset) ? Math.Round(Math.Clamp(FineFrequencyOffset, -MaximumFineOffset, MaximumFineOffset), 1) : 0,
            Mode = Enum.IsDefined(Mode) ? Mode : DigitalMode.Qpsk,
            SymbolRate = Math.Clamp(SymbolRate, 1000, (int)Math.Min(100000, rate / 8)),
            Rolloff = new[] { 0.2, 0.35, 0.5, 1.0 }.Contains(Rolloff) ? Rolloff : 0.35,
            QamOrder = QamOrder is 16 or 64 ? QamOrder : 16,
            AskOrder = AskOrder is 2 or 4 ? AskOrder : 2,
            FskOrder = FskOrder is 2 or 4 ? FskOrder : 2,
            FskSpacing = double.IsFinite(FskSpacing) ? Math.Clamp(FskSpacing, 100, 500000) : 4800
        };
        return result with { FskSpacing = Math.Min(result.FskSpacing, result.MaximumSpacing(rate)) };
    }
    internal void Validate(uint rate)
    {
        if (!double.IsFinite(FineFrequencyOffset) || Math.Abs(FineFrequencyOffset) > MaximumFineOffset)
            throw new ArgumentException("手動周波数補正は −10000.0 ～ +10000.0 Hz で指定してください。");
        if (!Enum.IsDefined(Mode) || SymbolRate < 1000 || SymbolRate > Math.Min(100000, rate / 8)
            || !new[] { 0.2, 0.35, 0.5, 1.0 }.Contains(Rolloff)
            || QamOrder is not (16 or 64) || AskOrder is not (2 or 4) || FskOrder is not (2 or 4)
            || !double.IsFinite(FskSpacing) || FskSpacing < 100 || FskSpacing > 500000
            || ChannelCutoff > rate * 0.4)
            throw new ArgumentException("baud・多値数・周波数間隔を確認してください。受信帯域に収まる設定が必要です。");
    }
}
internal sealed record ConstellationFrame(PointF[] Points, double FrequencyErrorHz, long Symbols, int Discontinuities,
    float[]? Trace = null, bool CarrierAcquired = true, PointF[]? IqTrajectory = null, int IqSamplesPerSymbol = 0);

// Streaming mode-specific channel filtering/detection -> Gardner timing -> visual measurements.
internal sealed class ConstellationProcessor
{
    private readonly List<ComplexFir> decimators = [];
    private readonly ComplexFir matched;
    private readonly DigitalSettings settings;
    private readonly FineFrequencyShifter fine;
    internal void SetFineFrequencyOffset(double hz) => fine.SetOffset(hz);
    private readonly double nominalPeriod, workingRate;
    private readonly QamCarrierRecovery? qam;
    private readonly IqDisplayProcessor? iqDisplay;
    private readonly Queue<float> trace = new();
    private readonly Queue<double> discriminator = new();
    private double discriminatorSum, traceTime, differentialFrequency;
    private Complex previousRf, previousDifferential, differentialMoment;
    private bool hasRf, hasDifferential;
    private double period, nextTime, sampleTime = -1, phase, frequency, power = 0.1;
    private Complex previousInput, previousHalf, previousSymbol;
    private bool half;
    private int halfCount;
    private long symbols;
    private readonly Queue<PointF> points = new();
    internal double FrequencyErrorHz => settings.Mode == DigitalMode.Qam ? qam!.FrequencyHz
        : settings.Mode == DigitalMode.Pi4Qpsk ? differentialFrequency * settings.SymbolRate / (2 * Math.PI)
        : settings.FrequencyMode || settings.Mode is DigitalMode.Ask or DigitalMode.Iq ? 0
        : frequency * settings.SymbolRate / (2 * Math.PI);

    internal ConstellationProcessor(uint rate, DigitalSettings settings)
    {
        settings.Validate(rate);
        this.settings = settings;
        fine = new FineFrequencyShifter(rate);
        fine.SetOffset(settings.FineFrequencyOffset);
        workingRate = rate;
        while (workingRate >= 16 * settings.SymbolRate && settings.ChannelCutoff <= workingRate * 0.2)
        {
            decimators.Add(new ComplexFir(Fir.LowPass(63, 0.25), 2));
            workingRate /= 2;
        }
        nominalPeriod = period = workingRate / settings.SymbolRate;
        matched = new ComplexFir(settings.FrequencyMode || settings.Mode == DigitalMode.Ask
            ? Fir.LowPass(63, settings.ChannelCutoff / workingRate)
            : RootRaisedCosine(nominalPeriod, settings.Rolloff), 1);
        if (settings.Mode == DigitalMode.Qam) qam = new QamCarrierRecovery(settings.QamOrder, settings.SymbolRate);
        if (settings.Mode == DigitalMode.Iq) iqDisplay = new IqDisplayProcessor(workingRate, settings.SymbolRate);
        nextTime = 12 * nominalPeriod;
    }

    internal static float[] RootRaisedCosine(double samplesPerSymbol, double beta)
    {
        int length = ((int)Math.Ceiling(10 * samplesPerSymbol)) | 1;
        var taps = new float[length];
        double sum = 0;
        for (int n = 0; n < length; n++)
        {
            double t = (n - (length - 1) / 2.0) / samplesPerSymbol;
            double value;
            if (Math.Abs(t) < 1e-9) value = 1 + beta * (4 / Math.PI - 1);
            else if (Math.Abs(Math.Abs(4 * beta * t) - 1) < 1e-8)
                value = beta / Math.Sqrt(2) * ((1 + 2 / Math.PI) * Math.Sin(Math.PI / (4 * beta)) + (1 - 2 / Math.PI) * Math.Cos(Math.PI / (4 * beta)));
            else value = (Math.Sin(Math.PI * t * (1 - beta)) + 4 * beta * t * Math.Cos(Math.PI * t * (1 + beta)))
                / (Math.PI * t * (1 - 16 * beta * beta * t * t));
            taps[n] = (float)value; sum += value;
        }
        for (int n = 0; n < length; n++) taps[n] /= (float)sum;
        return taps;
    }

    internal void Process(ReadOnlySpan<byte> iq) => Process(IqSamples.FromRtl(iq));

    internal void Process(ReadOnlySpan<float> iq)
    {
        if ((iq.Length & 1) != 0) throw new ArgumentException("I/Q samples must be paired.");
        for (int n = 0; n < iq.Length; n += 2)
        {
            float i = iq[n], q = iq[n + 1];
            fine.Shift(ref i, ref q);
            bool ready = true;
            foreach (var stage in decimators)
                if (!stage.Push(i, q, out i, out q)) { ready = false; break; }
            if (!ready) continue;
            matched.Push(i, q, out i, out q);
            double magnitude = i * i + q * q;
            power += (settings.Mode is DigitalMode.Qam or DigitalMode.Ask ? 0.0002 : 0.002) * (magnitude - power);
            var input = new Complex(i, q) / Math.Sqrt(Math.Max(power, 1e-6));
            sampleTime++;
            if (settings.Mode == DigitalMode.Ask)
            {
                input = new Complex(input.Magnitude, 0);
                AddTrace(input.Real);
            }
            if (settings.FrequencyMode)
            {
                var rf = new Complex(i, q);
                double hz = hasRf && rf.Magnitude > 1e-5 && previousRf.Magnitude > 1e-5
                    ? (rf * Complex.Conjugate(previousRf)).Phase * workingRate / (2 * Math.PI) : 0;
                previousRf = rf; hasRf = true;
                discriminator.Enqueue(hz); discriminatorSum += hz;
                if (discriminator.Count > Math.Max(1, (int)Math.Round(nominalPeriod / 4))) discriminatorSum -= discriminator.Dequeue();
                hz = discriminatorSum / discriminator.Count;
                input = new Complex(hz / settings.Spacing, 0);
                AddTrace(hz);
            }
            if (settings.Mode == DigitalMode.Iq)
            {
                iqDisplay!.Push(input);
            }
            else if (sampleTime >= nextTime)
            {
                double fraction = Math.Clamp(nextTime - (sampleTime - 1), 0, 1);
                var value = previousInput + fraction * (input - previousInput);
                double correction = 0;
                if (!half)
                {
                    if (halfCount >= 2)
                    {
                        double error = Math.Clamp(((previousSymbol - value) * Complex.Conjugate(previousHalf)).Real, -1, 1);
                        period = Math.Clamp(period + 0.0002 * error, nominalPeriod * 0.99, nominalPeriod * 1.01);
                        correction = 0.03 * error;
                    }
                    previousSymbol = value;
                    DisplaySymbol(value);
                    symbols++;
                }
                else previousHalf = value;
                half = !half; halfCount = Math.Min(halfCount + 1, 2);
                nextTime += period / 2 + correction;
            }
            previousInput = input;
        }
    }
    private void AddTrace(double value)
    {
        if (sampleTime < traceTime) return;
        trace.Enqueue((float)value); if (trace.Count > 128) trace.Dequeue();
        traceTime += nominalPeriod / 8;
    }
    private void DisplaySymbol(Complex value)
    {
        if (settings.FrequencyMode) { Add(new Complex(value.Real * settings.Spacing, 0)); return; }
        if (settings.Mode == DigitalMode.Ask) { Add(new Complex(Math.Max(0, value.Real), 0)); return; }
        if (settings.Mode == DigitalMode.Qam)
        {
            if (qam!.Push(value, out var corrected)) Add(corrected);
            return;
        }
        if (settings.Mode == DigitalMode.Pi4Qpsk)
        {
            if (hasDifferential && value.Magnitude > 1e-5 && previousDifferential.Magnitude > 1e-5)
            {
                var delta = value * Complex.Conjugate(previousDifferential);
                delta /= delta.Magnitude;
                var fourth = delta * delta; fourth *= -fourth;
                differentialMoment = 0.99 * differentialMoment + 0.01 * fourth;
                differentialFrequency = differentialMoment.Phase / 4;
                Add(delta * Complex.FromPolarCoordinates(1, -differentialFrequency));
            }
            previousDifferential = value; hasDifferential = true;
            return;
        }
        var rotated = value * Complex.FromPolarCoordinates(1, -phase);
        double carrierError = settings.Mode == DigitalMode.Bpsk
            ? Math.Sign(rotated.Real) * rotated.Imaginary
            : Math.Sign(rotated.Real) * rotated.Imaginary - Math.Sign(rotated.Imaginary) * rotated.Real;
        carrierError = Math.Clamp(carrierError, -1, 1);
        frequency = Math.Clamp(frequency + 0.0004 * carrierError, -0.15, 0.15);
        phase = Math.IEEERemainder(phase + frequency + 0.04 * carrierError, 2 * Math.PI);
        Add(rotated);
    }
    private void Add(Complex value)
    {
        points.Enqueue(new PointF((float)value.Real, (float)value.Imaginary));
        if (points.Count > 1024) points.Dequeue();
    }
    internal ConstellationFrame Snapshot(int discontinuities) => iqDisplay?.Snapshot(discontinuities) ?? new(points.ToArray(), FrequencyErrorHz, symbols, discontinuities, settings.FrequencyMode || settings.Mode == DigitalMode.Ask ? trace.ToArray() : null, qam?.Acquired ?? true);
}
