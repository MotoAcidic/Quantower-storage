namespace AuctionResponse.Core;

/// <summary>Pressure-response plot coordinates (Section 8).</summary>
public readonly record struct PlotPoint(
    double X,
    double Y,
    double XClipped,
    double YClipped,
    bool OverflowX,
    bool OverflowY,
    long AtNs)
{
    public bool IsNeutral => Math.Abs(X) < PlotMath.NeutralBand && Math.Abs(Y) < PlotMath.NeutralBand;
}

public static class PlotMath
{
    public const double Clip = 3d;
    public const double NeutralBand = 0.25d;

    /// <summary>
    /// Sign-preserving scales s_D = max(Q0.75(|D|), 1), s_R = max(Q0.75(|R|), 1).
    /// Deliberately NOT a centered z-score: a negative centered score can still represent
    /// positive buying, which would make the quadrants lie.
    /// </summary>
    public static double Scale(double q75OfAbsolute) => Math.Max(q75OfAbsolute, 1d);

    /// <summary>
    /// x = D/s_D, y = R/s_R. Values are stored unmodified; clipping is for DRAWING only and
    /// is reported so the renderer can show an overflow arrow.
    /// </summary>
    public static PlotPoint Coordinates(double delta, double deltaScale, double response, double responseScale, long atNs)
    {
        if (deltaScale <= 0d) throw new ArgumentOutOfRangeException(nameof(deltaScale), "Plot scale must be positive.");
        if (responseScale <= 0d) throw new ArgumentOutOfRangeException(nameof(responseScale), "Plot scale must be positive.");

        var x = delta / deltaScale;
        var y = response / responseScale;
        var xc = Math.Clamp(x, -Clip, Clip);
        var yc = Math.Clamp(y, -Clip, Clip);
        return new PlotPoint(x, y, xc, yc, Math.Abs(x) > Clip, Math.Abs(y) > Clip, atNs);
    }

    /// <summary>
    /// Screen mapping inside a rectangle: X = X0 + w(x_clip + 3)/6, Y = Y0 + h(3 - y_clip)/6.
    /// Changing zoom or plot scale must never change a signal — this is presentation only.
    /// </summary>
    public static (double X, double Y) ToScreen(in PlotPoint p, double x0, double y0, double w, double h)
        => (x0 + w * (p.XClipped + Clip) / (2 * Clip), y0 + h * (Clip - p.YClipped) / (2 * Clip));

    /// <summary>
    /// Optional circular rendering (Section 15). Pi enters only through radians; the angle
    /// and radius are not probabilities and not market-cycle predictors.
    /// </summary>
    public static (double R, double Theta, double Dx, double Dy) ToDisk(in PlotPoint p)
    {
        var rho = Math.Sqrt(p.X * p.X + p.Y * p.Y);
        var theta = Math.Atan2(p.Y, p.X);
        var r = Math.Tanh(rho);
        if (rho == 0d) return (0d, 0d, 0d, 0d);
        return (r, theta, r * Math.Cos(theta), r * Math.Sin(theta));
    }
}

/// <summary>
/// Independent descriptive label (Section 12). It describes what the two plot coordinates
/// currently look like. It never delays confirmation and never causes a health fault.
/// </summary>
public enum DescriptiveLabel { Unavailable, Neutral, BuyingAdvances, SellingAdvances, Mixed }

/// <summary>
/// Applies the 5-second dwell before a displayed label changes: the same label must be
/// proposed at every decision tick for the dwell period. Missing data resets the timer and
/// shows Unavailable immediately.
/// </summary>
public sealed class DescriptiveLabelTracker
{
    private readonly long _dwellNs;
    private DescriptiveLabel _proposed = DescriptiveLabel.Unavailable;
    private long _proposedSinceNs;

    public DescriptiveLabelTracker(int dwellMs) { _dwellNs = (long)dwellMs * 1_000_000L; }

    public DescriptiveLabel Displayed { get; private set; } = DescriptiveLabel.Unavailable;

    public static DescriptiveLabel Propose(Measure delta, Measure response, in PlotPoint? point)
    {
        if (!delta.IsAvailable || !response.IsAvailable || point is null) return DescriptiveLabel.Unavailable;
        if (point.Value.IsNeutral) return DescriptiveLabel.Neutral;
        var d = delta.Value!.Value;
        var r = response.Value!.Value;
        if (d > 0 && r > 0) return DescriptiveLabel.BuyingAdvances;
        if (d < 0 && r < 0) return DescriptiveLabel.SellingAdvances;
        return DescriptiveLabel.Mixed;
    }

    public DescriptiveLabel Update(DescriptiveLabel proposed, long nowNs)
    {
        if (proposed == DescriptiveLabel.Unavailable)
        {
            Displayed = DescriptiveLabel.Unavailable;
            _proposed = DescriptiveLabel.Unavailable;
            _proposedSinceNs = nowNs;
            return Displayed;
        }

        if (proposed != _proposed) { _proposed = proposed; _proposedSinceNs = nowNs; }

        // Every change needs the full dwell, including the first one out of Unavailable.
        if (proposed != Displayed && nowNs - _proposedSinceNs >= _dwellNs)
            Displayed = proposed;

        return Displayed;
    }

    public void Reset()
    {
        Displayed = DescriptiveLabel.Unavailable;
        _proposed = DescriptiveLabel.Unavailable;
        _proposedSinceNs = 0;
    }
}
