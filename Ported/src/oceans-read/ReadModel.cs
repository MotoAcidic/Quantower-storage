using System;
using System.Collections.Generic;

namespace OceansRead
{
    /// <summary>How a line should be coloured. The model decides tone; the indicator picks the colour.</summary>
    public enum Tone
    {
        Header,
        Neutral,
        Good,
        Bad,
        Warn,
        Muted
    }

    /// <summary>Which of the two books the current condition puts us in.</summary>
    public enum Playbook
    {
        /// <summary>Not enough read to be in either.</summary>
        None,

        /// <summary>Balancing. The edges of value are the trade; the point of control is the target.</summary>
        FadeTheEdge,

        /// <summary>Imbalancing. The move is the trade; the pullback is the entry.</summary>
        GoWithTheMove,

        /// <summary>The measurements disagree, or the day is not readable. No book.</summary>
        StandAside
    }

    /// <summary>What to do about it right now.</summary>
    public enum Stance
    {
        StandAside,
        Wait,
        Prepare,
        Act
    }

    /// <summary>One condition the trade needs, and whether the market is meeting it.</summary>
    public struct Gate
    {
        public string Name;
        public bool Passed;
        public string Detail;

        public Gate(string name, bool passed, string detail)
        {
            Name = name;
            Passed = passed;
            Detail = detail;
        }
    }

    /// <summary>One printed line of the read.</summary>
    public struct ReadLine
    {
        public string Label;
        public string Text;
        public Tone Tone;

        public ReadLine(string label, string text, Tone tone)
        {
            Label = label;
            Text = text;
            Tone = tone;
        }
    }

    /// <summary>Everything the read is assembled from. Filled by the indicator, read here.</summary>
    public sealed class ReadInputs
    {
        public DateTime Now;
        public string ZoneAbbrev = string.Empty;
        public string ClockHow = string.Empty;
        public string ClockError;

        public decimal TickSize;
        public decimal Price;
        public string SessionLabel = string.Empty;
        public bool RegularHours;
        public bool PowerHour;

        public AuctionRead Auction = new AuctionRead();
        public ReadProfile Session;
        public ReadProfile Prior;
        public bool PriorComplete;

        public Rhythm Rhythm = new Rhythm();
        public LegRead Leg;

        public Sequence Sequence = Sequence.TooShort;
        public WaveCount Wave = new WaveCount();
        public CorrectionRead Correction = new CorrectionRead();

        public string VwapText = string.Empty;
        public int VwapAbove;
        public int VwapBelow;
        public int VwapStack;            // +1 stacked up, -1 stacked down, 0 broken
        public decimal SessionVwap;

        public AcceptanceTest Acceptance;
        public AcceptanceTest LastAcceptance;

        public OrderflowRead Flow = new OrderflowRead();

        public decimal OvernightHigh;
        public decimal OvernightLow;
        public bool SinglePrintsOpen;
        public bool PoorHigh;
        public bool PoorLow;

        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>The assembled read: the lines to print, the gates, and the stance.</summary>
    public sealed class ReadBox
    {
        public readonly List<ReadLine> Lines = new List<ReadLine>();
        public readonly List<Gate> Gates = new List<Gate>();

        public Playbook Playbook = Playbook.None;
        public Stance Stance = Stance.StandAside;
        public string StanceWhy = string.Empty;
        public int Direction;            // +1 long, -1 short, 0 none

        public void Add(string label, string text, Tone tone) => Lines.Add(new ReadLine(label, text, tone));
    }

