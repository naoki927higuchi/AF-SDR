using System.Numerics;

namespace AfSdr.Dsp;

// Observation only: fixed symbol clock, no carrier loop, decisions or baud tracking.
// RRC/normalization and the manual NCO are upstream in ConstellationProcessor.
internal sealed class IqDisplayProcessor
{
    internal const int SamplesPerSymbol = 8;
    private const int Half = 32, Taps = 64, Phases = 1024, AcquisitionSymbols = 256;
    private readonly Complex[] input = new Complex[128];
    private readonly float[][] kernels = new float[Phases + 1][];
    private readonly PointF[] history = new PointF[1024 * SamplesPerSymbol];
    private readonly PointF[] group = new PointF[SamplesPerSymbol];
    private readonly double[] phasePower = new double[SamplesPerSymbol];
    private readonly double step;
    private long inputIndex = -1, resampled;
    private double startTime, nextTime;
    private int phaseIndex, acquisitionCount, selectedPhase, write, count;
    private long symbols;
    private bool acquired;

    internal IqDisplayProcessor(double inputRate, int symbolRate)
    {
        step = inputRate / (SamplesPerSymbol * (double)symbolRate);
        // Discard filter startup. A symmetric fractional-delay FIR needs 32 future samples.
        nextTime = startTime = 64 * inputRate / symbolRate;
        double cutoff = .35 * Math.Min(1, 1 / step);
        for (int phase = 0; phase <= Phases; phase++)
        {
            var weights = new float[Taps]; double sum = 0;
            for (int tap = 0; tap < Taps; tap++)
            {
                double distance = tap - (Half - 1) - phase / (double)Phases;
                double window = Math.Abs(distance) >= Half ? 0
                    : .42 + .5 * Math.Cos(Math.PI * distance / Half) + .08 * Math.Cos(2 * Math.PI * distance / Half);
                double weight = (Math.Abs(distance) < 1e-12 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * distance) / (Math.PI * distance)) * window;
                weights[tap] = (float)weight; sum += weight;
            }
            for (int tap = 0; tap < Taps; tap++) weights[tap] /= (float)sum;
            kernels[phase] = weights;
        }
    }

    internal void Push(Complex sample)
    {
        input[++inputIndex & 127] = sample;
        while ((long)Math.Floor(nextTime) + Half <= inputIndex)
        {
            long center = (long)Math.Floor(nextTime);
            int phase = (int)Math.Round((nextTime - center) * Phases);
            var weights = kernels[phase];
            double real = 0, imaginary = 0;
            for (int tap = 0; tap < Taps; tap++)
            {
                var value = input[(center + tap - (Half - 1)) & 127];
                real += value.Real * weights[tap]; imaginary += value.Imaginary * weights[tap];
            }
            // Derive time from the integer output count; repeated floating additions
            // would slowly accumulate a timing error during long observations.
            nextTime = startTime + (resampled + 1) * step;
            Add(new PointF((float)real, (float)imaginary));
        }
    }

    private void Add(PointF value)
    {
        resampled++;
        if (!acquired)
        {
            phasePower[phaseIndex] += (double)value.X * value.X + (double)value.Y * value.Y;
            if (++phaseIndex < SamplesPerSymbol) return;
            phaseIndex = 0;
            if (++acquisitionCount < AcquisitionSymbols) return;
            selectedPhase = Array.IndexOf(phasePower, phasePower.Max());
            double left = phasePower[(selectedPhase + SamplesPerSymbol - 1) % SamplesPerSymbol];
            double peak = phasePower[selectedPhase];
            double right = phasePower[(selectedPhase + 1) % SamplesPerSymbol];
            double curvature = left - 2 * peak + right;
            double fraction = curvature < -Math.Max(1e-20, peak * 1e-8)
                ? Math.Clamp(.5 * (left - right) / curvature, -.5, .5) : 0;
            // Refine the initial grid once, before exposing history. All subsequent
            // samples have exactly the configured interval; no symbol-clock tracking.
            startTime += fraction * step;
            nextTime = startTime + resampled * step;
            acquired = true;
            return;
        }
        group[phaseIndex++] = value;
        if (phaseIndex != SamplesPerSymbol) return;
        phaseIndex = 0;
        // Publish complete symbol intervals, so dots and trajectory cover identical time.
        Array.Copy(group, 0, history, write, SamplesPerSymbol);
        write = (write + SamplesPerSymbol) % history.Length;
        count = Math.Min(history.Length, count + SamplesPerSymbol);
        symbols++;
    }

    internal long ResampledCount => resampled;
    internal ConstellationFrame Snapshot(int discontinuities)
    {
        var samples = new PointF[count];
        int first = (write - count + history.Length) % history.Length;
        int tail = Math.Min(count, history.Length - first);
        Array.Copy(history, first, samples, 0, tail);
        Array.Copy(history, 0, samples, tail, count - tail);
        var points = new PointF[count / SamplesPerSymbol];
        for (int n = 0; n < points.Length; n++) points[n] = samples[n * SamplesPerSymbol + selectedPhase];
        return new(points, 0, symbols, discontinuities, IqTrajectory: samples, IqSamplesPerSymbol: SamplesPerSymbol);
    }
}
