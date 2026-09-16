using System;
using System.Collections.Generic;

namespace OceansAnchor
{
    /// <summary>Gate and clock settings. All indicator settings.</summary>
    public sealed class StateRules
    {
        /// <summary>Bars a Triggered zone has to resolve in before it expires.</summary>
        public int ClockBars = 3;

        public int BreakBufferTicks = 3;

        /// <summary>
        /// How close price must get to the shelf before the zone counts as tested. Shares its
        /// value with the tape path's zone buffer -- one tolerance, one number.
        /// </summary>
        public int ZoneBufferTicks = 8;

        public int OtfBars = 5;
        public bool OtfGuardOn = true;

        /// <summary>A+ window, Houston time. Outside it zones still render; alerts stay silent.</summary>
        public TimeSpan WindowStart = new TimeSpan(8, 30, 0);
        public TimeSpan WindowEnd = new TimeSpan(10, 30, 0);
        public bool AllowAllHours = false;

        /// <summary>The personal rule. On by default.</summary>
        public bool SuppressFriday = true;
    }

    /// <summary>One visit to a zone, kept so CVD can be compared across visits.</summary>
    public struct ZoneTouch
    {
        public int Bar;

        /// <summary>The extreme that tested the zone: the low for a long, the high for a short.</summary>
        public decimal Extreme;

        public decimal Cvd;
    }

    public static class SignalGate
    {
        /// <summary>
        /// Whether signals are allowed to fire at this moment.
        ///
        /// Zones keep rendering outside the window -- knowing where the level is at 13:00 is
        /// still worth something -- but nothing alerts. The window is not a filter on the
        /// market, it is a filter on the trader: the A+ setup at 14:15 is the one taken tired,
        /// after the morning already went badly.
        /// </summary>
        public static bool Allowed(DateTime local, StateRules r)
        {
            if (r == null) return false;
            if (r.SuppressFriday && local.DayOfWeek == DayOfWeek.Friday) return false;
            if (r.AllowAllHours) return true;

            return SessionScan.InWindow(local.TimeOfDay, r.WindowStart, r.WindowEnd);
        }

        /// <summary>
        /// One-timeframing: every one of the last N bars closed beyond the previous bar's
        /// extreme, in the same direction. Returns +1 for up, -1 for down, 0 for neither.
        ///
        /// Crude, and honest about it. What it encodes is the only rule in the playbook with no
        /// exceptions: never fade the freight train. A perfect HVN shelf in front of a market
        /// that has closed above the prior bar's high five times running is not a level, it is
        /// the next thing to get run over.
        /// </summary>
        public static int OneTimeframing(IList<BarFacts> bars, int n)
        {
            if (bars == null || n < 2 || bars.Count < n + 1) return 0;

            var first = bars.Count - n;

            var up = true;
            var down = true;

            for (var i = first; i < bars.Count; i++)
            {
                if (bars[i].Close <= bars[i - 1].High) up = false;
                if (bars[i].Close >= bars[i - 1].Low) down = false;
            }

            if (up) return 1;
            if (down) return -1;
            return 0;
        }

        /// <summary>Whether the guard blocks arming this side.</summary>
        public static bool BlockedByOtf(TestSide side, int otf, StateRules r)
        {
            if (r == null || !r.OtfGuardOn || otf == 0) return false;

            // Fading up-momentum is the short; fading down-momentum is the long.
            return side == TestSide.ResistanceShort ? otf > 0 : otf < 0;
        }
    }

    /// <summary>What one bar did to one zone. The indicator turns these into alerts and CSV rows.</summary>
    public struct Transition
    {
        public SignalState From;
        public SignalState To;
        public bool Changed { get { return From != To; } }
    }

    /// <summary>
    /// Drives every zone through Dormant -> Armed -> Triggered -> Confirmed, and the two ways
    /// out of it. Bar-close only: nothing here runs per tick.
    /// </summary>
    public sealed class SignalEngine
    {
        private readonly Dictionary<string, List<ZoneTouch>> _touches =
            new Dictionary<string, List<ZoneTouch>>();

        public StateRules Rules = new StateRules();

        /// <summary>Running cumulative volume delta, advanced once per closed bar.</summary>
        public decimal Cvd { get; private set; }

        public void Reset()
        {
            _touches.Clear();
            Cvd = 0m;
        }

        public void NoteBarDelta(decimal delta) { Cvd += delta; }

        /// <summary>
        /// Advances one zone across one closed bar. Order matters: a zone that just got run
        /// through must break before anything tries to confirm it.
        /// </summary>
        public Transition Advance(Zone zone, int bar, BarFacts facts, DateTime local,
                                  decimal tickSize, int otf)
        {
            var t = new Transition { From = zone.State, To = zone.State };
            if (zone == null || tickSize <= 0m) return t;

            ZoneMaintenance.NoteBar(zone, facts.Open, facts.Close);

            // Terminal for the session. A zone that got traded through does not get a second
            // chance at the same fade: whoever was defending it is now the one being stopped
            // out, and that is fuel for continuation, not a reason to try again.
            if (zone.State == SignalState.Broken || zone.State == SignalState.Expired)
                return t;

            if (Broke(zone, facts, tickSize))
            {
                zone.State = SignalState.Broken;
                t.To = zone.State;
                return t;
            }

            if (zone.State == SignalState.Triggered)
            {
                // Only a bar that actually reached the zone is a touch. Recording one every bar
                // regardless would let a zone price ran away from keep appending "visits" at
                // whatever the CVD happened to be, and the divergence test would then compare
                // two moments that were never the same test.
                if (Touches(zone, facts, tickSize, zone.Side)) RecordTouch(zone, bar, facts);

                if (Confirms(zone, facts))
                {
                    zone.State = SignalState.Confirmed;
                    t.To = zone.State;
                    return t;
                }

                if (bar - zone.TriggeredBar >= Rules.ClockBars)
                {
                    zone.State = SignalState.Expired;
                    t.To = zone.State;
                }

                return t;
            }

            if (zone.State == SignalState.Confirmed) return t;

            // Dormant or Armed: the arming test is just location, and it re-runs every bar so a
            // zone price left and came back to arms again.
            var side = ZoneMaintenance.SideFor(zone, facts.Open);
            var inside = Touches(zone, facts, tickSize, side);

            if (inside && !SignalGate.BlockedByOtf(side, otf, Rules))
            {
                zone.Side = side;
                if (zone.State != SignalState.Armed)
                {
                    zone.State = SignalState.Armed;
                    zone.ArmedBar = bar;
                    t.To = zone.State;
                }

                RecordTouch(zone, bar, facts);
            }
            else if (zone.State == SignalState.Armed && !inside)
            {
                zone.State = SignalState.Dormant;
                t.To = zone.State;
            }

            return t;
        }