    /// <summary>
    /// Assembles every measurement into one read, in the order the method reads them:
    /// condition first, then location, then structure, then rhythm, then value, then
    /// acceptance, and orderflow last -- because orderflow confirms a read, it does not
    /// produce one.
    ///
    /// The stance at the bottom is a checklist, not a signal. Every gate is named and printed
    /// with its own measurement, so a WAIT says exactly which condition is missing and an ACT
    /// can be argued with.
    /// </summary>
    public static class ReadModel
    {
        public static ReadBox Build(ReadInputs input)
        {
            var box = new ReadBox();

            if (input == null)
            {
                box.Add("READ", "nothing to read", Tone.Bad);
                return box;
            }

            var header = "OCEANS READ";
            if (input.SessionLabel.Length > 0) header += "   " + input.SessionLabel;
            box.Add(header, input.Now.ToString("HH:mm:ss") +
                            (input.ZoneAbbrev.Length > 0 ? " " + input.ZoneAbbrev : ""), Tone.Header);

            if (input.ClockError != null)
            {
                box.Add("CLOCK", input.ClockError, Tone.Bad);
                box.Stance = Stance.StandAside;
                box.StanceWhy = "the bar clock is unresolved, so every level would be misplaced";

                return box;
            }

            ConditionSection(box, input);
            Location(box, input);
            Structure(box, input);
            RhythmSection(box, input);
            ValueSection(box, input);
            Acceptance(box, input);
            Flow(box, input);
            Decide(box, input);

            foreach (var note in input.Notes) box.Add("!", note, Tone.Bad);

            return box;
        }

        private static void ConditionSection(ReadBox box, ReadInputs input)
        {
            var auction = input.Auction;

            var tone = auction.Condition == Condition.Imbalancing ? Tone.Warn
                     : auction.Condition == Condition.Balancing ? Tone.Neutral
                     : Tone.Muted;

            box.Add("CONDITION", Name(auction.Condition) + "  --  " + auction.ConditionWhy, tone);

            var day = AuctionMath.Describe(auction.DayType);
            if (auction.DayWhy.Length > 0) day += " (" + auction.DayWhy + ")";
            box.Add("", "day so far: " + day, Tone.Neutral);

            box.Add("", "profile: " + AuctionMath.Describe(auction.Shape), Tone.Neutral);

            if (auction.Ib.Set)
            {
                box.Add("", "IB " + Price(auction.Ib.Low, input) + " - " + Price(auction.Ib.High, input) +
                            "   RE up " + Two(auction.ExtensionUp) + "x  down " + Two(auction.ExtensionDown) + "x",
                        Tone.Muted);
            }
        }

        private static void Location(ReadBox box, ReadInputs input)
        {
            var session = input.Session;

            if (session == null || session.PocIndex < 0)
            {
                box.Add("LOCATION", "no session profile yet", Tone.Muted);
            }
            else
            {
                var where = input.Price > session.Vah ? "above value"
                          : input.Price < session.Val ? "below value"
                          : "inside value";

                box.Add("LOCATION", where + "   VAH " + Price(session.Vah, input) +
                                    "  POC " + Price(session.Poc, input) +
                                    "  VAL " + Price(session.Val, input),
                        input.Price > session.Vah || input.Price < session.Val ? Tone.Warn : Tone.Neutral);
            }

            if (input.Prior != null && input.Prior.PocIndex >= 0)
            {
                box.Add("", "prior: VAH " + Price(input.Prior.Vah, input) +
                            "  POC " + Price(input.Prior.Poc, input) +
                            "  VAL " + Price(input.Prior.Val, input) +
                            (input.PriorComplete ? "" : "  PARTIAL"),
                        input.PriorComplete ? Tone.Muted : Tone.Warn);
            }
            else
            {
                box.Add("", "no completed prior session in the loaded history", Tone.Warn);
            }

            if (input.OvernightHigh > 0m && input.OvernightLow > 0m)
            {
                box.Add("", "overnight " + Price(input.OvernightLow, input) + " - " +
                            Price(input.OvernightHigh, input), Tone.Muted);
            }

            var stack = input.VwapStack > 0 ? "stacked up"
                      : input.VwapStack < 0 ? "stacked down"
                      : "broken stack";

            box.Add("VWAP", input.VwapText + "   " + stack + "   " +
                            input.VwapAbove + " above / " + input.VwapBelow + " below",
                    input.VwapStack == 0 ? Tone.Warn : Tone.Neutral);
        }

