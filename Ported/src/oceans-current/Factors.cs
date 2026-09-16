using System;

namespace OceansCurrent
{
    public enum FactorId
    {
        Vwap = 0,
        Cvd = 1,
        Value = 2,
        Inventory = 3,
        Structure = 4,
        OneTimeframing = 5
    }

    /// <summary>What the gamma regime is doing to the weights on this bar.</summary>
    public enum RegimeMode
    {
        /// <summary>No feed, a stale feed, or a mixed reading. Nothing is transformed.</summary>
        None = 0,

        /// <summary>Long gamma: dealer hedging suppresses range. Thrust evidence is discounted.</summary>
        Positive = 1,

        /// <summary>Short gamma: hedging amplifies. Thrust evidence is upweighted.</summary>
        Negative = -1
    }

    /// <summary>
    /// One factor's vote, in [-100, +100], positive long.
    ///
    /// <see cref="Available"/> is the load-bearing field. A factor that cannot be computed says
    /// so and drops out of the weight normalisation entirely; it never votes zero. Zero is a
    /// real reading -- "price is exactly on VWAP" -- and burying "I don't know" inside it would
    /// drag every blended score toward neutral for reasons no one could see on the chart.
    /// </summary>
    public struct Subscore
    {
        public decimal Value;
        public bool Available;

        /// <summary>Why it is absent, in the words the badge prints. Null when available.</summary>
        public string Absent;

        /// <summary>A flag worth showing next to the state, or null. e.g. DIVERGENCE.</summary>
        public string Note;

        public static Subscore At(decimal value) =>
            new Subscore { Value = Factors.Clamp(value, 100m), Available = true };

        public static Subscore Missing(string why) =>
            new Subscore { Available = false, Absent = why };

        public Subscore WithNote(string note) { Note = note; return this; }

        public int Sign => !Available ? 0 : Value > 0m ? 1 : Value < 0m ? -1 : 0;
    }

    /// <summary>Every threshold in the engine. All of them are indicator inputs; none are constants.</summary>
    public sealed class FactorConfig
    {
        public bool[] Enabled = { true, true, false, true, true, false };
        public decimal[] Weight = { 0.20m, 0.20m, 0.20m, 0.10m, 0.20m, 0.10m };

        // F1 VWAP
        public int VwapSlopeBars = 10;
        public decimal SlopeDisagreeMultiplier = 0.5m;
        public decimal FadeAboveSigma = 1.5m;
        public decimal FadeMultiplier = -0.5m;

        // F2 CVD
        public int ThrustBars = 20;
        public int ThrustNormWindow = 100;
        public int DivergenceBars = 20;
        public decimal DivergenceMultiplier = 0.5m;

        // F4 overnight inventory
        public decimal GapNormPoints = 40m;
        public decimal StretchedAbove = 0.8m;
        public decimal StretchedMultiplier = 0.6m;

        // F5 structure
        public int SweepWithinBars = 3;
        public int SweepDecayBars = 20;
        public decimal SweepPenalty = 50m;

        // F7 regime transforms
        public decimal PosGammaCvdMultiplier = 0.7m;
        public decimal PosGammaOtfMultiplier = 0.5m;
        public decimal NegGammaCvdMultiplier = 1.3m;
        public decimal NegGammaOtfMultiplier = 1.5m;
        public decimal WallZonePoints = 15m;
        public decimal WallDampMultiplier = 0.6m;
    }

    /// <summary>
    /// The four factors that ship in v1, each a pure function of numbers handed to it. Nothing
    /// here reads a chart, a clock or a file, which is why the harness can drive all of it.
    ///
    /// F3 (value migration) and F6 (30-minute one-timeframing) are declared and permanently
    /// absent until they are built: an unbuilt factor that returned 0 would vote, and a factor
    /// nobody wrote must not have an opinion.
    /// </summary>
    public static class Factors
    {
        public static decimal Clamp(decimal value, decimal magnitude) =>
            value > magnitude ? magnitude : value < -magnitude ? -magnitude : value;

        private static int Sign(decimal v) => v > 0m ? 1 : v < 0m ? -1 : 0;

