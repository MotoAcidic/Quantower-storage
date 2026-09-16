using System;
using System.Collections.Generic;

namespace OceansDom
{
    /// <summary>One price of resting size on one side of the book.</summary>
    public struct BookLevel
    {
        public decimal Price;
        public decimal Size;
        public bool IsAsk;

        public BookLevel(decimal price, decimal size, bool isAsk)
        {
            Price = price;
            Size = size;
            IsAsk = isAsk;
        }
    }

    /// <summary>Volume that actually traded at one price, split by which side of the book it hit.</summary>
    public struct TapePrint
    {
        public decimal Price;
        public decimal Bid;
        public decimal Ask;

        public TapePrint(decimal price, decimal bid, decimal ask)
        {
            Price = price;
            Bid = bid;
            Ask = ask;
        }
    }

    /// <summary>How the weight of a bar's prints falls off as the bar ages out of the window.</summary>
    public enum TapeDecay
    {
        /// <summary>Every bar in the window counts the same. A hard edge, but the plainest.</summary>
        Flat,

        /// <summary>Full weight on the newest bar falling straight to nothing at the window edge.</summary>
        Linear,

        /// <summary>Halves every half-window. Recent trade dominates without the window edge showing.</summary>
        HalfLife
    }

    /// <summary>
    /// Where a level stands relative to price. These are the only five things the ladder can say,
    /// and every one of them is a measurement -- no state here is a forecast.
    /// </summary>
    public enum LevelState
    {
        /// <summary>Price is nowhere near it. The screen has nothing to say.</summary>
        Quiet,

        /// <summary>Price is closing on it but has not reached it.</summary>
        Approach,

        /// <summary>Price is inside the level's band right now.</summary>
        Test,

        /// <summary>Price entered the band and left again on the side it came from.</summary>
        Reject,

        /// <summary>Price went through the band and out the far side. The one that says you are wrong.</summary>
        Break
    }

    /// <summary>How much of the salience budget a state is allowed to spend.</summary>
    public enum Loudness
    {
        Silent,
        Soft,
        Loud
    }

    /// <summary>A price worth watching, with the name it goes by on the chart.</summary>
    public struct TrackedLevel
    {
        public string Name;
        public decimal Price;

        public TrackedLevel(string name, decimal price)
        {
            Name = name;
            Price = price;
        }
    }

    /// <summary>The tick distances that turn a raw price into a <see cref="LevelState"/>.</summary>
    public struct LevelTuning
    {
        public decimal TickSize;

        /// <summary>Inside this many ticks the level is being tested.</summary>
        public int BandTicks;

        /// <summary>Inside this many ticks price is approaching.</summary>
        public int ApproachTicks;

        /// <summary>Ticks beyond the band, on the far side, before a break is called.</summary>
        public int BreakTicks;

        /// <summary>Ticks back beyond the band, on the near side, before a rejection is called.</summary>
        public int RejectTicks;

        /// <summary>How long a break or a rejection stays on screen after it happens.</summary>
        public int LatchMs;

        public static LevelTuning Default(decimal tickSize)
        {
            var tuning = new LevelTuning();
            tuning.TickSize = tickSize;
            tuning.BandTicks = 4;
            tuning.ApproachTicks = 20;
            tuning.BreakTicks = 8;
            tuning.RejectTicks = 8;
            tuning.LatchMs = 20000;
            return tuning;
        }
    }

    /// <summary>One row of the ladder: the same price seen as resting size and as traded size.</summary>
    public struct LadderRow
    {
        public int Index;
        public decimal BidRest;
        public decimal AskRest;
        public decimal TapeBid;
        public decimal TapeAsk;

        public decimal Rest(bool isAsk) { return isAsk ? AskRest : BidRest; }
        public decimal Tape(bool isAsk) { return isAsk ? TapeAsk : TapeBid; }
        public decimal TapeTotal { get { return TapeBid + TapeAsk; } }
    }

    /// <summary>
    /// The price-to-row mapping for the ladder.
    ///
    /// Rows are anchored in PRICE space, never to the bottom of the window. Anchoring to whatever
    /// price happens to be at the bottom edge reshuffles which ticks share a row every time the
    /// chart scrolls, and the whole ladder shimmers.
    /// </summary>
    public sealed class LadderGrid
    {
        public decimal Anchor;
        public decimal RowSize;
        public int Count;

