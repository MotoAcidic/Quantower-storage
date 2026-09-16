using System;

namespace OceansCurrent
{
    public enum TriggerKind
    {
        Here = 8,
        CloseBelow = 0,
        CloseAbove = 1,
        HoldBelow = 2,
        HoldAbove = 3,
        SellDelta = 4,
        BuyDelta = 5,
        SweepHigh = 6,
        SweepLow = 7
    }

    /// <summary>One thing that would change the state, and what it would change it to.</summary>
    public struct Trigger
    {
        public bool Found;
        public TriggerKind Kind;

        /// <summary>The close (or the swept reference) that does it. Rounded to the tick.</summary>
        public decimal Price;

        /// <summary>For the delta triggers: net contracts on one bar.</summary>
        public decimal Delta;

        public BiasState To;

        /// <summary>For sweeps: PDH / PDL / ONH / ONL.</summary>
        public string Reference;
    }

    /// <summary>Everything that would change the current state, worked out once per closed bar.</summary>
    public sealed class TriggerReport
    {
        public BiasState State;

        /// <summary>Committed bars before any change is possible. The levels hold regardless.</summary>
        public int DwellLeft;

        public decimal Close;

        /// <summary>Why there is no report, or null. e.g. no score yet.</summary>
        public string Problem;

        /// <summary>
        /// Set when a close right where price is now already changes the state -- the score is
        /// past the threshold and only the dwell is holding it. Then the side levels are not
        /// "the nearest change", this is, and the panel says so instead of printing a level a
        /// tick either side of price.
        /// </summary>
        public Trigger Here;

        /// <summary>The nearest next-bar close on each side that changes the state.</summary>
        public Trigger Below;
        public Trigger Above;

        /// <summary>
        /// Where the settled score -- price simply staying there for a few bars -- crosses into
        /// the opposite state. From LONG that is the outright reversal to SHORT, through neutral.
        /// </summary>
        public Trigger HoldBelow;
        public Trigger HoldAbove;

        /// <summary>One bar of net delta, at today's price, that changes the state. Absent when delta is not voting.</summary>
        public Trigger SellDelta;
        public Trigger BuyDelta;
        public bool DeltaVoting;

        /// <summary>The nearest reference above and below, swept and reclaimed.</summary>
        public Trigger SweepHigh;
        public Trigger SweepLow;

        public Level GammaFlip;
        public RegimeMode Regime;

        /// <summary>How far each side was searched, in points.</summary>
        public decimal Range;

        public static TriggerReport Failed(string why) => new TriggerReport { Problem = why };
    }

    /// <summary>
    /// "What would change its mind" -- asked of the engine itself, not of a second model.
    ///
    /// Every number here comes from <see cref="BiasEngine.ScoreIf"/>, which runs the same factor
    /// calls as a real commit and writes nothing, fed through the same transition rule the state
    /// machine uses. So a level on the panel is the level the engine will actually act on --
    /// including the steps where price crosses PDH or ONL, and the regime changing side as price
    /// crosses the gamma flip.
    ///
    /// What it holds fixed, and says so: the price levels assume delta stays where it is, and the
    /// delta triggers assume price stays where it is. In a real move both change together, so a
    /// sell-off with heavy selling reaches the SHORT level sooner than the printed price.
    /// </summary>
    public static class TriggerScan
    {
        /// <summary>Search each side out to this fraction of price.</summary>
        public const decimal RangeFraction = 0.02m;

        /// <summary>Delta is searched in steps of this many contracts, up to the cap.</summary>
        public const decimal DeltaStep = 50m;
        public const decimal DeltaCap = 15000m;