        /// <summary>
        /// F1 -- where price sits against the anchored VWAP, measured in that session's own
        /// spread rather than in points, so a quiet morning and a trend day are read on the
        /// same scale. Two standard deviations saturates.
        /// </summary>
        public static Subscore Vwap(decimal close, Level vwap, Level sigma, Level slope,
                                    bool fadeExtremes, FactorConfig cfg)
        {
            if (!vwap.Known) return Subscore.Missing("no VWAP");
            if (!sigma.Known) return Subscore.Missing("no spread");

            var z = (close - vwap.Value) / sigma.Value;
            var sub = Clamp(z * 50m, 100m);

            // Price above a falling VWAP is a weaker long than price above a rising one.
            if (slope.Known && Sign(slope.Value) != 0 && Sign(slope.Value) != Sign(sub))
                sub *= cfg.SlopeDisagreeMultiplier;

            // In long gamma, dealer hedging leans against extension: an extreme reads as
            // stretched rather than strong, so the vote is halved AND turned around.
            if (fadeExtremes && Math.Abs(z) > cfg.FadeAboveSigma)
                sub *= cfg.FadeMultiplier;

            return Subscore.At(sub);
        }

        /// <summary>
        /// F2 -- how hard cumulative delta has pushed over the last K bars, priced against how
        /// hard it usually pushes. Halved and flagged where price makes a new extreme that
        /// delta will not confirm; that flag is context for the orderflow read, not a signal.
        /// </summary>
        public static Subscore Cvd(decimal cvd, bool hasBack, decimal cvdBack, Level norm,
                                   bool priceMadeHigh, bool priceMadeLow,
                                   bool cvdMadeHigh, bool cvdMadeLow, FactorConfig cfg)
        {
            if (!hasBack) return Subscore.Missing("session too young");

            // No yardstick, no reading. A default yardstick here would scale every thrust by a
            // number nobody measured, and the state machine would act on it.
            if (!norm.Known) return Subscore.Missing("no thrust norm");

            var thrust = cvd - cvdBack;
            var sub = Clamp(100m * thrust / (2m * norm.Value), 100m);

            var divergence = (priceMadeHigh && !cvdMadeHigh) || (priceMadeLow && !cvdMadeLow);
            if (divergence) sub *= cfg.DivergenceMultiplier;

            var score = Subscore.At(sub);
            return divergence ? score.WithNote("DIVERGENCE") : score;
        }

        /// <summary>
        /// F4 -- what the overnight left behind: where it closed inside its own range, and
        /// which way it gapped the cash open. Frozen at the open and constant for the session.
        /// </summary>
        public static Subscore Inventory(Level onHigh, Level onLow, Level onClose,
                                         Level rthOpen, Level priorRthClose, FactorConfig cfg)
        {
            if (!onHigh.Known || !onLow.Known || !onClose.Known)
                return Subscore.Missing("no overnight");

            var range = onHigh.Value - onLow.Value;
            if (range <= 0m) return Subscore.Missing("no overnight range");

            var mid = (onHigh.Value + onLow.Value) / 2m;
            var position = Clamp((onClose.Value - mid) / (range / 2m), 1m);

            var sub = 50m * position;

            var gapKnown = rthOpen.Known && priorRthClose.Known;
            var gap = gapKnown ? rthOpen.Value - priorRthClose.Value : 0m;

            // The gap needs a scale to be read as small or large, and a zero scale has none.
            if (gapKnown && cfg.GapNormPoints > 0m)
            {
                var size = Math.Abs(gap) / cfg.GapNormPoints;
                if (size > 1m) size = 1m;
                sub += 50m * Sign(gap) * size;
            }

            // Inventory one-sided AND gapped the same way is a stretched open, which is where
            // the fade happens. The vote survives; it just stops being worth a full weight.
            var stretched = Math.Abs(position) > cfg.StretchedAbove
                            && gapKnown && Sign(gap) != 0 && Sign(gap) == Sign(position);

            if (stretched) sub *= cfg.StretchedMultiplier;

            var score = Subscore.At(sub);
            return stretched ? score.WithNote("INV STRETCHED") : score;
        }