        /// <summary>How many ticks have to be merged for a row to clear the minimum height.</summary>
        public static int TicksPerRow(decimal pixelsPerTick, int minRowPixels)
        {
            if (pixelsPerTick <= 0m) return 1;
            if (minRowPixels <= 0) return 1;
            if (pixelsPerTick >= minRowPixels) return 1;

            var needed = minRowPixels / pixelsPerTick;
            var ticks = (int)Math.Ceiling(needed);
            return ticks < 1 ? 1 : ticks;
        }

        /// <summary>Null when the geometry is nonsense or the row count runs away.</summary>
        public static LadderGrid Build(decimal low, decimal high, decimal tickSize, int ticksPerRow, int maxRows)
        {
            if (tickSize <= 0m) return null;
            if (ticksPerRow < 1) ticksPerRow = 1;
            if (high < low)
            {
                var swap = low;
                low = high;
                high = swap;
            }

            var rowSize = tickSize * ticksPerRow;
            var anchor = Math.Floor(low / rowSize) * rowSize;
            var count = (int)Math.Floor((high - anchor) / rowSize) + 1;
            if (count < 1) count = 1;
            if (maxRows > 0 && count > maxRows) return null;

            var grid = new LadderGrid();
            grid.Anchor = anchor;
            grid.RowSize = rowSize;
            grid.Count = count;
            return grid;
        }

        /// <summary>The row containing this price, or -1 when it falls outside the ladder.</summary>
        public int IndexOf(decimal price)
        {
            if (RowSize <= 0m) return -1;

            var index = (int)Math.Floor((price - Anchor) / RowSize);
            if (index < 0 || index >= Count) return -1;
            return index;
        }

        public decimal Low(int index) { return Anchor + RowSize * index; }
        public decimal High(int index) { return Anchor + RowSize * (index + 1); }
        public decimal Mid(int index) { return Anchor + RowSize * index + RowSize / 2m; }
    }

    /// <summary>
    /// Traded volume per price over a window of bars, each bar weighted by its age.
    ///
    /// It is rebuilt from the bars every frame rather than accumulated from the live tape on
    /// purpose: an accumulator silently loses whatever arrived while the feed was reconnecting,
    /// and the hole never shows. Rebuilding cannot drift from what the bars actually hold.
    /// </summary>
    public sealed class TapeBook
    {
        private readonly Dictionary<decimal, decimal[]> _at = new Dictionary<decimal, decimal[]>();

        public int PriceCount { get { return _at.Count; } }

        public void Clear() { _at.Clear(); }

        /// <summary>Weight of a bar that is <paramref name="ageBars"/> back from the newest one.</summary>
        public static decimal Weight(int ageBars, int windowBars, TapeDecay decay)
        {
            if (windowBars <= 0) return 0m;
            if (ageBars < 0) return 0m;
            if (ageBars >= windowBars) return 0m;

            if (decay == TapeDecay.Flat) return 1m;
            if (decay == TapeDecay.Linear) return (decimal)(windowBars - ageBars) / windowBars;

            var half = windowBars / 2m;
            if (half <= 0m) return 1m;
            return (decimal)Math.Pow(0.5, (double)ageBars / (double)half);
        }

        public void Add(decimal price, decimal bid, decimal ask, decimal weight)
        {
            if (weight <= 0m) return;
            if (bid <= 0m && ask <= 0m) return;

            decimal[] cell;
            if (!_at.TryGetValue(price, out cell))
            {
                cell = new decimal[2];
                _at[price] = cell;
            }

            if (bid > 0m) cell[0] += bid * weight;
            if (ask > 0m) cell[1] += ask * weight;
        }

        public void AddBar(IList<TapePrint> prints, int ageBars, int windowBars, TapeDecay decay)
        {
            if (prints == null) return;

            var weight = Weight(ageBars, windowBars, decay);
            if (weight <= 0m) return;

            for (var i = 0; i < prints.Count; i++)
                Add(prints[i].Price, prints[i].Bid, prints[i].Ask, weight);
        }

        public bool TryGet(decimal price, out decimal bid, out decimal ask)
        {
            decimal[] cell;
            if (_at.TryGetValue(price, out cell))
            {
                bid = cell[0];
                ask = cell[1];
                return true;
            }

            bid = 0m;
            ask = 0m;
            return false;
        }

        public IEnumerable<KeyValuePair<decimal, decimal[]>> All { get { return _at; } }
    }

    /// <summary>
    /// Tracks one level and reports which of the five states it is in.
    ///
    /// The side price approached from is remembered, because that is the only thing that tells a
    /// break apart from a rejection: same distance, same band, opposite meaning.
    /// </summary>
    public sealed class LevelWatch
    {
        public string Name;
        public decimal Price;

