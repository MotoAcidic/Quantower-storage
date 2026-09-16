using System;
using System.Collections.Generic;
using System.Globalization;

namespace OceansEffort
{
    /// <summary>What a piece of text means, which is what decides its colour.</summary>
    public enum Tone
    {
        /// <summary>A plain fact with no side to it.</summary>
        Neutral,

        Bullish,
        Bearish,

        /// <summary>Absorption, and anything the reader should not skim past.</summary>
        Warning,

        /// <summary>Present but saying nothing: balanced, silent, unknown.</summary>
        Muted
    }

    /// <summary>One line of the box. A section row carries only its label.</summary>
    public sealed class BoxRow
    {
        public string Label;
        public Tone LabelTone = Tone.Muted;

        public string[] Cells = new string[0];
        public Tone[] Tones = new Tone[0];

        public bool Section;
    }

    /// <summary>
    /// Everything the box says, with no idea how it is drawn. Keeping it this side of the render
    /// loop is deliberate: the numbers a trader acts on are worth testing, and a string built
    /// inside OnRender can only be checked by looking at a chart.
    /// </summary>
    public sealed class BoxModel
    {
        public string Headline;
        public Tone HeadlineTone = Tone.Muted;

        public readonly List<BoxRow> Rows = new List<BoxRow>();

        public string Status;
        public Tone StatusTone = Tone.Muted;

        public readonly List<string> Warnings = new List<string>();
    }

    /// <summary>Everything the box is built from. Gathered once per frame by the indicator.</summary>
    public sealed class BoxInputs
    {
        public int LastBar;
        public BarFacts Facts;

        public Side Migration;
        public EffortVerdict Effort;
        public int EffortWindowBars;

        public bool HasAbsorption;
        public AbsorptionMark Absorption;

        public SetupRun Run;

        public decimal Tick;

        /// <summary>Money per tick per contract. Zero when the platform has not said, and then no money is shown.</summary>
        public decimal TickCost;

        public Func<decimal, string> Price;

        /// <summary>The profile of the bars in view, or null when there is none to draw.</summary>
        public RangeProfile Profile;
        public Cluster[] Clusters;
        public string ProfileProblem;
        public string TrackProblem;

        /// <summary>Where the last close stands in that profile.</summary>
        public Location Where;

        /// <summary>Whether a setup is required to come from a level at all.</summary>
        public bool RequireLevel;

        public int BarsMissingValue;
        public int BarsInView;

        public List<string> Errors;
    }

    public static class TradeBox
    {
        /// <summary>
        /// Turns the three layers and the running setup into the block of text on the chart.
        ///
        /// Two rules run through all of it. Nothing is stated that was not measured -- an unknown
        /// stays unknown and says so. And money only appears when the platform has given a tick
        /// cost; a guessed one would be wrong on every instrument but the one it was guessed for.
        /// </summary>
        public static BoxModel Build(BoxInputs input)
        {
            var box = new BoxModel();
            if (input == null) return box;

            var price = input.Price ?? (p => p.ToString(CultureInfo.InvariantCulture));

            Profile(box, input, price);
            Read(box, input, price);
            Trade(box, input, price);
            Warnings(box, input);

            return box;
        }

        #region Where price is standing

