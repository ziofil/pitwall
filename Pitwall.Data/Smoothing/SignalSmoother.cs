using System.Collections.Concurrent;

namespace Pitwall.Data;

/// <summary>
/// Turns values that arrive in bursts (gaps, intervals, car telemetry, roughly once a second)
/// into continuous ones that can be drawn at a high frame rate. For each key it keeps the most
/// recent samples, fits a least-squares polynomial through them (quadratic when there are enough
/// samples, linear or constant otherwise), and evaluates it at the requested time.
/// Callers evaluate slightly behind the newest data, so the value is mostly interpolated between
/// real samples rather than extrapolated beyond them.
/// <para>
/// A new sample changes the fitted curve. Switching to the new curve at once would make the shown
/// value jump, so instead the value blends from the old curve to the new one over
/// <paramref name="blend"/>, keeping it continuous. Only a real discontinuity (see
/// <paramref name="resetThreshold"/>) shows as a jump.
/// </para>
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
/// <param name="blend">How long the value takes to move from the old curve to a new one.</param>
/// <param name="maxSamples">How many recent samples to fit through.</param>
public sealed class SignalSmoother(
    TimeSpan window,
    TimeSpan maxExtrapolation,
    double resetThreshold,
    TimeSpan blend,
    int maxSamples = 6
)
{
    /// <summary>
    /// What is shown: the current fitted curve, blending in from the curve shown before it.
    /// </summary>
    private sealed record Curve(
        double[]? Coefficients,
        DateTimeOffset Origin,
        double First,
        double Last,
        Curve? Previous,
        DateTimeOffset BlendStart
    );

    private sealed class Series
    {
        public readonly List<(DateTimeOffset Time, double Value)> Samples = [];
        public Curve? Curve;

        // The latest time this series was shown at, which is where a blend starts from
        public DateTimeOffset LastShown;
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

            // Compare with the last real sample, not the curve's trend, which may be well past it
            var reset = samples.Count > 0 && Math.Abs(samples[^1].Value - value) > resetThreshold;
            if (reset)
                samples.Clear();

            samples.Add((time, value));
            var oldest = time - window;
            samples.RemoveAll(x => x.Time < oldest);
            if (samples.Count > maxSamples)
                samples.RemoveRange(0, samples.Count - maxSamples);

            // Blend from whatever was being shown, unless this is a discontinuity to show as-is
            var previous = reset ? null : series.Curve;
            if (previous?.Previous is not null && series.LastShown >= previous.BlendStart + blend)
            {
                // The previous blend has finished, so its older curve is no longer needed
                previous = previous with
                {
                    Previous = null,
                };
            }
            series.Curve = Fit(
                samples,
                previous,
                series.LastShown > DateTimeOffset.MinValue ? series.LastShown : time
            );
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
            if (series.Curve is null)
                return false;
            if (at > series.LastShown)
                series.LastShown = at;
            value = Evaluate(series.Curve, at);
            return true;
        }
    }

    private Curve Fit(
        List<(DateTimeOffset Time, double Value)> samples,
        Curve? previous,
        DateTimeOffset blendStart
    )
    {
        // Work in seconds relative to the newest sample to keep the numbers well conditioned
        var origin = samples[^1].Time;
        var first = (samples[0].Time - origin).TotalSeconds;
        var coefficients = samples.Count switch
        {
            1 => [samples[0].Value],
            < 4 => FitPolynomial(samples, origin, 1),
            _ => FitPolynomial(samples, origin, 2),
        };
        coefficients ??= [samples[^1].Value];
        return new Curve(
            coefficients,
            origin,
            first,
            maxExtrapolation.TotalSeconds,
            previous,
            blendStart
        );
    }

    private double Evaluate(Curve curve, DateTimeOffset at)
    {
        var value = EvaluateFit(curve, at);
        if (curve.Previous is null || blend <= TimeSpan.Zero)
            return value;

        var progress = (at - curve.BlendStart) / blend;
        if (progress >= 1)
            return value;
        if (progress <= 0)
            return Evaluate(curve.Previous, at);

        // Smoothstep, so the value eases from the old curve on to the new one
        var weight = progress * progress * (3 - (2 * progress));
        var old = Evaluate(curve.Previous, at);
        return old + ((value - old) * weight);
    }

    private static double EvaluateFit(Curve curve, DateTimeOffset at)
    {
        var coefficients = curve.Coefficients!;
        var t = Math.Clamp((at - curve.Origin).TotalSeconds, curve.First, curve.Last);

        // When extrapolating, follow only the straight-line trend: a quadratic can run away
        if (t > 0)
        {
            var slope = coefficients.Length > 1 ? coefficients[1] : 0;
            return coefficients[0] + (slope * t);
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
                    (matrix[pivot, column], matrix[best, column]) = (
                        matrix[best, column],
                        matrix[pivot, column]
                    );
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
