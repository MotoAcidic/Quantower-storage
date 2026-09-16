using System;
using System.Collections.Generic;

namespace OceansCurrent
{
    /// <summary>How the calls on this chart actually played out.</summary>
    public sealed class Scorecard
    {
        /// <summary>Closed LONG/SHORT calls: entered on one committed close, left on another.</summary>
        public int Calls;
        public int Right;
        public decimal NetPoints;
        public decimal Best;
        public decimal Worst;

        /// <summary>The call still open now, if any: its direction, where it started, and how it is doing.</summary>
        public bool OpenCall;
        public BiasState OpenState;
        public decimal OpenEntry;
        public DateTime OpenSince;
        public decimal OpenPoints;

        /// <summary>Calls closed today (the trading day of the last record).</summary>
        public int TodayCalls;
        public decimal TodayPoints;

        public decimal HitRate => Calls > 0 ? (decimal)Right / Calls : 0m;
        public decimal AvgPoints => Calls > 0 ? NetPoints / Calls : 0m;

        /// <summary>
        /// Below this many closed calls a hit rate is noise. The first calibration needed ~2,200
        /// sessions to resolve a 3-point edge; thirty calls resolves nothing, and the panel says
        /// so rather than print "63% right" as though it meant something.
        /// </summary>
        public const int TooFew = 30;

        /// <summary>
        /// Grades every committed LONG and SHORT: in on the close of the bar that entered it, out
        /// on the close of the bar that left it, in points, in the direction called.
        ///
        /// This is evaluation, so it reads forward -- a call's outcome is only known once it
        /// ends -- but nothing here ever feeds back into a state. The states were committed from
        /// closed bars alone; this only scores them afterwards.
        ///
        /// Two limits worth knowing. The history on a chart is scored without the gamma regime
        /// (levels are live only), so this grades the price-and-delta engine. And "right" means
        /// the call closed in profit, from its own entry to its own exit, with no costs: it is
        /// the question "would following it have made points", not a backtest of a strategy.
        /// </summary>
        public static Scorecard Grade(IReadOnlyList<BiasRecord> records, Func<DateTime, DateTime> tradingDay)
        {
            var card = new Scorecard { Best = decimal.MinValue, Worst = decimal.MaxValue };
            if (records == null || records.Count == 0)
            {
                card.Best = card.Worst = 0m;
                return card;
            }

            var today = tradingDay(records[records.Count - 1].Local);

            var inCall = false;
            var dir = 0;
            var entry = 0m;
            var since = default(DateTime);
            var state = BiasState.Neutral;

            for (var i = 0; i < records.Count; i++)
            {
                var r = records[i];

                if (inCall && r.State != state)
                {
                    var pts = dir * (r.Close - entry);
                    card.Calls++;
                    if (pts > 0m) card.Right++;
                    card.NetPoints += pts;
                    if (pts > card.Best) card.Best = pts;
                    if (pts < card.Worst) card.Worst = pts;

                    if (tradingDay(r.Local) == today)
                    {
                        card.TodayCalls++;
                        card.TodayPoints += pts;
                    }

                    inCall = false;
                }

                if (!inCall && r.State != BiasState.Neutral && (i == 0 || records[i - 1].State != r.State))
                {
                    inCall = true;
                    state = r.State;
                    dir = r.State == BiasState.Long ? 1 : -1;
                    entry = r.Close;
                    since = r.Local;
                }
            }

            if (inCall)
            {
                var lastRec = records[records.Count - 1];
                card.OpenCall = true;
                card.OpenState = state;
                card.OpenEntry = entry;
                card.OpenSince = since;
                card.OpenPoints = dir * (lastRec.Close - entry);
            }

            if (card.Calls == 0) card.Best = card.Worst = 0m;
            return card;
        }
    }
}
