using System.Collections.Concurrent;

namespace Pitwall.Data;

/// <summary>
/// Turns values that arrive in bursts (gaps, intervals, car telemetry, roughly once a second)
/// into continuous ones that can be drawn at a high frame rate. For each key it keeps the most
/// recent samples, fits a least-squares polynomial through them (quadratic when there are enough
/// samples, linear or constant otherwise), and evaluates it at the requested time.
/// Callers evaluate slightly behind the newest data, so the value is mostly interpolated between
/// real samples rather than extrapolated beyond them.
/// </summary>
/// <param name="window">Samples older than this, relative to the newest one, are dropped.</param>
/// <param name="maxExtrapolation">
/// How far past the newest sample the fit may be followed. Beyond that the value holds, so a
/// stalled feed doesn't drift off.
/// </param>
/// <param name="resetThreshold">
/// A sample this far from what the fit predicted is treated as a discontinuity (a pit stop, a
/// change of car ahead) and restarts the series, so the jump isn't smeared across the fit.
/// </param>
/// <param name="maxSamples">How many recent samples to fit through.</param>
public sealed class SignalSmoother(
    TimeSpan window,
    TimeSpan maxExtrapolation,
    double resetThreshold,
    int maxSamples = 6
)
{
    private sealed class Series
    {
        public readonly List<(DateTimeOffset Time, double Value)> Samples = [];
    }

    private readonly ConcurrentDictionary<string, Series> _series = new();

    public void Add(string key, DateTimeOffset time, double value)
    {
        var series = _series.GetOrAdd(key, _ => new Series());
        lock (series)
        {
            var samples = series.Samples;
            if (samples.Count > 0 && time <= samples[^1].Time)
            {
                // Out of order or duplicate time: keep the newest value for that time
                if (time == samples[^1].Time)
                    samples[^1] = (time, value);
                return;
            }

            if (
                samples.Count > 0
                && Evaluate(samples, time, maxExtrapolation) is { } predicted
                && Math.Abs(predicted - value) > resetThreshold
            )
            {
                samples.Clear();
            }

            samples.Add((time, value));
            var oldest = time - window;
            samples.RemoveAll(x => x.Time < oldest);
            if (samples.Count > maxSamples)
                samples.RemoveRange(0, samples.Count - maxSamples);
        }
    }

    /// <summary>Forgets the series, e.g. when the value stops being meaningful.</summary>
    public void Reset(string key) => _series.TryRemove(key, out _);

    /// <summary>The smoothed value of the series at the given time, if there is any data.</summary>
    public bool TryGet(string key, DateTimeOffset at, out double value)
    {
        value = default;
        if (!_series.TryGetValue(key, out var series))
            return false;
        lock (series)
        {
            if (Evaluate(series.Samples, at, maxExtrapolation) is not { } result)
                return false;
            value = result;
            return true;
        }
    }

    private static double? Evaluate(
        List<(DateTimeOffset Time, double Value)> samples,
        DateTimeOffset at,
        TimeSpan maxExtrapolation
    )
    {
        if (samples.Count == 0)
            return null;
        if (samples.Count == 1)
            return samples[0].Value;

        // Work in seconds relative to the newest sample to keep the numbers well conditioned
        var origin = samples[^1].Time;
        var first = (samples[0].Time - origin).TotalSeconds;
        var t = Math.Clamp((at - origin).TotalSeconds, first, maxExtrapolation.TotalSeconds);

        var degree = samples.Count >= 4 ? 2 : 1;
        var coefficients = FitPolynomial(samples, origin, degree);
        if (coefficients is null)
            return samples[^1].Value;

        // When extrapolating, follow only the straight-line trend: a quadratic can run away
        if (t > 0)
        {
            var slope = coefficients.Length > 1 ? coefficients[1] : 0;
            return Polynomial(coefficients, 0) + (slope * t);
        }
        return Polynomial(coefficients, t);
    }

    private static double Polynomial(double[] coefficients, double t)
    {
        var result = 0.0;
        for (var i = coefficients.Length - 1; i >= 0; i--)
            result = (result * t) + coefficients[i];
        return result;
    }

    /// <summary>
    /// Least-squares polynomial fit via the normal equations, solved by Gaussian elimination.
    /// Returns coefficients lowest power first, or <c>null</c> if the system is singular.
    /// </summary>
    private static double[]? FitPolynomial(
        List<(DateTimeOffset Time, double Value)> samples,
        DateTimeOffset origin,
        int degree
    )
    {
        var size = degree + 1;
        var matrix = new double[size, size + 1];
        foreach (var (time, value) in samples)
        {
            var x = (time - origin).TotalSeconds;
            var powers = new double[(2 * degree) + 1];
            powers[0] = 1;
            for (var i = 1; i < powers.Length; i++)
                powers[i] = powers[i - 1] * x;
            for (var row = 0; row < size; row++)
            {
                for (var column = 0; column < size; column++)
                    matrix[row, column] += powers[row + column];
                matrix[row, size] += powers[row] * value;
            }
        }

        for (var pivot = 0; pivot < size; pivot++)
        {
            var best = pivot;
            for (var row = pivot + 1; row < size; row++)
            {
                if (Math.Abs(matrix[row, pivot]) > Math.Abs(matrix[best, pivot]))
                    best = row;
            }
            if (Math.Abs(matrix[best, pivot]) < 1e-12)
                return null;
            if (best != pivot)
            {
                for (var column = 0; column <= size; column++)
                    (matrix[pivot, column], matrix[best, column]) = (matrix[best, column], matrix[pivot, column]);
            }
            for (var row = 0; row < size; row++)
            {
                if (row == pivot)
                    continue;
                var factor = matrix[row, pivot] / matrix[pivot, pivot];
                for (var column = pivot; column <= size; column++)
                    matrix[row, column] -= factor * matrix[pivot, column];
            }
        }

        var coefficients = new double[size];
        for (var i = 0; i < size; i++)
            coefficients[i] = matrix[i, size] / matrix[i, i];
        return coefficients;
    }
}