        private static void Structure(ReadBox box, ReadInputs input)
        {
            box.Add("STRUCTURE", WaveMath.Describe(input.Sequence),
                    input.Sequence == Sequence.Impulsive ? Tone.Warn : Tone.Neutral);

            box.Add("", "count: " + input.Wave.Verdict, input.Wave.Valid ? Tone.Good : Tone.Muted);

            if (input.Wave.Valid)
            {
                box.Add("", "w2 " + Pct(input.Wave.Wave2Retrace) +
                            "  w3 " + Two(input.Wave.Wave3Extension) + "x" +
                            "  w4 " + Pct(input.Wave.Wave4Retrace), Tone.Muted);

                if (input.Correction.Present)
                {
                    box.Add("", "correction " + input.Correction.Kind + ", " +
                                Pct(input.Correction.Retrace) + " retraced", Tone.Muted);
                }
            }
            else
            {
                // Every rule is printed, not just the broken one: knowing that three of four
                // held is a different picture from knowing one failed.
                foreach (var rule in input.Wave.Rules)
                    box.Add("", (rule.Passed ? "  ok  " : "  no  ") + rule.Name + " -- " + rule.Detail,
                            rule.Passed ? Tone.Muted : Tone.Bad);
            }
        }

        private static void RhythmSection(ReadBox box, ReadInputs input)
        {
            var rhythm = input.Rhythm;

            if (!rhythm.Valid)
            {
                box.Add("RHYTHM", "not enough completed rotations to measure", Tone.Warn);
                return;
            }

            var text = "rotation " + Format.Ticks(rhythm.MedianTicks) + " / " +
                       Format.Minutes(rhythm.MedianMinutes) + " median over " + rhythm.Legs + " legs";

            if (rhythm.Lopsided)
                text += "   (up " + Format.Ticks(rhythm.MedianUpTicks) +
                        ", down " + Format.Ticks(rhythm.MedianDownTicks) + ")";

            box.Add("RHYTHM", text, Tone.Neutral);

            if (!input.Leg.Active)
            {
                box.Add("", "no leg in progress", Tone.Muted);
                return;
            }

            var maturity = Name(input.Leg.Maturity);
            var tone = input.Leg.Maturity == LegMaturity.AtTheTurn ? Tone.Good
                     : input.Leg.Maturity == LegMaturity.Beyond ? Tone.Bad
                     : input.Leg.Maturity == LegMaturity.Extended ? Tone.Warn
                     : Tone.Neutral;

            box.Add("", "this leg " + (input.Leg.Up ? "up " : "down ") +
                        Format.Ticks(input.Leg.Ticks) + " / " + Format.Minutes(input.Leg.Minutes) +
                        "  --  " + maturity +
                        " (" + Format.Percent(input.Leg.TickPercentile) + " of size, " +
                        Format.Percent(input.Leg.MinutePercentile) + " of time)",
                    tone);
        }

        private static void ValueSection(ReadBox box, ReadInputs input)
        {
            var auction = input.Auction;

            var relation = AuctionMath.Describe(auction.Relation);
            box.Add("VALUE", "developing value " + relation + " vs prior" +
                             "   overlap " + Format.Percent((double)auction.ValueOverlap),
                    auction.Relation == ValueRelation.Unknown ? Tone.Muted : Tone.Neutral);

            var migration = auction.ValueMigration;
            if (migration.Set)
            {
                box.Add("", "point of control " + migration.Direction + " " +
                            Format.Ticks(Math.Abs(migration.Ticks)) + " over " +
                            Format.Minutes(migration.Minutes),
                        Tone.Muted);
            }
            else
            {
                box.Add("", "point of control has not moved enough to call", Tone.Muted);
            }

            var flags = new List<string>();
            if (input.PoorHigh) flags.Add("poor high");
            if (input.PoorLow) flags.Add("poor low");
            if (input.SinglePrintsOpen) flags.Add("single prints open");

            if (flags.Count > 0) box.Add("", string.Join(", ", flags) + " -- unfinished business", Tone.Warn);
        }

        private static void Acceptance(ReadBox box, ReadInputs input)
        {
            var test = input.Acceptance;

            if (test == null)
            {
                var last = input.LastAcceptance;
                if (last == null)
                {
                    box.Add("ACCEPTANCE", "nothing under test", Tone.Muted);
                }
                else
                {
                    box.Add("ACCEPTANCE", "last: " + last.Name + " " + (last.Up ? "up" : "down") +
                                          " -- " + Name(last.State) + ", " + last.Why,
                            last.State == AcceptanceState.Accepted ? Tone.Good : Tone.Bad);
                }

                return;
            }

            box.Add("ACCEPTANCE", "testing " + test.Name + " " + Price(test.Price, input) +
                                  " " + (test.Up ? "up" : "down") +
                                  ", broke " + test.BrokeAt.ToString("HH:mm"),
                    Tone.Warn);

            box.Add("", "time " + Format.Minutes(test.MinutesBeyond) + " of " +
                        Format.Minutes(test.MinutesNeeded) +
                        "   ground " + Format.Ticks(test.MaxGroundTicks) + " of " +
                        Format.Ticks(test.GroundNeeded) +
                        (test.ValueFollowed ? "   value has followed" : "   value has not followed"),
                    Tone.Neutral);
        }