        /// <summary>
        /// The profile section: what the auction has built, and where price is standing in it.
        /// The transcript's reason for this being first -- "use your confirmation to buy from
        /// levels that are relevant for the order flow" -- is that everything below is worth less
        /// if it fires in mid-air.
        /// </summary>
        private static void Profile(BoxModel box, BoxInputs input, Func<decimal, string> price)
        {
            box.Rows.Add(Section("PROFILE"));

            var profile = input.Profile;

            if (profile == null || profile.Count == 0)
            {
                box.Rows.Add(Row("VALUE", Tone.Muted,
                                 Cell(input.ProfileProblem ?? "no profile from the bars in view",
                                      Tone.Warning)));
                return;
            }

            box.Rows.Add(Row("VALUE", Tone.Muted,
                             Cell(price(profile.ValueLow) + " - " + price(profile.ValueHigh), Tone.Neutral),
                             Cell("poc " + price(profile.Poc), Tone.Warning),
                             Cell(EffortMath.Compact(profile.TotalVolume) + " traded", Tone.Muted)));

            var where = input.Where;

            if (!where.Known)
            {
                box.Rows.Add(Row("AT", Tone.Muted, Cell("price not placed in the profile", Tone.Muted)));
                return;
            }

            var zone = where.Zone == Zone.Premium ? "premium, above value"
                     : where.Zone == Zone.Discount ? "discount, below value"
                     : "inside value";

            // Above value is expensive to buy and below it is cheap -- the transcript's iPhone in
            // the Apple store. The tone follows what the location argues FOR.
            var zoneTone = where.Zone == Zone.Premium ? Tone.Bearish
                         : where.Zone == Zone.Discount ? Tone.Bullish : Tone.Muted;

            box.Rows.Add(Row("AT", Of(where.Favours),
                             Cell(zone, zoneTone),
                             Cell(Signed(where.ToPoc) + "t from poc", Tone.Neutral),
                             Cell(Edge(where), Tone.Muted)));

            box.Rows.Add(ClusterRow(where, price));
        }

        private static BoxRow ClusterRow(Location where, Func<decimal, string> price)
        {
            if (!where.AtCluster)
            {
                return Row("CLUSTER", Tone.Muted,
                           Cell("none: price is in mid-air, not on a level", Tone.Muted));
            }

            var cluster = where.Cluster;

            var what = cluster.Kind == ClusterKind.Absorption
                ? (cluster.Side == Side.Buy ? "buyers absorbed here" : "sellers absorbed here")
                : (cluster.Side == Side.Buy ? "buyers drove through here" : "sellers drove through here");

            return Row("CLUSTER", Of(cluster.Favours),
                       Cell(what, Of(cluster.Favours)),
                       Cell(price(cluster.Low) + " - " + price(cluster.High), Tone.Neutral),
                       Cell(EffortMath.Compact(cluster.Volume) + "  " +
                            Math.Round(cluster.Share * 100m) + "% of range", Tone.Muted));
        }

        private static string Edge(Location where)
        {
            if (where.AtValueLow && where.AtValueHigh) return "at both value edges";
            if (where.AtValueLow) return "at value low";
            if (where.AtValueHigh) return "at value high";

            return "";
        }

        #endregion

        #region What the three layers say

        private static void Read(BoxModel box, BoxInputs input, Func<decimal, string> price)
        {
            box.Rows.Add(Section("READ"));

            var facts = input.Facts;

            box.Rows.Add(Row("MIGRATION", Of(input.Migration),
                             Cell(Word(input.Migration, "migrating up", "migrating down", "balanced"),
                                  Of(input.Migration)),
                             facts.HasValue
                                 ? Cell(price(facts.ValueLow) + " - " + price(facts.ValueHigh), Tone.Neutral)
                                 : Cell("no per-price data on this bar", Tone.Warning)));

            box.Rows.Add(EffortRow(input));
            box.Rows.Add(AbsorptionRow(input, price));
            box.Rows.Add(BarRow(input));
            box.Rows.Add(AgreeRow(input));
        }

        private static BoxRow EffortRow(BoxInputs input)
        {
            var verdict = input.Effort;
            var window = "over " + input.EffortWindowBars + " bars";

            switch (verdict.Basis)
            {
                case EffortBasis.Insufficient:
                    return Row("EFFORT", Tone.Muted,
                               Cell("nothing moved far enough to price", Tone.Muted),
                               Cell("", Tone.Muted),
                               Cell(window, Tone.Muted));

                case EffortBasis.OneSided:
                    var only = verdict.Side == Side.Buy ? verdict.UpCost : verdict.DownCost;
                    return Row("EFFORT", Of(verdict.Side),
                               Cell("all progress " + (verdict.Side == Side.Buy ? "up" : "down"), Of(verdict.Side)),
                               Cell(EffortMath.Compact(only) + "/tick, nothing the other way", Tone.Neutral),
                               Cell(window, Tone.Muted));

                default:
                    var costs = "up " + EffortMath.Compact(verdict.UpCost) +
                                " vs down " + EffortMath.Compact(verdict.DownCost) + " per tick";

                    if (verdict.Side == Side.None)
                    {
                        return Row("EFFORT", Tone.Muted,
                                   Cell("even", Tone.Muted),
                                   Cell(costs, Tone.Neutral),
                                   Cell(window, Tone.Muted));
                    }

                    var lead = (verdict.Side == Side.Buy ? "up cheaper" : "down cheaper") +
                               "  " + Multiple(verdict.Ratio);

                    return Row("EFFORT", Of(verdict.Side),
                               Cell(lead, Of(verdict.Side)),
                               Cell(costs, Tone.Neutral),
                               Cell(window, Tone.Muted));
            }
        }