        /// <summary>
        /// F5 -- the ladder: price above or below each carried-over reference, averaged over the
        /// references that actually exist, plus whatever decaying sweep-and-reclaim penalty is
        /// still live. Four known references is the same +/-25 apiece the spec asks for; fewer
        /// scales up rather than quietly voting the missing ones as bearish.
        /// </summary>
        public static Subscore Structure(decimal close, Level[] references, decimal sweepBias)
        {
            var net = 0;
            var count = 0;

            for (var i = 0; i < references.Length; i++)
            {
                if (!references[i].Known) continue;

                count++;
                net += close > references[i].Value ? 1 : -1;
            }

            if (count == 0) return Subscore.Missing("no references");

            var ladder = 100m * net / count;
            var score = Subscore.At(ladder + sweepBias);

            return sweepBias != 0m ? score.WithNote("SWEEP") : score;
        }

        /// <summary>F3. Not built. Says so rather than voting.</summary>
        public static Subscore Value() => Subscore.Missing("not built (v1.1)");

        /// <summary>F6. Not built. Says so rather than voting.</summary>
        public static Subscore OneTimeframing() => Subscore.Missing("not built (v1.1)");
    }

    /// <summary>
    /// Turning the factor votes into one number.
    ///
    /// This lives outside the engine so the harness can drive it with subscores it chose, rather
    /// than with subscores it had to rig a price series to produce. The rule it exists to
    /// protect is one line long and is the difference between a blend that means something and
    /// one that quietly drifts neutral: a factor that is disabled, unavailable or weightless is
    /// OUT of the average, not averaged in as a zero.
    /// </summary>
    public static class Scoring
    {
        /// <summary>The regime-adjusted weight of one factor, or zero where it must not vote.</summary>
        public static decimal WeightOf(int i, Subscore sub, FactorConfig cfg, RegimeMode regime)
        {
            if (i < 0 || i >= cfg.Enabled.Length) return 0m;
            if (!cfg.Enabled[i] || !sub.Available) return 0m;

            var w = cfg.Weight[i];
            if (w <= 0m) return 0m;

            if (regime == RegimeMode.Positive)
            {
                if (i == (int)FactorId.Cvd) w *= cfg.PosGammaCvdMultiplier;
                if (i == (int)FactorId.OneTimeframing) w *= cfg.PosGammaOtfMultiplier;
            }
            else if (regime == RegimeMode.Negative)
            {
                if (i == (int)FactorId.Cvd) w *= cfg.NegGammaCvdMultiplier;
                if (i == (int)FactorId.OneTimeframing) w *= cfg.NegGammaOtfMultiplier;
            }

            return w > 0m ? w : 0m;
        }

        /// <summary>
        /// The weighted average over the factors that may vote. False when none may -- which is
        /// a different thing from a score of zero, and the state machine treats it as such.
        /// </summary>
        public static bool Blend(Subscore[] subs, FactorConfig cfg, RegimeMode regime,
                                 bool inWallZone, out decimal score)
        {
            var weighted = 0m;
            var total = 0m;

            for (var i = 0; i < subs.Length; i++)
            {
                var w = WeightOf(i, subs[i], cfg, regime);
                if (w <= 0m) continue;

                weighted += w * subs[i].Value;
                total += w;
            }

            if (total <= 0m) { score = 0m; return false; }

            score = weighted / total;

            // Pinned against a wall the tape chops, so the whole reading is worth less. This
            // scales the answer; it never changes its sign.
            if (inWallZone) score *= cfg.WallDampMultiplier;

            return true;
        }
    }

    /// <summary>
    /// Watches each carried-over reference for the shape that means the opposite of a break: a
    /// bar trades through it and price is back on the original side within a few bars. The
    /// penalty is applied in the direction of the failed break and decays to nothing.
    ///
    /// Committed bars only, fed strictly in order. The provisional read on the live bar reuses
    /// whatever penalty is already standing rather than advancing this, because a sweep that
    /// un-happens on the next tick is not a sweep.
    /// </summary>
    public sealed class StructureTracker
    {
        private struct Watch
        {
            public decimal Reference;
            public bool Known;
            public int Side;            // where the last close sat: +1 above, -1 below
            public bool Pending;
            public int PendingBar;
            public int PendingDirection; // +1 broke up, -1 broke down
            public bool Fired;
            public int FiredBar;
            public int FiredDirection;
        }

