namespace AuctionResponse.Core;

/// <summary>Section 8 reference statistics. All causal: fitted only on permitted past data.</summary>
public static class Statistics
{
    /// <summary>
    /// Empirical percentile P(x) = #{j : x_j &lt;= x} / N over a sorted reference sample.
    /// </summary>
    public static double? Percentile(IReadOnlyList<double> sortedSample, double x)
    {
        if (sortedSample.Count == 0) return null;
        var count = 0;
        for (var i = 0; i < sortedSample.Count; i++) if (sortedSample[i] <= x) count++;
        return (double)count / sortedSample.Count;
    }

    /// <summary>
    /// Nearest-rank quantile, one-based: Q_p = x_(ceil(pN)) for 0 &lt; p &lt;= 1.
    /// </summary>
    public static double? Quantile(IReadOnlyList<double> sortedSample, double p)
    {
        if (sortedSample.Count == 0) return null;
        if (p <= 0d || p > 1d) throw new ArgumentOutOfRangeException(nameof(p), "Nearest-rank quantile is defined for 0 < p <= 1.");
        var rank = (int)Math.Ceiling(p * sortedSample.Count);
        if (rank < 1) rank = 1;
        if (rank > sortedSample.Count) rank = sortedSample.Count;
        return sortedSample[rank - 1];
    }

    /// <summary>Center sorted value for odd N; mean of the two center values for even N.</summary>
    public static double? Median(IReadOnlyList<double> sortedSample)
    {
        var n = sortedSample.Count;
        if (n == 0) return null;
        return (n % 2 == 1) ? sortedSample[n / 2] : (sortedSample[n / 2 - 1] + sortedSample[n / 2]) / 2d;
    }

    public static double[] Sorted(IEnumerable<double> values)
    {
        var a = values.ToArray();
        Array.Sort(a);
        return a;
    }

    /// <summary>
    /// Robust centered scale: s = max(1.4826 * median|x - mu|, floor). The floor exists so a
    /// degenerate window cannot divide a z-score by ~zero.
    /// </summary>
    public static (double Center, double Scale) RobustScale(IEnumerable<double> values, double scaleFloor)
    {
        var sorted = Sorted(values);
        var mu = Median(sorted) ?? 0d;
        var dev = Sorted(sorted.Select(v => Math.Abs(v - mu)));
        var mad = Median(dev) ?? 0d;
        return (mu, Math.Max(1.4826d * mad, scaleFloor));
    }

    public static double ZScore(double x, double center, double scale) => (x - center) / scale;
}