        private static BoxRow AbsorptionRow(BoxInputs input, Func<decimal, string> price)
        {
            if (!input.HasAbsorption)
            {
                return Row("ABSORB", Tone.Muted,
                           Cell("none on the bars in view", Tone.Muted));
            }

            var mark = input.Absorption;
            var side = mark.Absorbed == Side.Buy ? "buyers" : "sellers";

            // Absorbed buyers is bearish information and the other way round, so the tone is the
            // side that WON, not the side named.
            var tone = mark.Absorbed == Side.Buy ? Tone.Bearish : Tone.Bullish;

            return Row("ABSORB", Tone.Warning,
                       Cell(side + " at " + price(mark.Price), tone),
                       Cell(EffortMath.Compact(mark.Volume) + " traded, delta " + Signed(mark.Delta),
                            Tone.Neutral),
                       Cell(Ago(input.LastBar - mark.Bar), Tone.Muted));
        }

        private static BoxRow BarRow(BoxInputs input)
        {
            var facts = input.Facts;

            var result = input.Tick > 0m
                ? Math.Round((facts.Close - facts.Open) / input.Tick)
                : 0m;

            var side = EffortMath.BarSide(facts);

            return Row("BAR", Of(side),
                       Cell("delta " + Signed(facts.Delta) + " of " + EffortMath.Compact(facts.Volume),
                            Of(side)),
                       Cell("closed " + Signed(result) + "t", Of(result > 0m ? Side.Buy
                                                                              : result < 0m ? Side.Sell : Side.None)));
        }

        private static BoxRow AgreeRow(BoxInputs input)
        {
            var bar = EffortMath.BarSide(input.Facts);

            var up = Count(Side.Buy, input.Migration, bar, input.Effort.Side);
            var down = Count(Side.Sell, input.Migration, bar, input.Effort.Side);

            var level = input.Where.Known ? input.Where.Favours : Side.None;
            var counted = input.RequireLevel ? 4 : 3;

            if (input.RequireLevel)
            {
                if (level == Side.Buy) up++;
                if (level == Side.Sell) down++;
            }

            var lead = up > down ? Side.Buy : down > up ? Side.Sell : Side.None;
            var agree = up > down ? up : down;

            if (lead == Side.None)
            {
                return Row("AGREE", Tone.Muted,
                           Cell("no side leads: nothing to act on", Tone.Muted));
            }

            var all = agree == counted;

            return Row("AGREE", Of(lead),
                       Cell(agree + " of " + counted + " point " + (lead == Side.Buy ? "up" : "down"), Of(lead)),
                       Cell(all ? "this is a setup bar"
                                : input.RequireLevel && level != lead
                                    ? "but not from a level"
                                    : "not all of them",
                            all ? Of(lead) : Tone.Muted));
        }

        private static int Count(Side side, Side migration, Side bar, Side effort)
        {
            var count = 0;
            if (migration == side) count++;
            if (bar == side) count++;
            if (effort == side) count++;

            return count;
        }

        #endregion

        #region The trade

