using System;
using System.Collections.Generic;
using System.Globalization;
using ATAS.Indicators;
using MColor = System.Windows.Media.Color;

namespace OceansAnchor
{
    public partial class OceansAnchor
    {
        private static readonly MColor AlertFg = MColor.FromRgb(250, 250, 250);

        /// <summary>
        /// Every state change lands here. Alerts and CSV rows are the only things it does --
        /// nothing about the trade decision is made in this file.
        /// </summary>
        private void OnTransition(Zone zone, Transition t, int bar, BarFacts facts, DateTime local)
        {
            switch (t.To)
            {
                case SignalState.Armed:
                    if (AlertApproach) Fire(zone, SignalState.Armed, ApproachWav, ApproachText(zone), bar, local);
                    break;

                case SignalState.Confirmed:
                    CacheSignal(zone, facts);
                    if (AlertSignal) Fire(zone, SignalState.Confirmed, SignalWav, SignalText(zone, facts), bar, local);
                    FinishEpisode(zone, SignalState.Confirmed, bar);
                    break;

                case SignalState.Expired:
                    FinishEpisode(zone, SignalState.Expired, bar);
                    break;

                case SignalState.Broken:
                    FinishEpisode(zone, SignalState.Broken, bar);
                    break;
            }
        }

        /// <summary>Fired from the absorption path, where the promotion actually happens.</summary>
        private void OnTriggered(Zone zone, AbsorptionEvent evt)
        {
            if (!AlertTriggered) return;

            Fire(zone, SignalState.Triggered, TriggerWav, TriggerText(zone, evt), evt.Bar,
                 LocalOf(evt.Time));
        }

        /// <summary>
        /// Three gates before a sound is made, and all three earn their place.
        ///
        /// The live-bar gate is the one that decides whether this indicator ever gets trusted.
        /// A chart load replays every bar and, once the history response lands, every trade of
        /// the session -- so without it, adding the indicator produces a wall of alerts for
        /// setups that resolved hours ago. An indicator that cries on load gets muted, and a
        /// muted indicator is worth nothing at 09:14 when the real one fires.
        ///
        /// The A+ window is second: outside it zones still render, but nothing sounds. Third is
        /// per-zone-per-state dedup with a cooldown, so a zone that keeps re-arming as price
        /// chops across its edge says so once.
        /// </summary>
        private void Fire(Zone zone, SignalState state, string wav, string message, int bar,
                          DateTime local)
        {
            if (SilentCalibration) return;
            if (!AlertsEnabled) return;
            if (!_historyDone) return;

            // "Now" is the newest CLOSED bar, not the forming one.
            //
            // Every state transition in this indicator is decided at bar close, so a signal is
            // always stamped CurrentBar - 2 at the moment it happens; tape promotions on the
            // forming bar are stamped CurrentBar - 1. Requiring the live bar exactly -- which is
            // the obvious reading of "alert only on the live bar" -- would therefore silence
            // every confirmation the indicator ever produced, which is most of them.
            if (bar < CurrentBar - 2) return;

            if (!SignalGate.Allowed(local, _engine.Rules)) return;

            var now = MarketTime.Year > 2000 ? MarketTime : DateTime.UtcNow;

            DateTime last;
            if (zone.LastAlert.TryGetValue(state, out last) &&
                (now - last).TotalSeconds < AlertCooldownSec)
                return;

            zone.LastAlert[state] = now;

            var background = state == SignalState.Confirmed
                ? ZoneColor
                : state == SignalState.Triggered ? MarkColor : MColor.FromRgb(70, 78, 86);

            try
            {
                AddAlert(wav, InstrumentInfo != null ? InstrumentInfo.Instrument : "MNQ",
                         message, background, AlertFg);
            }
            catch (Exception) { }
        }

        #region Alert text

        private string ApproachText(Zone zone)
        {
            return "ANCHOR approach " + Rank(zone) + " " + Band(zone) + " " + Dot + " " +
                   (zone.Side == TestSide.SupportLong ? "support" : "resistance");
        }

        private string TriggerText(Zone zone, AbsorptionEvent evt)
        {
            var stacked = zone.Live != null ? zone.Live.StackedEvents : 1;

            var text = "ANCHOR triggered " + Rank(zone) + " " + Band(zone) + " " + Dot + " " +
                       Num(evt.Volume, 0) + " lot " +
                       (evt.Path == EventPath.Tape ? "tape" : "cluster");

            if (stacked > 1) text += " " + Dot + " " + stacked + " stacked";

            return text;
        }

