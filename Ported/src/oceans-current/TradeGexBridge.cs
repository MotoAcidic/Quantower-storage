using System;
using System.Collections;
using System.Reflection;
using System.Threading;

namespace OceansCurrent
{
    /// <summary>What TradeGEX had on this bar, in its own units. Nothing here is converted.</summary>
    public struct TradeGexReading
    {
        public bool Found;
        public string Problem;
        public string Status;
        public decimal? Flip;
        public decimal? CallWall;
        public decimal? PutWall;
        public decimal Ratio;
    }

    /// <summary>
    /// Reads the gamma flip and walls straight out of the TradeGEX indicator already running in
    /// this ATAS process, so the regime needs no CSV, no Tide Engine and no second subscription.
    ///
    /// There is no API for one indicator to see another, so this goes in by reflection, and it
    /// is written to fail visibly rather than cleverly:
    ///
    /// - No compile-time reference. TradeGEX not installed, not on any chart, or updated into a
    ///   different shape all come back as a <see cref="TradeGexReading.Problem"/> the panel
    ///   prints -- never an exception, never a guessed level.
    /// - Every live TradeGEX indicator registers itself in the static
    ///   <c>TgxAuth._consumers</c> list; the <c>Owner</c> of each entry is the indicator. Only a
    ///   <c>TradeGexLevels</c> on the SAME instrument is read. A TradeGEX on an ES chart is not an
    ///   MNQ gamma flip.
    /// - Its fields are read under its own <c>_lock</c>, the one it writes them under, so a
    ///   flip is never paired with the previous refresh's walls. The wait is bounded: if the
    ///   lock is busy this bar simply has no fresh reading.
    /// - <c>_status</c> must say "live". TradeGEX clears its strikes on a source change but not
    ///   on a dropped socket, so a signed-out or reconnecting instance could still be holding
    ///   this morning's numbers.
    ///
    /// Field names verified against TradeGEX.dll deployed 2026-08-22 (decompiled 2026-09-10).
    /// </summary>
    public sealed class TradeGexBridge
    {
        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private Type _auth;
        private Type _levels;
        private bool _searched;

        public TradeGexReading Read(string instrument)
        {
            try
            {
                if (!_searched || _auth == null) Locate();

                if (_auth == null) return Fail("TradeGEX not loaded");
                if (_levels == null) return Fail("TradeGEX changed");

                var consumersField = _auth.GetField("_consumers", Any);
                if (consumersField == null) return Fail("TradeGEX changed");

                var consumers = consumersField.GetValue(null) as IEnumerable;
                if (consumers == null) return Fail("TradeGEX not on a chart");

                // TradeGEX mutates the list only under its static _hub lock, so the copy is taken
                // under the same one -- bounded, and released before any indicator's own lock is
                // touched, so the two are never held together.
                var hub = _auth.GetField("_hub", Any)?.GetValue(null);
                if (hub == null) return Fail("TradeGEX changed");
                if (!Monitor.TryEnter(hub, 50)) return Fail("TradeGEX busy");

                var owners = new ArrayList();
                try
                {
                    foreach (var consumer in consumers)
                    {
                        if (consumer == null) continue;
                        var owner = consumer.GetType().GetField("Owner", Any)?.GetValue(consumer);
                        if (owner != null && _levels.IsInstanceOfType(owner)) owners.Add(owner);
                    }
                }
                finally
                {
                    Monitor.Exit(hub);
                }

                if (owners.Count == 0) return Fail("TradeGEX not on a chart");

                string otherSymbol = null;

                foreach (var owner in owners)
                {
                    var symbol = SymbolOf(owner);
                    if (!SameInstrument(symbol, instrument))
                    {
                        otherSymbol = otherSymbol ?? symbol;
                        continue;
                    }

                    return ReadOwner(owner);
                }

                return Fail(otherSymbol != null
                    ? "TradeGEX is on " + otherSymbol + ", not " + instrument
                    : "TradeGEX not on " + instrument);
            }
            catch (Exception ex)
            {
                return Fail("TradeGEX " + ex.GetType().Name);
            }
        }

        private TradeGexReading ReadOwner(object owner)
        {
            var t = owner.GetType();
            var gate = t.GetField("_lock", Any)?.GetValue(owner);
            if (gate == null) return Fail("TradeGEX changed");

            if (!Monitor.TryEnter(gate, 50)) return Fail("TradeGEX busy");

            try
            {
                var reading = new TradeGexReading
                {
                    Found = true,
                    Status = t.GetField("_status", Any)?.GetValue(owner) as string,
                    Flip = t.GetField("_flipStrike", Any)?.GetValue(owner) as decimal?,
                    CallWall = t.GetField("_cwStrike", Any)?.GetValue(owner) as decimal?,
                    PutWall = t.GetField("_pwStrike", Any)?.GetValue(owner) as decimal?
                };

                var ratio = t.GetField("_ratio", Any)?.GetValue(owner);
                if (!(ratio is decimal)) return Fail("TradeGEX changed");
                reading.Ratio = (decimal)ratio;

                var unsupported = t.GetField("_unsupported", Any)?.GetValue(owner);
                if (unsupported is bool && (bool)unsupported)
                    return Fail("TradeGEX: symbol unsupported");

                if (reading.Status == null ||
                    !reading.Status.StartsWith("live", StringComparison.OrdinalIgnoreCase))
                {
                    reading.Problem = "TradeGEX " + (string.IsNullOrEmpty(reading.Status) ? "not live" : reading.Status);
                }

                return reading;
            }
            finally
            {
                Monitor.Exit(gate);
            }
        }

        private void Locate()
        {
            _searched = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(asm.GetName().Name, "TradeGEX", StringComparison.OrdinalIgnoreCase))
                    continue;

                _auth = asm.GetType("TradeGEX.TgxAuth", false);
                _levels = asm.GetType("TradeGEX.TradeGexLevels", false);
                return;
            }
        }

        /// <summary>The instrument TradeGEX's chart is on. InstrumentInfo is protected, hence reflection.</summary>
        private static string SymbolOf(object owner)
        {
            for (var t = owner.GetType(); t != null; t = t.BaseType)
            {
                var prop = t.GetProperty("InstrumentInfo", Any | BindingFlags.DeclaredOnly);
                if (prop == null) continue;

                var info = prop.GetValue(owner);
                if (info == null) return null;

                return info.GetType().GetProperty("Instrument")?.GetValue(info) as string;
            }

            return null;
        }

        /// <summary>
        /// Same contract, allowing for the way feeds decorate a symbol (MNQU6, MNQU6.CME, ...).
        /// An unknown symbol on either side is NOT a match: guessing which chart a gamma flip
        /// belongs to is how an ES level ends up scoring MNQ.
        /// </summary>
        public static bool SameInstrument(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;

            a = Root(a);
            b = Root(b);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        private static string Root(string s)
        {
            s = s.Trim();
            var dot = s.IndexOf('.');
            if (dot > 0) s = s.Substring(0, dot);
            var at = s.IndexOf('@');
            if (at > 0) s = s.Substring(0, at);
            return s;
        }

        private static TradeGexReading Fail(string why) =>
            new TradeGexReading { Found = false, Problem = why };
    }
}
