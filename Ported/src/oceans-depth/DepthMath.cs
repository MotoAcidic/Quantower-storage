using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace OceansDepth
{
    /// <summary>Which number the strip sizes and colours a level by.</summary>
    public enum DepthMetric
    {
        /// <summary>Everything resting at the price, however many orders that is.</summary>
        [Display(Name = "Total size at price")] TotalSize,

        /// <summary>The single biggest order resting at the price. Market-by-order only.</summary>
        [Display(Name = "Biggest single order (needs market by order)")] LargestOrder
    }

    /// <summary>How normalised size (0..1) is bent before it becomes a bar length or a colour.</summary>
    public enum IntensityCurve
    {
        Linear,

        /// <summary>Square root -- lifts small levels, so the strip is never nearly empty.</summary>
        EmphasiseSmall,

        /// <summary>Squared -- only genuine outliers light up.</summary>
        EmphasiseLarge
    }

    /// <summary>
    /// One price of resting liquidity. Deliberately free of ATAS types: the indicator translates
    /// the platform's book into these, and the tests fabricate them directly.
    /// </summary>
    public struct DepthLevel
    {
        public decimal Price;

        /// <summary>Total resting size at Price.</summary>
        public decimal Size;

        /// <summary>Biggest single order at Price. Zero when the feed gives no per-order data.</summary>
        public decimal Largest;

        /// <summary>Orders resting at Price. Zero when the feed gives no per-order data.</summary>
        public int Orders;

        public bool IsAsk;

        public DepthLevel(decimal price, decimal size, bool isAsk)
        {
            Price = price;
            Size = size;
            Largest = 0m;
            Orders = 0;
            IsAsk = isAsk;
        }
    }

    /// <summary>One drawn row of the strip: one or more ticks collapsed to fit the screen.</summary>
    public struct DepthRow
    {
        public decimal Low;
        public decimal High;
        public decimal Mid;

        public decimal BidSize;
        public decimal AskSize;
        public decimal BidLargest;
        public decimal AskLargest;
        public int BidOrders;
        public int AskOrders;
        public decimal Value(DepthMetric metric, bool isAsk)
        {
            if (metric == DepthMetric.LargestOrder)
                return isAsk ? AskLargest : BidLargest;

            return isAsk ? AskSize : BidSize;
        }
    }

    /// <summary>
    /// The price-to-row mapping. Rows are laid out in PRICE space, anchored to a multiple of the
    /// row size, so scrolling the chart slides rows across the screen instead of reshuffling which
    /// ticks share a row. That is what stops the strip shimmering as the chart moves.
    /// </summary>
    public sealed class RowGrid
    {
        public decimal Anchor;    // low edge of row 0
        public decimal RowSize;   // price span of one row
        public int Count;

        /// <summary>
        /// How many ticks have to share a row for the row to be at least minRowPixels tall.
        /// Zoomed in this is 1 -- one tick, one row. Zoomed out it grows, which is the whole
        /// reason the strip never needs adjusting by hand.
        /// </summary>
        public static int TicksPerRow(decimal pixelsPerTick, int minRowPixels)
        {
            if (minRowPixels < 1) minRowPixels = 1;
            if (pixelsPerTick <= 0m) return 1;

            var needed = minRowPixels / pixelsPerTick;
            var ticks = (int)Math.Ceiling(needed);
            return ticks < 1 ? 1 : ticks;
        }

        /// <summary>
        /// Builds the grid covering [low, high]. Returns null when the inputs cannot describe a
        /// grid -- an unattached chart, a zero tick size -- rather than inventing one.
        /// </summary>
        public static RowGrid Build(decimal low, decimal high, decimal tickSize, int ticksPerRow, int maxRows)
        {
            if (tickSize <= 0m) return null;
            if (ticksPerRow < 1) ticksPerRow = 1;
            if (high < low) return null;

            var grid = new RowGrid();
            grid.RowSize = tickSize * ticksPerRow;
            grid.Anchor = Math.Floor(low / grid.RowSize) * grid.RowSize;

            var span = high - grid.Anchor;
            var count = (int)Math.Ceiling(span / grid.RowSize) + 1;

            if (count < 1) return null;
            if (maxRows > 0 && count > maxRows) count = maxRows;

            grid.Count = count;
            return grid;
        }

        public int IndexOf(decimal price)
        {
            var offset = price - Anchor;
            if (offset < 0m) return -1;

            var index = (int)Math.Floor(offset / RowSize);
            return index >= Count ? -1 : index;
        }

        public decimal Low(int index) { return Anchor + RowSize * index; }
        public decimal High(int index) { return Anchor + RowSize * (index + 1); }
        public decimal Mid(int index) { return Anchor + RowSize * index + RowSize / 2m; }

        /// <summary>
        /// Collapses levels onto the grid. Sizes add; "largest order" takes the max, because the
        /// biggest single order in a row is still one order however many ticks the row spans.
        /// </summary>
        public DepthRow[] Aggregate(IList<DepthLevel> levels)
        {
            var rows = new DepthRow[Count];

            for (var i = 0; i < Count; i++)
            {
                rows[i].Low = Low(i);
                rows[i].High = High(i);
                rows[i].Mid = Mid(i);
            }

            if (levels == null) return rows;

            for (var i = 0; i < levels.Count; i++)
            {
                var level = levels[i];
                if (level.Size <= 0m && level.Largest <= 0m) continue;

                var index = IndexOf(level.Price);
                if (index < 0) continue;

                if (level.IsAsk)
                {
                    rows[index].AskSize += level.Size;
                    rows[index].AskOrders += level.Orders;
                    if (level.Largest > rows[index].AskLargest) rows[index].AskLargest = level.Largest;
                }
                else
                {
                    rows[index].BidSize += level.Size;
                    rows[index].BidOrders += level.Orders;
                    if (level.Largest > rows[index].BidLargest) rows[index].BidLargest = level.Largest;
                }
            }

            return rows;
        }
    }

    /// <summary>
    /// Remembers how much size WAS resting at a price after it is pulled, and fades it out.
    ///
    /// Two reasons this is not optional polish. MNQ's book flickers many times a second, so a
    /// strip drawn straight off the raw snapshot strobes and the numbers cannot be read. And a
    /// 400-lot that appears and vanishes is information -- the fade is the only way it is ever
    /// visible on a chart that repaints a few times a second.
    ///
    /// The clock is passed in rather than read, so the decay can be tested exactly.
    /// </summary>
    public sealed class PeakBook
    {
        private struct Entry
        {
            public decimal Peak;
            public long PeakMs;
            public decimal Current;
            public long SeenMs;
            public decimal Largest;
            public int Orders;
        }

        private readonly Dictionary<decimal, Entry> _bids = new Dictionary<decimal, Entry>();
        private readonly Dictionary<decimal, Entry> _asks = new Dictionary<decimal, Entry>();

        /// <summary>How long a pulled peak is held at full value before it starts to fade.</summary>
        public int HoldMs = 1500;

        /// <summary>How long the fade from peak down to what is actually there now takes.</summary>
        public int FadeMs = 3500;

        public void Clear()
        {
            _bids.Clear();
            _asks.Clear();
        }

        public int Count { get { return _bids.Count + _asks.Count; } }

        /// <summary>
        /// Takes a whole book snapshot. Prices in the book are updated; prices the book knows
        /// about that are NOT in the snapshot have been pulled, and go to a current size of zero
        /// so the fade can start.
        /// </summary>
        public void Observe(IList<DepthLevel> levels, long nowMs)
        {
            MarkAbsent(_bids, nowMs);
            MarkAbsent(_asks, nowMs);

            if (levels == null) return;

            for (var i = 0; i < levels.Count; i++)
            {
                var level = levels[i];
                var book = level.IsAsk ? _asks : _bids;

                Entry entry;
                if (!book.TryGetValue(level.Price, out entry))
                {
                    entry = new Entry();
                    entry.Peak = level.Size;
                    entry.PeakMs = nowMs;
                }

                entry.Current = level.Size;
                entry.SeenMs = nowMs;
                entry.Largest = level.Largest;
                entry.Orders = level.Orders;

                if (level.Size >= entry.Peak)
                {
                    entry.Peak = level.Size;
                    entry.PeakMs = nowMs;
                }

                book[level.Price] = entry;
            }
        }

        private static void MarkAbsent(Dictionary<decimal, Entry> book, long nowMs)
        {
            if (book.Count == 0) return;

            var prices = new decimal[book.Count];
            book.Keys.CopyTo(prices, 0);

            for (var i = 0; i < prices.Length; i++)
            {
                var entry = book[prices[i]];
                if (entry.SeenMs == nowMs) continue;

                entry.Current = 0m;
                entry.Largest = 0m;
                entry.Orders = 0;
                book[prices[i]] = entry;
            }
        }

        /// <summary>
        /// The levels to actually draw: what is resting now, lifted toward the recent peak by
        /// however much of the fade is left. Entries that have fully faded to nothing are dropped.
        /// </summary>
        public List<DepthLevel> Emit(long nowMs)
        {
            var result = new List<DepthLevel>(_bids.Count + _asks.Count);
            EmitSide(_bids, false, nowMs, result);
            EmitSide(_asks, true, nowMs, result);
            return result;
        }

        private void EmitSide(Dictionary<decimal, Entry> book, bool isAsk, long nowMs, List<DepthLevel> into)
        {
            if (book.Count == 0) return;

            var prices = new decimal[book.Count];
            book.Keys.CopyTo(prices, 0);

            for (var i = 0; i < prices.Length; i++)
            {
                var entry = book[prices[i]];
                var shown = Decayed(entry.Peak, entry.PeakMs, entry.Current, nowMs);

                if (shown <= 0m)
                {
                    book.Remove(prices[i]);
                    continue;
                }

                var level = new DepthLevel(prices[i], shown, isAsk);

                // Per-order figures describe what is REALLY there. A faded ghost has no orders
                // in it, so they are reported as-is and never scaled up with the ghost.
                level.Largest = entry.Largest;
                level.Orders = entry.Orders;

                into.Add(level);
            }
        }

        /// <summary>Peak for HoldMs, then a straight line down to current over FadeMs.</summary>
        public decimal Decayed(decimal peak, long peakMs, decimal current, long nowMs)
        {
            if (peak <= current) return current;

            var elapsed = nowMs - peakMs;
            if (elapsed <= 0) return peak;

            var hold = HoldMs < 0 ? 0 : HoldMs;
            if (elapsed <= hold) return peak;

            if (FadeMs <= 0) return current;

            var into = elapsed - hold;
            if (into >= FadeMs) return current;

            var t = (decimal)into / FadeMs;
            return peak - (peak - current) * t;
        }

        /// <summary>Prices this far outside the drawn range are never seen again -- drop them.</summary>
        public void Prune(decimal low, decimal high)
        {
            PruneSide(_bids, low, high);
            PruneSide(_asks, low, high);
        }

        private static void PruneSide(Dictionary<decimal, Entry> book, decimal low, decimal high)
        {
            if (book.Count == 0) return;

            var prices = new decimal[book.Count];
            book.Keys.CopyTo(prices, 0);

            for (var i = 0; i < prices.Length; i++)
            {
                if (prices[i] < low || prices[i] > high) book.Remove(prices[i]);
            }
        }
    }

    /// <summary>A colour with no WPF or GDI attached, so the gradient can be tested.</summary>
    public struct Rgb
    {
        public byte R;
        public byte G;
        public byte B;

        public Rgb(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }
    }

    public static class DepthMath
    {
        /// <summary>Largest value on the strip, one side. Zero when the side is empty.</summary>
        public static decimal Max(DepthRow[] rows, DepthMetric metric, bool isAsk)
        {
            var max = 0m;
            if (rows == null) return max;

            for (var i = 0; i < rows.Length; i++)
            {
                var v = rows[i].Value(metric, isAsk);
                if (v > max) max = v;
            }

            return max;
        }

        /// <summary>Largest value on the strip across both sides.</summary>
        public static decimal Max(DepthRow[] rows, DepthMetric metric)
        {
            var bid = Max(rows, metric, false);
            var ask = Max(rows, metric, true);
            return bid > ask ? bid : ask;
        }

        /// <summary>
        /// Size as a fraction of the reference, clamped to 0..1. A reference of zero gives zero:
        /// an empty book must draw nothing, not a full-scale bar.
        /// </summary>
        public static decimal Normalise(decimal value, decimal reference)
        {
            if (reference <= 0m) return 0m;
            if (value <= 0m) return 0m;

            var t = value / reference;
            return t > 1m ? 1m : t;
        }

        /// <summary>Bends 0..1 so the strip can favour outliers or small levels.</summary>
        public static decimal Shape(decimal t, IntensityCurve curve)
        {
            if (t <= 0m) return 0m;
            if (t >= 1m) return 1m;

            if (curve == IntensityCurve.EmphasiseLarge) return t * t;

            if (curve == IntensityCurve.EmphasiseSmall)
                return (decimal)Math.Sqrt((double)t);

            return t;
        }

        public static Rgb Lerp(Rgb from, Rgb to, decimal t)
        {
            if (t <= 0m) return from;
            if (t >= 1m) return to;

            return new Rgb(LerpByte(from.R, to.R, t),
                           LerpByte(from.G, to.G, t),
                           LerpByte(from.B, to.B, t));
        }

        public static byte LerpByte(byte from, byte to, decimal t)
        {
            if (t <= 0m) return from;
            if (t >= 1m) return to;

            var value = from + (to - from) * t;
            var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);

            if (rounded < 0) rounded = 0;
            if (rounded > 255) rounded = 255;
            return (byte)rounded;
        }

        /// <summary>
        /// Short enough to fit a narrow strip. Sizes below 1000 stay exact -- on MNQ that is
        /// nearly every level, and a rounded "0.1k" would hide the difference between 60 and 140.
        /// </summary>
        public static string Compact(decimal value)
        {
            if (value < 0m) value = -value;

            var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            if (rounded < 1000m) return ((long)rounded).ToString(CultureInfo.InvariantCulture);

            if (rounded < 1000000m)
            {
                var k = rounded / 1000m;
                if (k < 100m) return k.ToString("0.#", CultureInfo.InvariantCulture) + "k";
                return Math.Round(k, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "k";
            }

            var m = rounded / 1000000m;
            return m.ToString("0.#", CultureInfo.InvariantCulture) + "M";
        }

        /// <summary>
        /// Indices of the n biggest rows on a side, biggest first. Used to decide which levels
        /// are worth a printed number -- labelling everything is labelling nothing.
        /// </summary>
        public static int[] TopIndices(DepthRow[] rows, DepthMetric metric, bool isAsk, int n)
        {
            if (rows == null || n <= 0) return new int[0];

            var order = new List<int>(rows.Length);
            for (var i = 0; i < rows.Length; i++)
            {
                if (rows[i].Value(metric, isAsk) > 0m) order.Add(i);
            }

            order.Sort(delegate (int a, int b)
            {
                var va = rows[a].Value(metric, isAsk);
                var vb = rows[b].Value(metric, isAsk);
                var cmp = vb.CompareTo(va);
                return cmp != 0 ? cmp : a.CompareTo(b);
            });

            if (order.Count > n) order.RemoveRange(n, order.Count - n);
            return order.ToArray();
        }
    }
}
