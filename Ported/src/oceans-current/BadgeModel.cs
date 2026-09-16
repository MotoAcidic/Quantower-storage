using System;
using System.Globalization;

namespace OceansCurrent
{
    /// <summary>What a cell means, so the renderer can colour it without re-deciding anything.</summary>
    public enum CellKind
    {
        Plain = 0,
        Long = 1,
        Short = 2,
        Neutral = 3,
        Warn = 4,
        Muted = 5
    }

    public struct BadgeCell
    {
        public string Label;
        public string Text;
        public CellKind Kind;
    }

    /// <summary>
    /// Every word and number on the badge, worked out as data before anything is drawn. The
    /// renderer only places these; it decides nothing. That is what lets the harness assert what
    /// the chart says -- including that it says "waiting" rather than a confident zero.
    /// </summary>
    public static class BadgeModel
    {
        public const int CellCount = 8;

        public static BadgeCell[] Waiting(string why)
        {
            var cells = new BadgeCell[1];
            cells[0] = new BadgeCell { Label = "OCEAN'S CURRENT", Text = why, Kind = CellKind.Muted };
            return cells;
        }

        public static BadgeCell[] Build(BiasRecord r, bool hasProvisional, decimal provisional,
                                        GexSnapshot gex, bool gexPresent, bool gexStale,
                                        int gexAgeSeconds, decimal tick)
        {
            var cells = new BadgeCell[CellCount];

            cells[0] = new BadgeCell
            {
                Label = "STATE",
                Text = BiasEngine.StateText(r.State),
                Kind = r.State == BiasState.Long ? CellKind.Long
                     : r.State == BiasState.Short ? CellKind.Short
                     : CellKind.Neutral
            };

            cells[1] = new BadgeCell
            {
                Label = "SCORE",
                // The committed number in full, the forming bar's in brackets. They are
                // different things and the badge never blends them into one figure.
                Text = r.ScoreKnown
                    ? Signed(r.Score) + (hasProvisional ? " (" + Signed(provisional) + ")" : "")
                    : "no vote",
                Kind = r.ScoreKnown ? CellKind.Plain : CellKind.Muted
            };

            cells[2] = new BadgeCell
            {
                Label = "CONF",
                Text = r.ScoreKnown ? ((int)r.Confidence).ToString(CultureInfo.InvariantCulture) : "-",
                Kind = CellKind.Plain
            };

            cells[3] = new BadgeCell
            {
                Label = "REGIME",
                Text = RegimeText(r, gex, gexPresent, gexStale, tick),
                Kind = r.Regime == RegimeMode.None ? CellKind.Muted : CellKind.Plain
            };

            cells[4] = new BadgeCell
            {
                Label = "FLIP",
                Text = r.Flip.Known ? Price(r.Flip.Value, tick) : "none below" ,
                Kind = r.Flip.Known ? CellKind.Plain : CellKind.Muted
            };

            if (!r.Flip.Known && r.State == BiasState.Short) cells[4].Text = "none above";
            if (!r.Flip.Known && r.State == BiasState.Neutral) cells[4].Text = "-";

            cells[5] = new BadgeCell { Label = "DRIVERS", Text = r.Drivers ?? "-", Kind = CellKind.Plain };

            cells[6] = new BadgeCell
            {
                Label = "NOTES",
                Text = r.Notes ?? "-",
                Kind = r.Notes != null && r.Notes != "-" ? CellKind.Warn : CellKind.Muted
            };

            cells[7] = FeedCell(gexPresent, gexStale, gexAgeSeconds, gex);

            return cells;
        }

        private static BadgeCell FeedCell(bool present, bool stale, int ageSeconds, GexSnapshot gex)
        {
            if (!present)
                return new BadgeCell { Label = "FEED", Text = "GEX -", Kind = CellKind.Muted };

            if (gex != null && gex.Problem != null)
                return new BadgeCell { Label = "FEED", Text = "GEX " + gex.Problem, Kind = CellKind.Warn };

            if (stale)
                return new BadgeCell { Label = "FEED", Text = "GEX STALE " + Age(ageSeconds), Kind = CellKind.Warn };

            return new BadgeCell { Label = "FEED", Text = "GEX " + Age(ageSeconds), Kind = CellKind.Plain };
        }

        private static string RegimeText(BiasRecord r, GexSnapshot gex, bool present, bool stale, decimal tick)
        {
            if (!present) return "no feed";
            if (stale) return "stale";
            if (r.Regime == RegimeMode.None) return "MIXED";

            var text = r.Regime == RegimeMode.Positive ? "+GAMMA" : "-GAMMA";

            if (gex != null && gex.GammaFlip.Known)
            {
                var distance = r.Close - gex.GammaFlip.Value;
                text += " flip " + Signed(BiasEngine.Round(distance, tick));
            }

            return text;
        }

        public static string Age(int seconds)
        {
            if (seconds < 0) return "?";
            if (seconds < 90) return seconds + "s";

            var minutes = seconds / 60;
            if (minutes < 90) return minutes + "m";

            return (minutes / 60) + "h";
        }

        private static string Signed(decimal value)
        {
            var rounded = Math.Round(value, 0, MidpointRounding.AwayFromZero);
            return (rounded > 0m ? "+" : "") + rounded.ToString("0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A price at the instrument's own resolution. Two decimals is right for MNQ and NQ
        /// alike -- both tick at 0.25 -- and the tick is read rather than assumed so a different
        /// contract does not silently print a level it cannot trade at.
        /// </summary>
        public static string Price(decimal value, decimal tick)
        {
            var rounded = BiasEngine.Round(value, tick);
            var decimals = Decimals(tick);

            return rounded.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        private static int Decimals(decimal tick)
        {
            if (tick <= 0m) return 2;

            for (var d = 0; d <= 8; d++)
            {
                var scaled = tick * Pow10(d);
                if (scaled == Math.Truncate(scaled)) return d;
            }

            return 2;
        }

        private static decimal Pow10(int n)
        {
            var v = 1m;
            for (var i = 0; i < n; i++) v *= 10m;
            return v;
        }
    }
}