        public static TriggerReport Run(BiasEngine engine, int bar, IBarWindow bars, TimeContext time,
                                        GexSnapshot gex, bool gexUsable, decimal tick)
        {
            if (engine == null || !engine.Any) return TriggerReport.Failed("warming up");
            if (tick <= 0m) return TriggerReport.Failed("no tick size");

            // The hypothetical bar is the next one the engine will commit, whatever the chart's
            // live index happens to be.
            bar = engine.NextBar;
            if (bar <= 0 || bar - 1 >= bars.Count) return TriggerReport.Failed("no live bar");
            var time0 = bar < bars.Count ? bars.Time(bar) : bars.Time(bar - 1);

            var last = engine.Last;
            var close = last.Close;
            if (close <= 0m) return TriggerReport.Failed("no price");

            var volume = Math.Max(bars.Volume(bar - 1), 1m);

            var report = new TriggerReport
            {
                State = engine.State,
                DwellLeft = engine.DwellLeft,
                Close = close,
                DeltaVoting = engine.CvdVoting,
                GammaFlip = gex != null && gexUsable ? gex.GammaFlip : Level.None,
                Regime = last.Regime
            };

            var current = engine.State;

            // Commit one hypothetical bar on a throwaway copy of the engine, through the real
            // Advance. The gamma snapshot is re-read at that bar's own close, as the indicator
            // does for a real bar.
            Func<decimal, decimal, decimal, decimal, decimal, BiasRecord> commit = (hi, lo, c, delta, vol) =>
            {
                var g = gex != null ? gex.AtPrice(c) : null;
                var usable = gexUsable && g != null && g.Usable;
                return engine.WhatIf(new OneBar(bars, bar, time0, c, hi, lo, c, vol, delta), time, g, usable);
            };

            // What the next committed bar does if it closes at p: the machine's own rule with the
            // dwell set aside (it is reported separately), fed the committed score.
            Func<decimal, BiasState?> nextAt = p =>
            {
                var r = commit(p, p, p, 0m, volume);
                return r.ScoreKnown ? engine.PeekIgnoringDwell(true, r.Score) : current;
            };

            if (!commit(close, close, close, 0m, volume).ScoreKnown)
                return TriggerReport.Failed("no factor can vote");

            var here = nextAt(close);
            if (here.HasValue && here.Value != current)
                report.Here = new Trigger { Found = true, Kind = TriggerKind.Here, Price = close, To = here.Value };

            var range = Round(close * RangeFraction, tick);
            var step = Math.Max(tick, Round(CoarseStepPoints, tick));
            report.Range = range;

            report.Below = Scan(nextAt, close, -1, tick, step, range, current, TriggerKind.CloseBelow);
            report.Above = Scan(nextAt, close, +1, tick, step, range, current, TriggerKind.CloseAbove);

            // Settled: the raw blend, which the smoothed score converges to if price stays there,
            // judged from neutral -- where it goes to the opposite state outright.
            Func<decimal, BiasState?> settledAt = p =>
            {
                var r = commit(p, p, p, 0m, volume);
                return r.ScoreKnown ? engine.FromNeutral(r.Raw) : (BiasState?)null;
            };

            if (current != BiasState.Short)
                report.HoldBelow = ScanFor(settledAt, close, -1, tick, step, range, BiasState.Short, TriggerKind.HoldBelow);
            if (current != BiasState.Long)
                report.HoldAbove = ScanFor(settledAt, close, +1, tick, step, range, BiasState.Long, TriggerKind.HoldAbove);

            if (report.DeltaVoting)
            {
                Func<decimal, BiasState?> deltaAt = d =>
                {
                    var r = commit(close, close, close, d, Math.Max(volume, Math.Abs(d)));
                    return r.ScoreKnown ? engine.PeekIgnoringDwell(true, r.Score) : current;
                };

                report.SellDelta = ScanDelta(deltaAt, -1, current, TriggerKind.SellDelta);
                report.BuyDelta = ScanDelta(deltaAt, +1, current, TriggerKind.BuyDelta);
            }

            var refs = engine.References();
            report.SweepHigh = Sweep(engine, commit, refs, close, +1, tick, volume, range);
            report.SweepLow = Sweep(engine, commit, refs, close, -1, tick, volume, range);

            return report;
        }

        /// <summary>Price is searched in steps of this many points, then refined to the tick.</summary>
        public const decimal CoarseStepPoints = 2m;

        /// <summary>
        /// The real bars up to the one being imagined, and that one bar exactly as given. Reads
        /// past it are refused -- the engine never makes one, and a what-if must not be the first.
        /// </summary>
        private sealed class OneBar : IBarWindow
        {
            private readonly IBarWindow _real;
            private readonly int _bar;
            private readonly DateTime _time;
            private readonly decimal _o, _h, _l, _c, _v, _d;

            public OneBar(IBarWindow real, int bar, DateTime time, decimal open, decimal high,
                          decimal low, decimal close, decimal volume, decimal delta)
            {
                _real = real;
                _bar = bar;
                _time = time;
                _o = open; _h = Math.Max(high, Math.Max(open, close)); _l = Math.Min(low, Math.Min(open, close));
                _c = close; _v = volume; _d = delta;
            }

            public int Count => _bar + 1;

            public DateTime Time(int bar) => bar == _bar ? _time : Guard(bar).Time(bar);
            public decimal Open(int bar) => bar == _bar ? _o : Guard(bar).Open(bar);
            public decimal High(int bar) => bar == _bar ? _h : Guard(bar).High(bar);
            public decimal Low(int bar) => bar == _bar ? _l : Guard(bar).Low(bar);
            public decimal Close(int bar) => bar == _bar ? _c : Guard(bar).Close(bar);
            public decimal Volume(int bar) => bar == _bar ? _v : Guard(bar).Volume(bar);
            public decimal Delta(int bar) => bar == _bar ? _d : Guard(bar).Delta(bar);

