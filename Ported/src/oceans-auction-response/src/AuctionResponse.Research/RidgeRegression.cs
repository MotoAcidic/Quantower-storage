using AuctionResponse.Core;

namespace AuctionResponse.Research;

/// <summary>
/// Ridge regression for the optional conditional response model (Section 9).
///
/// Solved by QR with Householder reflections, never by forming and inverting X'X: that
/// squares the condition number, and these features are correlated enough for it to matter.
///
/// Loss convention, recorded because library implementations differ:
///
///     argmin  (1/N) * sum (R_j - X_j b)^2  +  lambda * sum_{k=1..3} b_k^2
///
/// The intercept is NOT penalised.
/// </summary>
public static class RidgeRegression
{
    /// <summary>
    /// Fits coefficients for a design matrix whose first column is the intercept.
    /// Returns [intercept, b1, b2, b3].
    /// </summary>
    public static double[] Fit(double[][] design, double[] targets, double lambda)
    {
        if (design.Length != targets.Length)
            throw new ArgumentException("Design matrix and targets must have the same number of rows.");
        if (design.Length == 0)
            throw new ArgumentException("Ridge fit needs at least one observation.");

        var n = design.Length;
        var p = design[0].Length;

        // The penalty enters as extra rows sqrt(N*lambda)*I on the NON-intercept columns.
        // With the (1/N) loss scaling above, that factor is what makes the augmented
        // least-squares problem equal the penalised one.
        var penaltyRows = p - 1;
        var scale = Math.Sqrt(n * lambda);

        var rows = n + penaltyRows;
        var a = new double[rows][];
        var b = new double[rows];

        for (var i = 0; i < n; i++)
        {
            a[i] = (double[])design[i].Clone();
            b[i] = targets[i];
        }
        for (var k = 0; k < penaltyRows; k++)
        {
            var row = new double[p];
            row[k + 1] = scale;
            a[n + k] = row;
            b[n + k] = 0d;
        }

        return SolveLeastSquaresQr(a, b, p);
    }

    /// <summary>Householder QR least squares. Returns the p coefficients.</summary>
    private static double[] SolveLeastSquaresQr(double[][] a, double[] b, int p)
    {
        var m = a.Length;

        for (var k = 0; k < p; k++)
        {
            // Householder vector for column k.
            var norm = 0d;
            for (var i = k; i < m; i++) norm += a[i][k] * a[i][k];
            norm = Math.Sqrt(norm);
            if (norm < 1e-300) continue;

            if (a[k][k] > 0) norm = -norm;

            for (var i = k; i < m; i++) a[i][k] /= norm;
            a[k][k] -= 1d;

            for (var j = k + 1; j < p; j++)
            {
                var s = 0d;
                for (var i = k; i < m; i++) s += a[i][k] * a[i][j];
                s /= a[k][k];
                for (var i = k; i < m; i++) a[i][j] += s * a[i][k];
            }

            var sb = 0d;
            for (var i = k; i < m; i++) sb += a[i][k] * b[i];
            sb /= a[k][k];
            for (var i = k; i < m; i++) b[i] += sb * a[i][k];

            a[k][k] = norm;
        }

        // Back substitution on the upper triangle.
        var x = new double[p];
        for (var i = p - 1; i >= 0; i--)
        {
            var sum = b[i];
            for (var j = i + 1; j < p; j++) sum -= a[i][j] * x[j];
            x[i] = Math.Abs(a[i][i]) < 1e-300 ? 0d : sum / a[i][i];
        }
        return x;
    }

    /// <summary>
    /// Chronological development folds. Random folds would leak the future into the past,
    /// which on overlapping market windows is the classic way to invent an edge.
    /// </summary>
    public static double SelectLambda(
        double[][] design, double[] targets, IReadOnlyList<double> candidates, int folds = 3)
    {
        if (candidates.Count == 0) throw new ArgumentException("At least one lambda must be offered.");

        var n = design.Length;
        var best = candidates[0];
        var bestError = double.PositiveInfinity;

        foreach (var lambda in candidates)
        {
            var total = 0d;
            var evaluated = 0;

            for (var f = 1; f <= folds; f++)
            {
                var trainEnd = n * f / (folds + 1);
                var validEnd = n * (f + 1) / (folds + 1);
                if (trainEnd < design[0].Length + 1 || validEnd <= trainEnd) continue;

                var beta = Fit(design[..trainEnd], targets[..trainEnd], lambda);
                for (var i = trainEnd; i < validEnd; i++)
                {
                    var predicted = Predict(design[i], beta);
                    total += Math.Abs(targets[i] - predicted);
                    evaluated++;
                }
            }

            if (evaluated == 0) continue;
            var mae = total / evaluated;
            if (mae < bestError) { bestError = mae; best = lambda; }
        }

        return best;
    }

    public static double Predict(double[] row, double[] beta)
    {
        var sum = 0d;
        for (var i = 0; i < row.Length && i < beta.Length; i++) sum += row[i] * beta[i];
        return sum;
    }

    /// <summary>
    /// Activation gates from Section 9. These are ENGINEERING gates, not a statistical
    /// guarantee, and the model stays off until every one of them passes.
    /// </summary>
    public static IReadOnlyList<string> ActivationBlockers(
        int trainingWindows, int validationWindows, double validationMae, double frozenMeanBaselineMae, Config config)
    {
        var blockers = new List<string>();

        if (trainingWindows < config.OptionalModules.RegressionMinimumTrainWindows)
            blockers.Add("training windows " + trainingWindows + " below the minimum " + config.OptionalModules.RegressionMinimumTrainWindows);
        if (validationWindows < config.OptionalModules.RegressionMinimumValidationWindows)
            blockers.Add("forward validation windows " + validationWindows + " below the minimum " + config.OptionalModules.RegressionMinimumValidationWindows);
        if (!(validationMae < frozenMeanBaselineMae))
            blockers.Add("validation MAE " + validationMae.ToString("0.####") +
                         " does not beat the frozen mean-response baseline " + frozenMeanBaselineMae.ToString("0.####"));

        return blockers;
    }
}

/// <summary>
/// Hypothetical trade economics (Section 20). Offline only, and every input must come from
/// validated metadata: the contract multiplier, tick size and fees are never assumed from
/// the signal instrument.
/// </summary>
public static class TradeEconomics
{
    /// <summary>
    /// PnL_net = d * q * M * (P_exit - P_entry) - Fees - SlippageCost.
    ///
    /// If slippage is already inside the fill prices, SlippageCost MUST be zero or the cost
    /// is counted twice. Spread is already present in bid/ask fills.
    /// </summary>
    public static decimal NetPnL(
        int direction, int contracts, decimal dollarsPerPoint,
        decimal entryPrice, decimal exitPrice,
        decimal fees, decimal slippageCost, bool slippageAlreadyInFills)
    {
        if (direction is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(direction), "Direction is +1 long or -1 short.");
        if (contracts <= 0) throw new ArgumentOutOfRangeException(nameof(contracts));
        if (slippageAlreadyInFills && slippageCost != 0m)
            throw new ArgumentException("Slippage is already inside the fill prices; passing it again double-counts the cost.", nameof(slippageCost));

        return direction * contracts * dollarsPerPoint * (exitPrice - entryPrice) - fees - slippageCost;
    }
}