        private Watch[] _watches;
        private readonly FactorConfig _cfg;

        public StructureTracker(FactorConfig cfg, int referenceCount)
        {
            _cfg = cfg;
            _watches = new Watch[referenceCount];
        }

        /// <summary>
        /// Absorb one closed bar against the current reference values. A reference that moves
        /// (the day rolled) drops everything pending on it: a break of yesterday's high is not
        /// a break of today's.
        /// </summary>
        public StructureTracker CloneForWhatIf()
        {
            var c = (StructureTracker)MemberwiseClone();
            c._watches = (Watch[])_watches.Clone();
            return c;
        }

        public void Absorb(int bar, Level[] references, decimal high, decimal low, decimal close) =>
            Step(_watches, _cfg, bar, references, high, low, close);

        /// <summary>
        /// What <see cref="BiasAt"/> would read at <paramref name="bar"/> if that bar closed as
        /// given -- the same step run on a copy, so nothing here is written. A close that
        /// completes a pending sweep fires its penalty here exactly as it would on commit, which
        /// is what lets a trigger level near a freshly broken reference be the true one.
        /// </summary>
        public decimal BiasIf(int bar, Level[] references, decimal high, decimal low, decimal close)
        {
            var copy = (Watch[])_watches.Clone();
            Step(copy, _cfg, bar, references, high, low, close);
            return Sum(copy, _cfg, bar);
        }

        private static void Step(Watch[] watches, FactorConfig cfg, int bar, Level[] references,
                                 decimal high, decimal low, decimal close)
        {
            for (var i = 0; i < watches.Length && i < references.Length; i++)
            {
                var w = watches[i];
                var reference = references[i];

                if (!reference.Known)
                {
                    watches[i] = new Watch();
                    continue;
                }

                if (!w.Known || w.Reference != reference.Value)
                {
                    w = new Watch { Reference = reference.Value, Known = true };
                    w.Side = close > reference.Value ? 1 : -1;
                    watches[i] = w;
                    continue;
                }

                var value = reference.Value;

                if (!w.Pending)
                {
                    // A break only counts as one if price was on the other side to begin with.
                    if (w.Side < 0 && high > value)
                    {
                        w.Pending = true;
                        w.PendingBar = bar;
                        w.PendingDirection = 1;
                    }
                    else if (w.Side > 0 && low < value)
                    {
                        w.Pending = true;
                        w.PendingBar = bar;
                        w.PendingDirection = -1;
                    }
                }

                if (w.Pending)
                {
                    // The window counts the breaking bar as its first: with the default of 3,
                    // price has the break bar and the two after it to get back inside.
                    var elapsed = bar - w.PendingBar;
                    var window = Math.Max(1, cfg.SweepWithinBars);
                    var backInside = w.PendingDirection > 0 ? close < value : close > value;

                    if (backInside && elapsed < window)
                    {
                        w.Fired = true;
                        w.FiredBar = bar;
                        w.FiredDirection = w.PendingDirection;
                        w.Pending = false;
                    }
                    else if (elapsed >= window - 1)
                    {
                        // Held past the window: an ordinary break, and nothing to say about it.
                        w.Pending = false;
                    }
                }

                w.Side = close > value ? 1 : -1;
                watches[i] = w;
            }
        }

        /// <summary>
        /// The standing penalty at this bar, in subscore points. A failed break upward is a
        /// trap, so it reads bearish, and it fades linearly to nothing over the decay window.
        /// </summary>
        public decimal BiasAt(int bar) => Sum(_watches, _cfg, bar);

        private static decimal Sum(Watch[] watches, FactorConfig cfg, int bar)
        {
            if (cfg.SweepDecayBars <= 0) return 0m;

            var total = 0m;

            for (var i = 0; i < watches.Length; i++)
            {
                var w = watches[i];
                if (!w.Fired) continue;

                var age = bar - w.FiredBar;
                if (age < 0 || age >= cfg.SweepDecayBars) continue;

                var decay = 1m - (decimal)age / cfg.SweepDecayBars;
                total += -w.FiredDirection * cfg.SweepPenalty * decay;
            }

            return total;
        }
    }
}