        private static void Flow(ReadBox box, ReadInputs input)
        {
            var flow = input.Flow;

            if (!flow.HaveFootprint && flow.Oi == OiRead.None)
            {
                box.Add("ORDERFLOW", "no footprint or open interest on this chart", Tone.Warn);
                return;
            }

            box.Add("ORDERFLOW", "session delta " + Format.Signed(flow.SessionDelta) +
                                 "   bar " + Format.Signed(flow.BarDelta) +
                                 "   leg " + Format.Signed(flow.LegDelta),
                    Tone.Neutral);

            if (flow.AtLevel.Have)
            {
                box.Add("", "at " + flow.LevelName + ": " +
                            Format.Percent(flow.AtLevel.AskShare) + " buyers, " +
                            Math.Round(flow.AtLevel.Volume).ToString("#,##0") + " traded" +
                            (flow.Absorption ? "   ABSORPTION -- " + flow.AbsorptionWhy : ""),
                        flow.Absorption ? Tone.Bad : Tone.Muted);
            }

            if (flow.Oi != OiRead.None)
            {
                box.Add("", "open interest " + Format.Signed(flow.OiChange) + " -- " +
                            OrderflowMath.Describe(flow.Oi), Tone.Muted);
            }

            if (flow.Divergence) box.Add("", flow.DivergenceWhy, Tone.Bad);

            box.Add("", OrderflowMath.Describe(flow.Verdict) + " -- " + flow.Why,
                    flow.Verdict == FlowVerdict.Confirms ? Tone.Good
                  : flow.Verdict == FlowVerdict.Diverges ? Tone.Bad
                  : Tone.Neutral);
        }

        /// <summary>
        /// The checklist. Which book we are in comes from the condition; the gates are that
        /// book's own conditions, and the stance is how many of them the market is meeting.
        ///
        /// Nothing here is a signal. Every gate carries the measurement it was judged on, so a
        /// WAIT names the missing condition and an ACT can be argued with.
        /// </summary>
        private static void Decide(ReadBox box, ReadInputs input)
        {
            var auction = input.Auction;

            if (!input.Rhythm.Valid)
            {
                box.Playbook = Playbook.StandAside;
                box.Stance = Stance.StandAside;
                box.StanceWhy = "no measured rhythm, so nothing can be called early or late";
                Verdict(box);

                return;
            }

            switch (auction.Condition)
            {
                case Condition.Balancing:
                    box.Playbook = Playbook.FadeTheEdge;
                    FadeGates(box, input);
                    break;

                case Condition.Imbalancing:
                    box.Playbook = Playbook.GoWithTheMove;
                    GoGates(box, input);
                    break;

                default:
                    box.Playbook = Playbook.StandAside;
                    box.Stance = Stance.StandAside;
                    box.StanceWhy = auction.Condition == Condition.Transitioning
                                  ? "the measurements disagree -- balance and travel are saying different things"
                                  : "not enough of the session to read a condition";
                    Verdict(box);

                    return;
            }

            var failed = 0;
            string firstMissing = null;

            foreach (var gate in box.Gates)
            {
                if (gate.Passed) continue;

                failed++;
                if (firstMissing == null) firstMissing = gate.Name;
            }

            if (failed == 0)
            {
                box.Stance = Stance.Act;
                box.StanceWhy = "every condition met " + (box.Direction > 0 ? "long" : "short");
            }
            else if (failed == 1)
            {
                box.Stance = Stance.Prepare;
                box.StanceWhy = "waiting on: " + firstMissing;
            }
            else
            {
                box.Stance = Stance.Wait;
                box.StanceWhy = failed + " conditions missing, first is: " + firstMissing;
            }

            if (box.Direction == 0)
            {
                box.Stance = Stance.Wait;
                box.StanceWhy = "no side to take yet";
            }

            Verdict(box);
        }

