using System;
using System.Globalization;

namespace OceansCurrent
{
    /// <summary>
    /// The dealer-gamma regime as Tide Engine last reported it. Immutable: the file is read on
    /// the calculation thread and this whole object is swapped in one reference write, so the
    /// render thread can never see half of an update.
    ///
    /// Every field is a <see cref="Level"/> or a flag, because a missing wall and a wall at zero
    /// are not the same thing and one of them is a level 24,000 points away from price.
    /// </summary>
    public sealed class GexSnapshot
    {
        public static readonly GexSnapshot Empty = new GexSnapshot { Problem = "no file" };

        /// <summary>When Tide Engine computed this. Absent if the row carried no usable stamp.</summary>
        public DateTime StampUtc { get; private set; }
        public bool HasStamp { get; private set; }

        /// <summary>+1 long gamma, -1 short gamma, 0 mixed. 0 applies no transform at all.</summary>
        public int Regime { get; private set; }

        public Level Spot { get; private set; }
        public Level NetGex { get; private set; }
        public Level GammaFlip { get; private set; }
        public Level CallWall { get; private set; }
        public Level PutWall { get; private set; }
        public Level Hvl { get; private set; }

        /// <summary>Why there is no regime, in the words the badge prints. Null when fine.</summary>
        public string Problem { get; private set; }

        /// <summary>Where the reading came from, for the panel. Null for the CSV.</summary>
        public string Source { get; private set; }

        /// <summary>
        /// A reading taken from a streaming source at the moment it was used. Its age is the
        /// time since this indicator last looked, not the time since the data was computed, so
        /// the file-age staleness rule does not apply -- whether it is live is the source's own
        /// status, which is checked before a snapshot is ever built.
        /// </summary>
        public bool LiveStream { get; private set; }

        public static GexSnapshot Failed(string why, string source) =>
            new GexSnapshot { Problem = why, Source = source };

        public bool Usable => Problem == null && Regime != 0;

        public int AgeSeconds(DateTime nowUtc) =>
            HasStamp ? (int)Math.Max(0d, (nowUtc - StampUtc).TotalSeconds) : -1;

        /// <summary>Stale readings are not regime readings. The transforms come off entirely.</summary>
        public bool IsStale(DateTime nowUtc, int staleAfterMinutes) =>
            !LiveStream && (!HasStamp || AgeSeconds(nowUtc) > staleAfterMinutes * 60);

        /// <summary>
        /// Parse the one wide row Tide Engine writes. Anything unreadable comes back as a
        /// snapshot with a <see cref="Problem"/> and no regime -- never as a partial reading,
        /// because a half-parsed row is a made-up regime and the transforms are multiplicative.
        /// </summary>
        public static GexSnapshot Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new GexSnapshot { Problem = "empty" };

            var lines = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            string row = null;
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("ts_utc", StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.StartsWith("#")) continue;
                row = trimmed;
            }

            if (row == null) return new GexSnapshot { Problem = "header only" };

            var f = row.Split(',');
            if (f.Length < 7) return new GexSnapshot { Problem = "short row" };

            var snap = new GexSnapshot();

            DateTime stamp;
            if (DateTime.TryParse(f[0].Trim(), CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                  out stamp))
            {
                snap.StampUtc = DateTime.SpecifyKind(stamp, DateTimeKind.Utc);
                snap.HasStamp = true;
            }
            else
            {
                return new GexSnapshot { Problem = "bad timestamp" };
            }

            snap.Spot = Number(f, 1);

            var regime = Number(f, 2);
            if (!regime.Known) return new GexSnapshot { Problem = "no regime" };

            var r = (int)Math.Round(regime.Value, MidpointRounding.AwayFromZero);
            snap.Regime = r > 0 ? 1 : r < 0 ? -1 : 0;

            snap.NetGex = Number(f, 3);
            snap.GammaFlip = Price(f, 4);
            snap.CallWall = Price(f, 5);
            snap.PutWall = Price(f, 6);
            snap.Hvl = Price(f, 7);