        private static void Trade(BoxModel box, BoxInputs input, Func<decimal, string> price)
        {
            var run = input.Run;

            if (run == null)
            {
                box.Headline = "NO TRADE";
                box.HeadlineTone = Tone.Muted;

                box.Rows.Add(Section("TRADE"));
                box.Rows.Add(Row("", Tone.Muted,
                                 Cell("nothing yet: the three layers have not agreed", Tone.Muted)));

                box.Status = "NO SETUP";
                box.StatusTone = Tone.Muted;
                return;
            }

            var setup = run.Setup;
            var buy = setup.Side == Side.Buy;
            var tone = Of(setup.Side);

            box.Headline = buy ? "LONG" : "SHORT";
            box.HeadlineTone = setup.OverCap ? Tone.Muted : tone;

            box.Rows.Add(Section(setup.OverCap ? "TRADE  (over cap -- not taken)" : "TRADE"));

            var labelTone = setup.OverCap ? Tone.Muted : tone;

            box.Rows.Add(Row("SIDE", labelTone,
                             Cell(buy ? "LONG" : "SHORT", labelTone),
                             Cell("bar " + setup.Bar, Tone.Muted),
                             Cell(Ago(input.LastBar - setup.Bar), Tone.Muted)));

            box.Rows.Add(Row("ENTRY", Tone.Muted,
                             Cell(price(setup.Entry), Tone.Neutral),
                             Cell(From(setup.Where), Tone.Muted)));

            var risk = Ticks(setup.Risk, input.Tick);

            box.Rows.Add(Row("STOP", Tone.Muted,
                             Cell(price(setup.Stop), Tone.Bearish),
                             Cell(risk + "t", Tone.Neutral),
                             Cell(Money(risk, input.TickCost), Tone.Neutral)));

            box.Rows.Add(Row("TARGET", Tone.Muted,
                             Cell(price(setup.Target), labelTone),
                             Cell(risk + "t", Tone.Neutral),
                             Cell(Money(risk, input.TickCost), Tone.Neutral),
                             Cell("1R", Tone.Muted)));

            if (!setup.OverCap)
            {
                var locked = buy ? run.Trail - setup.Entry : setup.Entry - run.Trail;
                var lockedTicks = Ticks(locked, input.Tick);

                box.Rows.Add(Row("TRAIL", Tone.Muted,
                                 Cell(price(run.Trail), Tone.Neutral),
                                 Cell(lockedTicks >= 0m ? "+" + lockedTicks + "t locked in"
                                                        : lockedTicks + "t still at risk",
                                      lockedTicks >= 0m ? Tone.Bullish : Tone.Muted)));
            }

            Status(box, input, run);
        }

        /// <summary>What the entry was taken off, in the words of the profile as it stood then.</summary>
        private static string From(Location where)
        {
            if (!where.Known) return "";

            if (where.AtCluster)
            {
                return where.Cluster.Kind == ClusterKind.Absorption
                    ? "off absorbed " + (where.Cluster.Side == Side.Buy ? "buyers" : "sellers")
                    : "off driven " + (where.Cluster.Side == Side.Buy ? "buyers" : "sellers");
            }

            if (where.AtValueLow) return "off value low";
            if (where.AtValueHigh) return "off value high";

            if (where.Zone == Zone.Discount) return "from discount";
            if (where.Zone == Zone.Premium) return "from premium";

            return "no level under it";
        }

        private static void Status(BoxModel box, BoxInputs input, SetupRun run)
        {
            var setup = run.Setup;
            var buy = setup.Side == Side.Buy;
            var bars = input.LastBar - setup.Bar;

            if (setup.OverCap)
            {
                box.Status = "NOT A TRADE  --  the stop is further away than the cap allows, so this is a reading only";
                box.StatusTone = Tone.Muted;
                return;
            }

            if (run.SameBar)
            {
                box.Status = "UNRESOLVED  --  one bar touched both target and stop, and bars do not record which came first";
                box.StatusTone = Tone.Warning;
                return;
            }

            if (run.Live)
            {
                var open = buy ? input.Facts.Close - setup.Entry : setup.Entry - input.Facts.Close;
                var openTicks = Ticks(open, input.Tick);

                var text = "LIVE  " + bars + " bars in  --  open " + Signed(openTicks) + "t";

                var money = Money(openTicks, input.TickCost);
                if (money.Length > 0) text += "  " + money;

                text += run.TargetBar >= 0 ? "  --  target already reached" : "  --  target not reached";

                box.Status = text;
                box.StatusTone = openTicks >= 0m ? Of(setup.Side) : Tone.Warning;
                return;
            }

            var moved = buy ? run.ExitLevel - setup.Entry : setup.Entry - run.ExitLevel;
            var movedTicks = Ticks(moved, input.Tick);

            var closed = "CLOSED on the trail at " + (input.Price ?? (p => p.ToString(CultureInfo.InvariantCulture)))(run.ExitLevel) +
                         "  --  " + Signed(movedTicks) + "t";

            var exitMoney = Money(movedTicks, input.TickCost);
            if (exitMoney.Length > 0) closed += "  " + exitMoney;

            if (run.TargetBar >= 0 && run.TargetBar < run.ExitBar) closed += "  --  after the target was reached";

            box.Status = closed;
            box.StatusTone = movedTicks > 0m ? Tone.Bullish
                                             : movedTicks < 0m ? Tone.Bearish
                                                               : Tone.Muted;
        }