        /// <summary>
        /// Balance: the edges are the trade. We want price at an extreme of value, a rotation
        /// that has already run its usual distance, and the tape failing at the edge.
        /// </summary>
        private static void FadeGates(ReadBox box, ReadInputs input)
        {
            var session = input.Session;
            if (session == null || session.PocIndex < 0)
            {
                box.Gates.Add(new Gate("a session profile", false, "none built yet"));
                return;
            }

            var aboveValue = input.Price >= session.Vah;
            var belowValue = input.Price <= session.Val;

            box.Direction = aboveValue ? -1 : belowValue ? 1 : 0;

            box.Gates.Add(new Gate("price at an edge of value", aboveValue || belowValue,
                aboveValue ? "at or above the value area high"
              : belowValue ? "at or below the value area low"
              : "inside value -- nothing to fade"));

            var mature = input.Leg.Active &&
                        (input.Leg.Maturity == LegMaturity.Extended || input.Leg.Maturity == LegMaturity.Beyond);

            box.Gates.Add(new Gate("the rotation has run its distance", mature,
                input.Leg.Active
                    ? Format.Ticks(input.Leg.Ticks) + " of a typical " + Format.Ticks(input.Rhythm.MedianTicks) +
                      " -- " + Name(input.Leg.Maturity)
                    : "no leg in progress"));

            var againstEdge = box.Direction != 0 && input.Leg.Active && input.Leg.Up == (box.Direction < 0);

            box.Gates.Add(new Gate("the leg is running into the edge", againstEdge,
                input.Leg.Active
                    ? "leg is " + (input.Leg.Up ? "up" : "down") + " into the " + (aboveValue ? "high" : "low")
                    : "no leg in progress"));

            var tapeFails = input.Flow.Absorption || input.Flow.Divergence ||
                            input.Flow.Verdict == FlowVerdict.Diverges;

            box.Gates.Add(new Gate("the tape is failing at the edge", tapeFails,
                input.Flow.HaveFootprint || input.Flow.Oi != OiRead.None
                    ? input.Flow.Why
                    : "no footprint or open interest to check"));

            // Accepting through the edge is the opposite of a fade, so a live acceptance test
            // going the other way vetoes the whole book rather than counting as one failed gate.
            var accepting = input.Acceptance != null &&
                            input.Acceptance.MaxGroundTicks >= input.Acceptance.GroundNeeded;

            box.Gates.Add(new Gate("value is not breaking out", !accepting,
                accepting ? "price is accepting through " + input.Acceptance.Name : "no break in progress"));
        }

        /// <summary>
        /// Imbalance: the move is the trade. We want acceptance in the direction, a pullback
        /// that has finished, and the tape behind it.
        /// </summary>
        private static void GoGates(ReadBox box, ReadInputs input)
        {
            var direction = 0;

            var accepted = input.LastAcceptance != null &&
                           input.LastAcceptance.State == AcceptanceState.Accepted;

            if (accepted) direction = input.LastAcceptance.Up ? 1 : -1;
            else if (input.Auction.ValueMigration.Set && input.Auction.ValueMigration.Ticks != 0m)
                direction = input.Auction.ValueMigration.Ticks > 0m ? 1 : -1;

            box.Direction = direction;

            box.Gates.Add(new Gate("a side to take", direction != 0,
                accepted ? "accepted " + (direction > 0 ? "above " : "below ") + input.LastAcceptance.Name
              : direction != 0 ? "value migrating " + input.Auction.ValueMigration.Direction
              : "nothing has been accepted and value has not moved"));

            box.Gates.Add(new Gate("acceptance established", accepted,
                accepted ? input.LastAcceptance.Name + ": " + input.LastAcceptance.Why
              : input.Acceptance != null
                    ? input.Acceptance.Name + " still under test -- " +
                      Format.Percent(Math.Min(input.Acceptance.TimeProgress, input.Acceptance.GroundProgress)) +
                      " of the way"
                    : "no level has been broken and held"));

            // Joining a move means entering on the pullback, not on the extension. The turn is
            // where the risk is small; three quarters of the way through a rotation it is not.
            var early = input.Leg.Active &&
                       (input.Leg.Maturity == LegMaturity.AtTheTurn || input.Leg.Maturity == LegMaturity.Developing);

            box.Gates.Add(new Gate("entering at the turn, not late", early,
                input.Leg.Active
                    ? Format.Ticks(input.Leg.Ticks) + " into a typical " + Format.Ticks(input.Rhythm.MedianTicks) +
                      " -- " + Name(input.Leg.Maturity)
                    : "no leg in progress"));

            var withStack = direction != 0 &&
                           ((direction > 0 && input.VwapAbove > input.VwapBelow) ||
                            (direction < 0 && input.VwapBelow > input.VwapAbove));

            box.Gates.Add(new Gate("the VWAP stack agrees", withStack,
                input.VwapText + " -- " + input.VwapAbove + " above, " + input.VwapBelow + " below"));

            var confirms = input.Flow.Verdict == FlowVerdict.Confirms;

            box.Gates.Add(new Gate("orderflow confirms", confirms,
                input.Flow.HaveFootprint || input.Flow.Oi != OiRead.None
                    ? OrderflowMath.Describe(input.Flow.Verdict) + " -- " + input.Flow.Why
                    : "no footprint or open interest to check"));
        }