            private IBarWindow Guard(int bar)
            {
                if (bar > _bar) throw new InvalidOperationException("what-if read past its own bar");
                return _real;
            }
        }

        /// <summary>
        /// Walks out from <paramref name="from"/> in coarse steps to the first price whose
        /// answer differs from <paramref name="current"/>, then back in by single ticks to the
        /// first one that does. Nearest crossing wins: the score is not monotonic in price (the
        /// ladder steps at each reference, and in long gamma VWAP fades an extreme), and the
        /// nearest level is the one a trader is going to meet first.
        /// </summary>
        public static Trigger Scan(Func<decimal, BiasState?> stateAt, decimal from, int dir,
                                   decimal tick, decimal step, decimal range, BiasState current,
                                   TriggerKind kind)
        {
            return ScanWhere(stateAt, from, dir, tick, step, range, s => s != current, kind);
        }

        /// <summary>As <see cref="Scan"/>, for the first price that reaches one particular state.</summary>
        public static Trigger ScanFor(Func<decimal, BiasState?> stateAt, decimal from, int dir,
                                      decimal tick, decimal step, decimal range, BiasState target,
                                      TriggerKind kind)
        {
            return ScanWhere(stateAt, from, dir, tick, step, range, s => s == target, kind);
        }

        private static Trigger ScanWhere(Func<decimal, BiasState?> stateAt, decimal from, int dir,
                                         decimal tick, decimal step, decimal range,
                                         Func<BiasState, bool> hit, TriggerKind kind)
        {
            var prev = from;

            for (var dist = step; dist <= range; dist += step)
            {
                var p = from + dir * dist;
                var s = stateAt(p);
                if (!s.HasValue || !hit(s.Value)) { prev = p; continue; }

                // Refine: the first tick after prev that hits.
                for (var q = prev + dir * tick; dir < 0 ? q >= p : q <= p; q += dir * tick)
                {
                    var r = stateAt(q);
                    if (r.HasValue && hit(r.Value))
                        return new Trigger { Found = true, Kind = kind, Price = q, To = r.Value };
                }

                return new Trigger { Found = true, Kind = kind, Price = p, To = s.Value };
            }

            return new Trigger { Found = false, Kind = kind };
        }

        public static Trigger ScanDelta(Func<decimal, BiasState?> stateAt, int dir, BiasState current,
                                        TriggerKind kind)
        {
            for (var d = DeltaStep; d <= DeltaCap; d += DeltaStep)
            {
                var s = stateAt(dir * d);
                if (s.HasValue && s.Value != current)
                    return new Trigger { Found = true, Kind = kind, Delta = dir * d, To = s.Value };
            }

            return new Trigger { Found = false, Kind = kind };
        }

        /// <summary>
        /// The nearest reference on one side, traded through and closed back on the near side on
        /// the same bar -- the stop run the ladder scores against the break. The bar is committed
        /// for real on a copy, so the tracker fires the sweep exactly as it would. Reported whether
        /// or not it changes anything: "a sweep of ONH does nothing" is worth knowing too.
        /// </summary>
        private static Trigger Sweep(BiasEngine engine, Func<decimal, decimal, decimal, decimal, decimal, BiasRecord> commit,
                                     Level[] refs, decimal close, int side, decimal tick, decimal volume, decimal range)
        {
            var kind = side > 0 ? TriggerKind.SweepHigh : TriggerKind.SweepLow;
            var best = -1;

            for (var i = 0; i < refs.Length; i++)
            {
                if (!refs[i].Known) continue;
                var v = refs[i].Value;
                if (side > 0 ? v <= close : v >= close) continue;
                if (Math.Abs(v - close) > range) continue;
                if (best < 0 || Math.Abs(v - close) < Math.Abs(refs[best].Value - close)) best = i;
            }

            if (best < 0) return new Trigger { Found = false, Kind = kind };

            var level = refs[best].Value;
            var through = level + side * tick;
            var reclaim = level - side * tick;

            var r = commit(side > 0 ? through : reclaim, side > 0 ? reclaim : through, reclaim, 0m, volume);

            return new Trigger
            {
                Found = true,
                Kind = kind,
                Price = level,
                Reference = BiasEngine.ReferenceNames[best],
                To = r.ScoreKnown ? engine.PeekIgnoringDwell(true, r.Score) : engine.State
            };
        }

        private static decimal Round(decimal v, decimal tick) =>
            tick > 0m ? Math.Round(v / tick, MidpointRounding.AwayFromZero) * tick : v;
    }
}