        #endregion

        private static void Warnings(BoxModel box, BoxInputs input)
        {
            if (input.BarsMissingValue > 0)
            {
                box.Warnings.Add(input.BarsMissingValue + " of " + input.BarsInView +
                                 " bars in view carry no per-price data, so their value area is unknown");
            }

            if (!string.IsNullOrEmpty(input.TrackProblem))
                box.Warnings.Add(input.TrackProblem);

            if (input.TickCost <= 0m)
                box.Warnings.Add("no tick value from the platform, so risk is shown in ticks only");

            if (input.Errors == null) return;

            for (var i = 0; i < input.Errors.Count; i++)
                box.Warnings.Add(input.Errors[i]);
        }

        #region Words and numbers

        /// <summary>Ticks, rounded. Zero tick size gives zero rather than dividing by it.</summary>
        public static decimal Ticks(decimal price, decimal tick)
        {
            return tick <= 0m ? 0m : Math.Round(price / tick);
        }

        /// <summary>Money for a number of ticks, or an empty string when the platform has not given a tick cost.</summary>
        public static string Money(decimal ticks, decimal tickCost)
        {
            if (tickCost <= 0m) return "";

            var value = ticks * tickCost;
            var text = Math.Abs(value).ToString("0.00", CultureInfo.InvariantCulture);

            return (value < 0m ? "-$" : "$") + text;
        }

        public static string Signed(decimal value)
        {
            var text = EffortMath.Compact(value);
            return value > 0m ? "+" + text : text;
        }

        public static string Multiple(decimal ratio)
        {
            if (ratio <= 0m) return "";

            return Math.Round(ratio, 1).ToString("0.0", CultureInfo.InvariantCulture) + "x";
        }

        public static string Ago(int bars)
        {
            if (bars <= 0) return "this bar";

            return bars == 1 ? "1 bar ago" : bars + " bars ago";
        }

        private static string Word(Side side, string buy, string sell, string none)
        {
            if (side == Side.Buy) return buy;
            return side == Side.Sell ? sell : none;
        }

        private static Tone Of(Side side)
        {
            if (side == Side.Buy) return Tone.Bullish;
            return side == Side.Sell ? Tone.Bearish : Tone.Muted;
        }

        private static BoxRow Section(string label)
        {
            var row = new BoxRow();
            row.Label = label;
            row.Section = true;
            return row;
        }

        private static BoxRow Row(string label, Tone labelTone, params BoxRow[] cells)
        {
            var row = new BoxRow();
            row.Label = label;
            row.LabelTone = labelTone;

            var text = new List<string>();
            var tones = new List<Tone>();

            for (var i = 0; i < cells.Length; i++)
            {
                text.Add(cells[i].Label);
                tones.Add(cells[i].LabelTone);
            }

            row.Cells = text.ToArray();
            row.Tones = tones.ToArray();
            return row;
        }

        /// <summary>A cell, carried in a row for want of a second tiny type.</summary>
        private static BoxRow Cell(string text, Tone tone)
        {
            var cell = new BoxRow();
            cell.Label = text ?? "";
            cell.LabelTone = tone;
            return cell;
        }

        #endregion
    }
}
