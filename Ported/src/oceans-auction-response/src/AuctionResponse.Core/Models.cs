namespace AuctionResponse.Core;

/// <summary>
/// Identity of a trained model (Section 17). Every field is part of the compatibility
/// contract: any mismatch means the baseline rule is retained and the UI shows
/// ModelUnavailable. Placeholder coefficients are never inserted.
/// </summary>
public sealed record ModelManifest
{
    public string ModelKind { get; init; } = "";
    public string ModelVersion { get; init; } = "";
    public string FeatureFormulaVersion { get; init; } = Versioning.FeatureFormulaVersion;
    public IReadOnlyList<string> FeatureNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<double> Centers { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> Scales { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> ScaleFloors { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> Coefficients { get; init; } = Array.Empty<double>();
    public double Intercept { get; init; }
    public string RegularizationConvention { get; init; } = "";
    public double Lambda { get; init; }
    public double? CalibrationTemperature { get; init; }
    public double ResidualCenter { get; init; }
    public double ResidualScale { get; init; }
    public string TrainingInterval { get; init; } = "";
    public string ValidationInterval { get; init; } = "";
    public string CalibrationInterval { get; init; } = "";
    public string TestInterval { get; init; } = "";
    public InstrumentKey Instrument { get; init; } = InstrumentKey.Unknown;
    public string ClockMode { get; init; } = "";
    public string FeedMode { get; init; } = "";
    public int HorizonMs { get; init; }
    public IReadOnlyList<string> ClassNames { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, double> QualityMetrics { get; init; } = new Dictionary<string, double>();
    public string Sha256 { get; init; } = "";
    /// <summary>Deterministic prediction fixture: inputs and the output they must reproduce.</summary>
    public IReadOnlyList<double> FixtureInput { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> FixtureExpectedOutput { get; init; } = Array.Empty<double>();

    /// <summary>
    /// Contract check against the running configuration. Returns the reason on mismatch.
    /// </summary>
    public string? IncompatibleReason(InstrumentKey instrument, string clockMode, string feedMode, int horizonMs)
    {
        if (FeatureFormulaVersion != Versioning.FeatureFormulaVersion)
            return "feature formula version " + FeatureFormulaVersion + " != " + Versioning.FeatureFormulaVersion;
        if (!Instrument.Equals(instrument)) return "model trained on " + Instrument + ", running " + instrument;
        if (ClockMode != clockMode) return "clock mode mismatch";
        if (FeedMode != feedMode) return "feed mode mismatch";
        if (HorizonMs != 0 && HorizonMs != horizonMs) return "horizon mismatch";
        if (Coefficients.Count != FeatureNames.Count) return "coefficient/feature count mismatch";
        return null;
    }
}

/// <summary>Section 9 residual evidence. Off by default; never a forecast on its own.</summary>
public static class ResponseResidual
{
    public const double ScaleFloorTicks = 0.5d;

    /// <summary>
    /// epsilon = R - Rhat, z = (epsilon - mu_val) / s_val, resistance evidence = -o * z.
    /// Centre and scale come from FORWARD VALIDATION predictions, never in-sample residuals.
    /// </summary>
    public static (double Residual, double ZResidual, double ResistanceEvidence) Evaluate(
        double actualResponse, double predictedResponse, double validationCenter, double validationScale, int orientation)
    {
        var scale = Math.Max(validationScale, ScaleFloorTicks);
        var eps = actualResponse - predictedResponse;
        var z = (eps - validationCenter) / scale;
        return (eps, z, -orientation * z);
    }

    /// <summary>
    /// The experimental enhanced filter: -o*z at least 1.5 AND o*Rhat positive. A negative
    /// residual during buying, by itself, is not confirmation.
    /// </summary>
    public static bool EnhancedFilterPasses(double resistanceEvidence, double predictedResponse, int orientation, double threshold)
        => resistanceEvidence >= threshold && orientation * predictedResponse > 0d;
}

/// <summary>
/// Discrete competing-risk survival aggregation (Section 15). Interface-level only:
/// the extension needs its own specification and evaluation before it is trained.
/// </summary>
public static class CompetingRisks
{
    /// <summary>
    /// S_j = S_(j-1)(1 - hB_j - hR_j); F_B(J) = sum S_(j-1) hB_j; F_R(J) = sum S_(j-1) hR_j.
    /// F_B + F_R + S_J = 1 by construction.
    /// </summary>
    public static (double Break, double Reject, double Survival) Accumulate(
        IReadOnlyList<double> breakHazards, IReadOnlyList<double> rejectHazards)
    {
        if (breakHazards.Count != rejectHazards.Count)
            throw new ArgumentException("Hazard series must cover the same intervals.");

        double s = 1d, fb = 0d, fr = 0d;
        for (var j = 0; j < breakHazards.Count; j++)
        {
            var hb = breakHazards[j];
            var hr = rejectHazards[j];
            if (hb < 0d || hr < 0d || hb + hr > 1d)
                throw new ArgumentOutOfRangeException(nameof(breakHazards), "Hazards must be non-negative and sum to at most one.");
            fb += s * hb;
            fr += s * hr;
            s *= 1d - hb - hr;
        }
        return (fb, fr, s);
    }
}

/// <summary>
/// Multinomial scoring and proper scoring rules (Section 14). Present so the research
/// project can evaluate a model; NOTHING here is displayed until a validated, compatible
/// model is loaded.
/// </summary>
public static class Multinomial
{
    /// <summary>Numerically stable softmax: p_k = exp(eta_k - c) / sum exp(eta_j - c), c = max eta.</summary>
    public static double[] Softmax(IReadOnlyList<double> eta)
    {
        var c = double.NegativeInfinity;
        for (var i = 0; i < eta.Count; i++) if (eta[i] > c) c = eta[i];

        var p = new double[eta.Count];
        var sum = 0d;
        for (var i = 0; i < eta.Count; i++) { p[i] = Math.Exp(eta[i] - c); sum += p[i]; }
        for (var i = 0; i < p.Length; i++) p[i] /= sum;
        return p;
    }

    /// <summary>Temperature-scaled probabilities. T is fitted on a separate calibration block.</summary>
    public static double[] Calibrated(IReadOnlyList<double> eta, double temperature)
    {
        if (temperature <= 0d) throw new ArgumentOutOfRangeException(nameof(temperature), "Calibration temperature must be positive.");
        var scaled = new double[eta.Count];
        for (var i = 0; i < eta.Count; i++) scaled[i] = eta[i] / temperature;
        return Softmax(scaled);
    }

    /// <summary>
    /// Unnormalized multiclass Brier score, range 0 to 2 for three classes.
    /// Report alongside log loss, reliability bins, class counts and the class-frequency baseline.
    /// </summary>
    public static double BrierScore(IReadOnlyList<double[]> predictions, IReadOnlyList<int> outcomes)
    {
        if (predictions.Count != outcomes.Count) throw new ArgumentException("Predictions and outcomes must align.");
        if (predictions.Count == 0) throw new ArgumentException("Brier score needs at least one observation.");

        var total = 0d;
        for (var i = 0; i < predictions.Count; i++)
        {
            var p = predictions[i];
            for (var k = 0; k < p.Length; k++)
            {
                var indicator = outcomes[i] == k ? 1d : 0d;
                var diff = p[k] - indicator;
                total += diff * diff;
            }
        }
        return total / predictions.Count;
    }

    public static double LogLoss(IReadOnlyList<double[]> predictions, IReadOnlyList<int> outcomes)
    {
        var total = 0d;
        for (var i = 0; i < predictions.Count; i++)
            total -= Math.Log(Math.Max(predictions[i][outcomes[i]], 1e-15));
        return total / predictions.Count;
    }
}

/// <summary>First-passage labels for the optional probability module (Section 14).</summary>
public enum FirstPassageLabel
{
    /// <summary>Failure boundary touched first, within the horizon.</summary>
    Break,
    /// <summary>Confirmation boundary touched first, within the horizon.</summary>
    Reject,
    /// <summary>Neither boundary touched within the horizon.</summary>
    Timeout,
    /// <summary>Future data is missing. NOT a timeout.</summary>
    Censored,
    /// <summary>Observations too coarse to establish which boundary came first.</summary>
    Ambiguous
}