        /// <summary>
        /// Promotes an Armed zone on a qualifying absorption event. Returns false when the zone
        /// was not in a state to be promoted, which is the normal case for most events.
        /// </summary>
        public bool Promote(Zone zone, AbsorptionEvent evt, int bar)
        {
            if (zone == null || evt == null) return false;
            if (zone.State != SignalState.Armed && zone.State != SignalState.Triggered) return false;

            if (zone.State == SignalState.Armed)
            {
                zone.State = SignalState.Triggered;
                zone.TriggeredBar = bar;
            }

            return true;
        }

        /// <summary>
        /// Whether the bar's tested extreme reached the zone.
        ///
        /// ZoneBufferTicks, not BreakBufferTicks. They are different tolerances that happen to
        /// have similar names: the zone buffer is how close price has to get before the level
        /// counts as tested, the break buffer is how far past the cluster it has to go before
        /// the level counts as lost. Using the break buffer here made zones arm on bars that
        /// never came near them.
        /// </summary>
        private bool Touches(Zone zone, BarFacts facts, decimal tickSize, TestSide side)
        {
            if (zone.Spent || zone.TooFar) return false;

            var probe = side == TestSide.SupportLong ? facts.Low : facts.High;

            return zone.ContainsWithBuffer(probe, tickSize * Rules.ZoneBufferTicks);
        }

        /// <summary>
        /// The cluster extreme traded through, plus a buffer. This is the stop, and it is the
        /// stop precisely because it is the price at which the reason for the trade stopped
        /// being true.
        /// </summary>
        private bool Broke(Zone zone, BarFacts facts, decimal tickSize)
        {
            if (!zone.HasCluster) return false;
            if (zone.State == SignalState.Dormant) return false;

            var buffer = tickSize * Rules.BreakBufferTicks;

            return zone.Side == TestSide.SupportLong
                ? facts.Close < zone.ClusterLow - buffer
                : facts.Close > zone.ClusterHigh + buffer;
        }

        /// <summary>
        /// Two ways to confirm, and they answer different questions.
        ///
        /// A delta flip says the aggressors changed sides on this bar -- the fastest, loudest
        /// confirmation, and the one that shows up on a single bar. A CVD higher-low says
        /// something quieter and often earlier: price came back to the same level, or lower, and
        /// the cumulative delta did NOT make a new low with it. Sellers spent less to get to the
        /// same place. That is the divergence the playbook is actually built on, and it survives
        /// bars where the flip has not happened yet.
        /// </summary>
        private bool Confirms(Zone zone, BarFacts facts)
        {
            if (zone.Side == TestSide.SupportLong ? facts.Delta > 0m : facts.Delta < 0m)
                return true;

            return CvdDivergence(zone);
        }

        private bool CvdDivergence(Zone zone)
        {
            List<ZoneTouch> list;
            if (!_touches.TryGetValue(zone.Key, out list) || list.Count < 2) return false;

            var latest = list[list.Count - 1];
            var prior = list[list.Count - 2];

            if (zone.Side == TestSide.SupportLong)
            {
                // Equal or lower low in price, higher low in CVD.
                return latest.Extreme <= prior.Extreme && latest.Cvd > prior.Cvd;
            }

            return latest.Extreme >= prior.Extreme && latest.Cvd < prior.Cvd;
        }

        /// <summary>
        /// One touch per visit, not one per bar. Consecutive bars inside the zone are the same
        /// test; recording each of them would compare a bar against its own neighbour and call
        /// any two-bar wobble a divergence.
        /// </summary>
        private void RecordTouch(Zone zone, int bar, BarFacts facts)
        {
            List<ZoneTouch> list;
            if (!_touches.TryGetValue(zone.Key, out list))
            {
                list = new List<ZoneTouch>();
                _touches[zone.Key] = list;
            }

            var extreme = zone.Side == TestSide.SupportLong ? facts.Low : facts.High;

            if (list.Count > 0 && bar - list[list.Count - 1].Bar <= 1)
            {
                var last = list[list.Count - 1];

                var extended = zone.Side == TestSide.SupportLong
                    ? Math.Min(last.Extreme, extreme)
                    : Math.Max(last.Extreme, extreme);

                list[list.Count - 1] = new ZoneTouch { Bar = bar, Extreme = extended, Cvd = Cvd };
                return;
            }

            list.Add(new ZoneTouch { Bar = bar, Extreme = extreme, Cvd = Cvd });

            // Only the last two visits are ever compared.
            while (list.Count > 4) list.RemoveAt(0);
        }
    }
}