        private LevelState _state = LevelState.Quiet;
        private int _entrySide;          // -1 came from below, +1 came from above, 0 not engaged
        private int _lastSide;
        private LevelState _latched = LevelState.Quiet;
        private long _latchUntilMs;

        public LevelWatch(string name, decimal price)
        {
            Name = name;
            Price = price;
        }

        public LevelState State { get { return _state; } }

        /// <summary>The side price is on now: -1 below the level, +1 above, 0 exactly on it.</summary>
        public int Side { get { return _lastSide; } }

        public void Reset()
        {
            _state = LevelState.Quiet;
            _entrySide = 0;
            _lastSide = 0;
            _latched = LevelState.Quiet;
            _latchUntilMs = 0;
        }

        public LevelState Update(decimal price, long nowMs, LevelTuning tuning)
        {
            if (tuning.TickSize <= 0m)
            {
                _state = LevelState.Quiet;
                return _state;
            }

            var ticks = (price - Price) / tuning.TickSize;
            var side = Math.Sign(ticks);
            var away = Math.Abs(ticks);

            var inBand = away <= tuning.BandTicks;
            var inApproach = away <= tuning.ApproachTicks;

            if (inBand)
            {
                // Remember which side we walked in from; entering twice does not overwrite it.
                if (_entrySide == 0) _entrySide = _lastSide != 0 ? _lastSide : side;
                _latched = LevelState.Quiet;
                _latchUntilMs = 0;
                _state = LevelState.Test;
            }
            else if (_entrySide != 0)
            {
                var brokeOut = away >= tuning.BandTicks + tuning.BreakTicks && side == -_entrySide;
                var rejected = away >= tuning.BandTicks + tuning.RejectTicks && side == _entrySide;

                if (brokeOut || rejected)
                {
                    _latched = brokeOut ? LevelState.Break : LevelState.Reject;
                    _latchUntilMs = nowMs + tuning.LatchMs;
                    _entrySide = 0;
                    _state = _latched;
                }
                else
                {
                    // Left the band but not far enough to mean anything yet.
                    _state = inApproach ? LevelState.Approach : LevelState.Quiet;
                }
            }
            else if (nowMs < _latchUntilMs)
            {
                _state = _latched;
            }
            else
            {
                _latched = LevelState.Quiet;
                _state = inApproach ? LevelState.Approach : LevelState.Quiet;
            }

            if (side != 0) _lastSide = side;
            return _state;
        }
    }

    /// <summary>
    /// The salience budget, enforced in code rather than left to the person drawing.
    ///
    /// Exactly one thing on the ladder may be loud at a time, and it is always the thing that says
    /// the trade is wrong. Spending the loud channel on confirmation is what marries you to a
    /// position, so <see cref="For"/> refuses to hand it to anything but a break.
    /// </summary>
    public static class Salience
    {
        public static Loudness For(LevelState state)
        {
            if (state == LevelState.Break) return Loudness.Loud;
            if (state == LevelState.Test || state == LevelState.Reject) return Loudness.Soft;
            return Loudness.Silent;
        }

        public static int Rank(LevelState state)
        {
            switch (state)
            {
                case LevelState.Break: return 4;
                case LevelState.Reject: return 3;
                case LevelState.Test: return 2;
                case LevelState.Approach: return 1;
                default: return 0;
            }
        }

        /// <summary>The one level allowed to draw loud, or -1 when nothing has earned it.</summary>
        public static int LoudIndex(IList<LevelState> states)
        {
            if (states == null) return -1;

            var best = -1;
            var bestRank = 0;
            for (var i = 0; i < states.Count; i++)
            {
                if (For(states[i]) != Loudness.Loud) continue;

                var rank = Rank(states[i]);
                if (rank > bestRank)
                {
                    bestRank = rank;
                    best = i;
                }
            }

            return best;
        }

        /// <summary>The level the state chip speaks for: highest rank, first one on a tie.</summary>
        public static int PrimaryIndex(IList<LevelState> states)
        {
            if (states == null) return -1;

            var best = -1;
            var bestRank = 0;
            for (var i = 0; i < states.Count; i++)
            {
                var rank = Rank(states[i]);
                if (rank > bestRank)
                {
                    bestRank = rank;
                    best = i;
                }
            }

            return best;
        }
    }

    /// <summary>What a line of the levels setting turned into.</summary>
    public sealed class LevelParse
    {
        public readonly List<TrackedLevel> Levels = new List<TrackedLevel>();

        /// <summary>The entries that could not be read, kept so the chart can say so out loud.</summary>
        public readonly List<string> Unreadable = new List<string>();
    }