            return snap;
        }

        /// <summary>
        /// A price field. Blank, unparseable or non-positive is ABSENT, not zero: a wall at
        /// zero would sit permanently 24,000 points below price and read as "never near a wall",
        /// which is exactly the silent-wrong-answer this suite refuses to ship.
        /// </summary>
        private static Level Price(string[] f, int i)
        {
            var n = Number(f, i);
            return n.Known && n.Value > 0m ? n : Level.None;
        }

        private static Level Number(string[] f, int i)
        {
            if (i >= f.Length) return Level.None;

            var s = f[i].Trim();
            if (s.Length == 0) return Level.None;

            decimal d;
            if (decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return Level.At(d);

            // Tide Engine writes net gamma in scientific notation (1.83e9). Above the decimal
            // range it is still a real reading, so it is kept as a double-widened value rather
            // than thrown away -- net gamma is only ever read for its sign and its magnitude.
            double g;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out g)
                && !double.IsNaN(g) && !double.IsInfinity(g)
                && g < 7.9e28d && g > -7.9e28d)
                return Level.At((decimal)g);

            return Level.None;
        }

        /// <summary>
        /// A regime built from levels another indicator already has on the chart -- TradeGEX --
        /// rather than from a file. The strikes arrive in the ETF's (or index's) own units and
        /// are converted exactly the way TradeGEX draws them: <c>strike x ratio</c>.
        ///
        /// That ratio is not trusted. TradeGEX sets it to <b>1</b> whenever its feed omits one,
        /// which in ETF mode would put a QQQ gamma flip at 713 on a 29,000 chart: every bar
        /// "above the flip", long gamma forever, and nothing on screen to say so. So every
        /// converted level has to land within <paramref name="maxFraction"/> of price or it is
        /// refused. A flip that fails is a Problem and there is no regime; a wall that fails is
        /// simply absent. There is no substitute ratio anywhere in here.
        ///
        /// The regime is the side of the flip price is on: above it dealers are long gamma and
        /// damp the move, below it they are short gamma and chase it.
        /// </summary>
        public static GexSnapshot FromLevels(DateTime stampUtc, decimal close,
                                             decimal? flipStrike, decimal? callWallStrike,
                                             decimal? putWallStrike, decimal ratio,
                                             decimal maxFraction)
        {
            const string src = "TradeGEX";

            if (close <= 0m) return Failed("no price", src);
            if (ratio <= 0m) return Failed("no ratio", src);
            if (!flipStrike.HasValue || flipStrike.Value <= 0m) return Failed("no flip", src);

            var flip = Converted(flipStrike, ratio, close, maxFraction);
            if (!flip.Known)
                return Failed("units off (ratio " + ratio.ToString("0.####", CultureInfo.InvariantCulture) + ")", src);

            var snap = new GexSnapshot
            {
                StampUtc = DateTime.SpecifyKind(stampUtc, DateTimeKind.Utc),
                HasStamp = true,
                LiveStream = true,
                Source = "TradeGEX",
                Spot = Level.At(close),
                GammaFlip = flip,
                CallWall = Converted(callWallStrike, ratio, close, maxFraction),
                PutWall = Converted(putWallStrike, ratio, close, maxFraction)
            };

            // Exactly on the flip is neither side, and Usable then applies no transform at all.
            snap.Regime = close > flip.Value ? 1 : close < flip.Value ? -1 : 0;

            return snap;
        }

        private static Level Converted(decimal? strike, decimal ratio, decimal close, decimal maxFraction)
        {
            if (!strike.HasValue || strike.Value <= 0m) return Level.None;

            var price = strike.Value * ratio;
            return Math.Abs(price - close) <= close * maxFraction ? Level.At(price) : Level.None;
        }

        /// <summary>
        /// The same streamed levels, read from a different price: the regime is the side of the
        /// flip, so a what-if close across the flip is in the other regime. Exactly what the
        /// indicator does on a real bar, which rebuilds the snapshot at that bar's own close.
        /// A file snapshot carries its regime as a stated fact and is returned unchanged.
        /// </summary>
        public GexSnapshot AtPrice(decimal price)
        {
            if (!LiveStream || !GammaFlip.Known || Problem != null) return this;

            var c = (GexSnapshot)MemberwiseClone();
            c.Spot = Level.At(price);
            c.Regime = price > GammaFlip.Value ? 1 : price < GammaFlip.Value ? -1 : 0;
            return c;
        }

        /// <summary>Distance from price to the flip level, absent if there is no flip level.</summary>
        public Level DistanceToFlip(decimal price) =>
            GammaFlip.Known ? Level.At(price - GammaFlip.Value) : Level.None;

        /// <summary>True when price is inside the damping zone of either wall.</summary>
        public bool InWallZone(decimal price, decimal zone)
        {
            if (zone <= 0m) return false;

            if (CallWall.Known && Math.Abs(price - CallWall.Value) <= zone) return true;
            if (PutWall.Known && Math.Abs(price - PutWall.Value) <= zone) return true;

            return false;
        }
    }
}