        /// <summary>
        /// The signal alert carries the stop and the 1.5R target, because the decision at that
        /// moment is not "is this a setup" -- the indicator just said it was -- it is "does the
        /// R:R clear the floor". Making that arithmetic visible at signal time is the whole
        /// point of tracking the cluster extremes.
        /// </summary>
        private string SignalText(Zone zone, BarFacts facts)
        {
            var text = "ANCHOR SIGNAL " + Rank(zone) + " " + Band(zone) + " " + Dot + " " +
                       (zone.Side == TestSide.SupportLong ? "LONG" : "SHORT");

            decimal stop, target;
            if (Targets(zone, facts, out stop, out target))
                text += " " + Dot + " stop " + Num(stop, 2) + ", 1.5R " + Num(target, 2);

            return text;
        }

        /// <summary>
        /// Stop is the cluster extreme plus the break buffer -- the price at which the reason
        /// for the trade stopped being true, which is the only defensible place to put one.
        /// </summary>
        private bool Targets(Zone zone, BarFacts facts, out decimal stop, out decimal target)
        {
            stop = 0m;
            target = 0m;

            var tick = Tick;
            if (!zone.HasCluster || tick <= 0m) return false;

            var buffer = tick * BreakBufferTicks;
            var entry = facts.Close;

            if (zone.Side == TestSide.SupportLong)
            {
                stop = zone.ClusterLow - buffer;
                if (entry <= stop) return false;

                target = entry + (entry - stop) * 1.5m;
            }
            else
            {
                stop = zone.ClusterHigh + buffer;
                if (entry >= stop) return false;

                target = entry - (stop - entry) * 1.5m;
            }

            return true;
        }

        private static string Rank(Zone zone) { return "r" + zone.Rank; }

        private string Band(Zone zone)
        {
            return Num(zone.Bottom, 2) + "-" + Num(zone.Top, 2);
        }

        private static string Num(decimal v, int places)
        {
            return Math.Round(v, places).ToString(CultureInfo.InvariantCulture);
        }

        #endregion

        #region Marks

        /// <summary>
        /// One PriceSelection rectangle per absorption event, spanning the cluster extremes so
        /// the mark shows the stop it implies rather than just where a print landed.
        ///
        /// Cluster-path marks are drawn more transparent than tape marks on purpose: they are a
        /// reconstruction, and it should never be possible to mistake one for the real tape at a
        /// glance.
        /// </summary>
        private void AddMark(Zone zone, AbsorptionEvent evt, decimal tick)
        {
            // Skipped rather than hidden: ATAS recalculates on any setting change, so turning
            // silent calibration off rebuilds every mark anyway.
            if (_marks == null || SilentCalibration) return;

            var bar = evt.Bar;
            if (bar < 0 || bar >= CurrentBar) return;

            var size = SizeFor(evt.Volume);
            var color = evt.Path == EventPath.Tape ? MarkColor : Dim(MarkColor, 150);

            var value = new PriceSelectionValue(evt.Price)
            {
                VisualObject = ObjectType.Rectangle,
                SelectionSide = evt.Direction == Aggressor.Sell ? SelectionType.Bid : SelectionType.Ask,
                MinimumPrice = zone.HasCluster ? zone.ClusterLow : evt.Price - tick,
                MaximumPrice = zone.HasCluster ? zone.ClusterHigh : evt.Price + tick,
                Size = size,
                ObjectColor = color,
                ObjectsTransparency = evt.Path == EventPath.Tape ? 6 : 9,
                PriceSelectionColor = color,
                Tooltip = Num(evt.Volume, 0) + " @ " + Num(evt.Price, 2) + " " +
                          (evt.Path == EventPath.Tape ? "tape" : "cluster") +
                          " " + Dot + " disp " + Num(evt.DisplacementTicks, 1) + "t",
                Context = evt.Volume
            };

            try { _marks[bar].Add(value); }
            catch (Exception) { }
        }

        /// <summary>
        /// Mark size scaled by volume above the floor, clamped. Unclamped, one 5000-lot print
        /// draws a rectangle that covers the panel.
        /// </summary>
        private int SizeFor(decimal volume)
        {
            var floor = SizeFloor > 0m ? SizeFloor : 1m;
            var ratio = volume / floor;

            var size = 6 + (int)Math.Round((double)(ratio - 1m) * 3.0);

            if (size < 6) size = 6;
            if (size > 24) size = 24;

            return size;
        }

        private static MColor Dim(MColor c, byte alpha)
        {
            return MColor.FromArgb(alpha, c.R, c.G, c.B);
        }

        #endregion
    }
}