    /// <summary>
    /// Turns the levels setting into prices.
    ///
    /// Anything it cannot read is collected rather than skipped. A level that silently vanishes
    /// because of a typo is a level you think you are watching and are not.
    /// </summary>
    public static class LevelParser
    {
        private static readonly char[] Separators = { ',', ';', '\n', '\r' };

        public static LevelParse Parse(string text)
        {
            var parsed = new LevelParse();
            if (string.IsNullOrWhiteSpace(text)) return parsed;

            var entries = text.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i].Trim();
                if (entry.Length == 0) continue;

                string name;
                string number;

                var equals = entry.IndexOf('=');
                if (equals >= 0)
                {
                    name = entry.Substring(0, equals).Trim();
                    number = entry.Substring(equals + 1).Trim();
                }
                else
                {
                    var space = entry.LastIndexOf(' ');
                    if (space >= 0)
                    {
                        name = entry.Substring(0, space).Trim();
                        number = entry.Substring(space + 1).Trim();
                    }
                    else
                    {
                        // A bare price is its own name.
                        name = entry;
                        number = entry;
                    }
                }

                decimal price;
                if (!decimal.TryParse(number, System.Globalization.NumberStyles.Number,
                        System.Globalization.CultureInfo.InvariantCulture, out price) || price <= 0m)
                {
                    parsed.Unreadable.Add(entry);
                    continue;
                }

                if (name.Length == 0) name = number;
                parsed.Levels.Add(new TrackedLevel(name, price));
            }

            return parsed;
        }
    }

    public static class DomMath
    {
        /// <summary>
        /// Fold the book and the tape onto the ladder rows. Both land on the same row so the two
        /// facts about a price -- what is resting there and what has traded there -- sit together.
        /// </summary>
        public static LadderRow[] Aggregate(LadderGrid grid, IList<BookLevel> book, TapeBook tape)
        {
            if (grid == null || grid.Count <= 0) return new LadderRow[0];

            var rows = new LadderRow[grid.Count];
            for (var i = 0; i < rows.Length; i++) rows[i].Index = i;

            if (book != null)
            {
                for (var i = 0; i < book.Count; i++)
                {
                    var level = book[i];
                    if (level.Size <= 0m) continue;

                    var index = grid.IndexOf(level.Price);
                    if (index < 0) continue;

                    if (level.IsAsk) rows[index].AskRest += level.Size;
                    else rows[index].BidRest += level.Size;
                }
            }

            if (tape != null)
            {
                foreach (var pair in tape.All)
                {
                    var index = grid.IndexOf(pair.Key);
                    if (index < 0) continue;

                    rows[index].TapeBid += pair.Value[0];
                    rows[index].TapeAsk += pair.Value[1];
                }
            }

            return rows;
        }

        public static decimal MaxRest(LadderRow[] rows)
        {
            if (rows == null) return 0m;

            var max = 0m;
            for (var i = 0; i < rows.Length; i++)
            {
                if (rows[i].BidRest > max) max = rows[i].BidRest;
                if (rows[i].AskRest > max) max = rows[i].AskRest;
            }

            return max;
        }

        public static decimal MaxTape(LadderRow[] rows)
        {
            if (rows == null) return 0m;

            var max = 0m;
            for (var i = 0; i < rows.Length; i++)
            {
                var total = rows[i].TapeTotal;
                if (total > max) max = total;
            }

            return max;
        }

        /// <summary>
        /// Zero when there is no reference. An empty book draws nothing -- falling back to full
        /// scale would paint a maximum-size wall out of nothing that is there.
        /// </summary>
        public static decimal Normalise(decimal value, decimal reference)
        {
            if (reference <= 0m) return 0m;
            if (value <= 0m) return 0m;

            var t = value / reference;
            return t > 1m ? 1m : t;
        }

        /// <summary>Sizes small enough to matter stay exact; only the big ones get abbreviated.</summary>
        public static string Compact(decimal value)
        {
            if (value <= 0m) return "";

            var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            if (rounded < 1000m) return ((int)rounded).ToString();

            if (rounded < 1000000m)
            {
                var thousands = rounded / 1000m;
                return thousands < 10m
                    ? thousands.ToString("0.0") + "k"
                    : Math.Round(thousands, MidpointRounding.AwayFromZero).ToString("0") + "k";
            }

            var millions = rounded / 1000000m;
            return millions < 10m
                ? millions.ToString("0.0") + "M"
                : Math.Round(millions, MidpointRounding.AwayFromZero).ToString("0") + "M";
        }
    }
}