        private static void Verdict(ReadBox box)
        {
            box.Add("", string.Empty, Tone.Muted);
            box.Add("PLAYBOOK", Name(box.Playbook), Tone.Neutral);

            foreach (var gate in box.Gates)
                box.Add("", (gate.Passed ? "  ok  " : "  no  ") + gate.Name + " -- " + gate.Detail,
                        gate.Passed ? Tone.Good : Tone.Bad);

            var side = box.Direction > 0 ? " LONG" : box.Direction < 0 ? " SHORT" : "";

            box.Add("READ", Name(box.Stance) + side + "  --  " + box.StanceWhy,
                    box.Stance == Stance.Act ? Tone.Good
                  : box.Stance == Stance.Prepare ? Tone.Warn
                  : Tone.Muted);
        }

        private static string Name(Condition condition)
        {
            switch (condition)
            {
                case Condition.Balancing: return "BALANCING";
                case Condition.Imbalancing: return "IMBALANCING";
                case Condition.Transitioning: return "TRANSITIONING";
                default: return "UNREAD";
            }
        }

        private static string Name(LegMaturity maturity)
        {
            switch (maturity)
            {
                case LegMaturity.AtTheTurn: return "at the turn";
                case LegMaturity.Developing: return "developing";
                case LegMaturity.Extended: return "extended";
                case LegMaturity.Beyond: return "beyond the usual rotation";
                default: return "unmeasured";
            }
        }

        private static string Name(AcceptanceState state)
        {
            switch (state)
            {
                case AcceptanceState.Accepted: return "ACCEPTED";
                case AcceptanceState.Rejected: return "REJECTED";
                case AcceptanceState.Testing: return "testing";
                default: return "none";
            }
        }

        private static string Name(Playbook playbook)
        {
            switch (playbook)
            {
                case Playbook.FadeTheEdge: return "FADE THE EDGE -- balance, target the point of control";
                case Playbook.GoWithTheMove: return "GO WITH THE MOVE -- imbalance, enter the pullback";
                case Playbook.StandAside: return "STAND ASIDE";
                default: return "none";
            }
        }

        private static string Name(Stance stance)
        {
            switch (stance)
            {
                case Stance.Act: return "ACT";
                case Stance.Prepare: return "PREPARE";
                case Stance.Wait: return "WAIT";
                default: return "STAND ASIDE";
            }
        }

        private static string Price(decimal price, ReadInputs input)
        {
            if (price <= 0m) return "--";

            var decimals = Decimals(input.TickSize);

            return price.ToString("F" + decimals);
        }

        /// <summary>Decimal places implied by the tick size, so prices print the way the ladder does.</summary>
        public static int Decimals(decimal tickSize)
        {
            if (tickSize <= 0m) return 2;

            var decimals = 0;
            var value = tickSize;

            while (value != Math.Floor(value) && decimals < 8)
            {
                value *= 10m;
                decimals++;
            }

            return decimals;
        }

        private static string Two(decimal value) => Math.Round(value, 2).ToString("0.##");
        private static string Pct(decimal fraction) => Math.Round(fraction * 100m) + "%";
    }
}
