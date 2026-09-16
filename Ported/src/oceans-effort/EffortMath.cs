using System;
using System.Collections.Generic;
using System.Globalization;

namespace OceansEffort
{
    /// <summary>Which side aggressed, or which side a verdict favours.</summary>
    public enum Side
    {
        None,
        Buy,
        Sell
    }

    /// <summary>Traded volume at one price inside a single bar.</summary>
    public struct PriceVolume
    {
        public decimal Price;
        public decimal Volume;

        /// <summary>Traded into the bid: the seller was the aggressor.</summary>
        public decimal Bid;

        /// <summary>Traded into the ask: the buyer was the aggressor.</summary>
        public decimal Ask;
    }

    /// <summary>
    /// One bar reduced to the facts this model reads. Everything here printed on the tape:
    /// traded volume, the bid/ask split behind it, and where the bar opened and closed.
    /// </summary>
    public struct BarFacts
    {
        public int Bar;

        public decimal Open;
        public decimal High;
        public decimal Low;
        public decimal Close;

        public decimal Volume;

        /// <summary>Buyers minus sellers by aggression, over the whole bar.</summary>
        public decimal Delta;

        /// <summary>The bar's own point of control and value area. Only meaningful with <see cref="HasValue"/>.</summary>
        public decimal Poc;
        public decimal ValueHigh;
        public decimal ValueLow;

        /// <summary>
        /// False when the bar carried no per-price data, so the value area is unknown. Layers
        /// that need it go dark rather than guessing one from the bar's high and low.
        /// </summary>
        public bool HasValue;

        public decimal ValueMid { get { return (ValueHigh + ValueLow) / 2m; } }

        /// <summary>Net progress: what the bar's effort actually achieved.</summary>
        public decimal Result { get { return Close - Open; } }

        /// <summary>-1 all sellers, 0 balanced, +1 all buyers.</summary>
        public decimal Lean
        {
            get
            {
                if (Volume <= 0m) return 0m;

                var lean = Delta / Volume;
                if (lean > 1m) return 1m;
                return lean < -1m ? -1m : lean;
            }
        }
    }

    /// <summary>How much of the window's verdict rests on real two-sided evidence.</summary>
    public enum EffortBasis
    {
        /// <summary>The window barely moved either way. No verdict.</summary>
        Insufficient,

        /// <summary>Every tick of progress in the window went one way, so there is nothing to compare against.</summary>
        OneSided,

        /// <summary>Both directions made progress and the two costs were compared.</summary>
        Both
    }

    /// <summary>
    /// What the last N bars cost each side. <see cref="UpCost"/> is contracts traded per tick of
    /// upward progress; <see cref="DownCost"/> the same downward. These are measurements of what
    /// happened, not readings of the order book -- the book is not visible here and is not being
    /// inferred.
    /// </summary>
    public struct EffortVerdict
    {
        public Side Side;
        public EffortBasis Basis;

        public decimal UpCost;
        public decimal DownCost;
        public bool HasUpCost;
        public bool HasDownCost;

        public decimal UpTicks;
        public decimal DownTicks;
        public decimal UpVolume;
        public decimal DownVolume;

        /// <summary>How much dearer the losing side was, as a multiple. 0 when only one side has a cost.</summary>
        public decimal Ratio
        {
            get
            {
                if (!HasUpCost || !HasDownCost) return 0m;
                if (UpCost <= 0m || DownCost <= 0m) return 0m;

                return UpCost > DownCost ? UpCost / DownCost : DownCost / UpCost;
            }
        }
    }

    /// <summary>A bar where one side aggressed hard and the price did not follow.</summary>
    public struct AbsorptionMark
    {
        public int Bar;

        /// <summary>The side that pushed and got nowhere.</summary>
        public Side Absorbed;

        /// <summary>The extreme they pushed into -- the high for absorbed buyers, the low for absorbed sellers.</summary>
        public decimal Price;

        public decimal Volume;
        public decimal Delta;
    }

    /// <summary>A bar where all three layers pointed the same way.</summary>
    public struct Setup
    {
        public int Bar;
        public Side Side;

        public decimal Entry;
        public decimal Stop;
        public decimal Target;

        /// <summary>
        /// The structure agreed but the stop is further away than the cap allows. Still a real
        /// reading of the auction, so it is kept and reported -- it is just not a trade, and it
        /// is drawn as one you could not have taken.
        /// </summary>
        public bool OverCap;

        /// <summary>Which trigger produced this.</summary>
        public SetupKind Kind;

        /// <summary>
        /// Where in the profile the entry stood: which cluster or value edge it was taken from.
        /// Kept on the setup so the box can say what it was engaging, months later.
        /// </summary>
        public Location Where;

        public decimal Risk
        {
            get { return this.Side == OceansEffort.Side.Buy ? Entry - Stop : Stop - Entry; }
        }
    }

    /// <summary>
    /// A stop that only ever moves in the trade's favour. Offering a looser level is ignored
    /// rather than applied, so a late aggression bar deep in the move cannot widen the risk.
    /// </summary>
    public sealed class Trailer
    {
        public Side Direction;
        public decimal Level;
        public bool Active;

        public void Start(Side direction, decimal level)
        {
            if (direction != Side.Buy && direction != Side.Sell)
            {
                Stop();
                return;
            }

            Direction = direction;
            Level = level;
            Active = true;
        }

        public void Stop()
        {
            Direction = Side.None;
            Level = 0m;
            Active = false;
        }

        /// <summary>Takes the level only if it tightens the stop. Returns true when it moved.</summary>
        public bool Offer(decimal level)
        {
            if (!Active) return false;

            if (Direction == Side.Buy)
            {
                if (level <= Level) return false;
                Level = level;
                return true;
            }

            if (level >= Level) return false;
            Level = level;
            return true;
        }
    }

    /// <summary>A setup and what became of it, filled in bar by bar as the chart advances.</summary>
    public sealed class SetupRun
    {
        public Setup Setup;

        /// <summary>The stop, one entry per bar from the setup bar onward.</summary>
        public readonly List<decimal> Levels = new List<decimal>();

        /// <summary>The bar that traded through the stop, or -1 while it is still standing.</summary>
        public int ExitBar = -1;
        public decimal ExitLevel;

        /// <summary>The bar that first reached the one-R target, or -1.</summary>
        public int TargetBar = -1;

        /// <summary>Target and stop were both touched by the same bar, so the order is unknowable.</summary>
        public bool SameBar;

        public bool Live { get { return ExitBar < 0; } }

        /// <summary>Where the stop stands now, which is where it started until a bar moves it.</summary>
        public decimal Trail
        {
            get { return Levels.Count == 0 ? Setup.Stop : Levels[Levels.Count - 1]; }
        }
    }

    /// <summary>
    /// Runs the setups forward, one closed bar at a time.
    ///
    /// The rule that matters here: ONE setup runs at a time. While a stop is still standing, the
    /// bars that keep agreeing with it are the same idea, not new ones -- without that, a three
    /// bar push prints three setups a few ticks apart and the chart becomes unreadable. The one
    /// exception is a setup you could actually have taken arriving behind one you could not.
    /// </summary>
    public sealed class SetupTracker
    {
        private readonly List<SetupRun> _runs = new List<SetupRun>();
        private SetupRun _active;
        private Trailer _trailer;
        private int _lastBar = -1;

        public IList<SetupRun> Runs { get { return _runs; } }

        public SetupRun Active { get { return _active; } }

        public void Reset()
        {
            _runs.Clear();
            _active = null;
            _trailer = null;
            _lastBar = -1;
        }

        /// <summary>The most recent run at or before a bar, or null. Scrolling back reads as living through it did.</summary>
        public SetupRun At(int bar)
        {
            for (var i = _runs.Count - 1; i >= 0; i--)
            {
                if (_runs[i].Setup.Bar <= bar) return _runs[i];
            }

            return null;
        }

        /// <summary>
        /// Carries the model over one newly closed bar: first what it did to the setup already
        /// running, then whether it starts a new one. A bar is only ever processed once, so the
        /// repeated calls a forming bar produces cost nothing and change nothing.
        /// </summary>
        public void Advance(int bar, BarFacts facts, Side aggression, bool hasCandidate, Setup setup)
        {
            if (bar < 0 || bar <= _lastBar) return;

            _lastBar = bar;

            if (_active != null) Carry(bar, facts, aggression);

            if (!hasCandidate) return;

            if (_active != null)
            {
                if (!_active.Setup.OverCap || setup.OverCap) return;

                // A tradeable setup arriving behind an untradeable one replaces it rather than
                // being hidden behind it.
                _runs.Remove(_active);
            }

            var run = new SetupRun();
            run.Setup = setup;
            run.Levels.Add(setup.Stop);

            _trailer = new Trailer();
            _trailer.Start(setup.Side, setup.Stop);

            _runs.Add(run);
            _active = run;
        }

        /// <summary>
        /// One bar of the running setup's life. The stop is tested BEFORE it is moved: a bar
        /// cannot both take the trade out and improve the stop it went through.
        /// </summary>
        private void Carry(int bar, BarFacts facts, Side aggression)
        {
            var setup = _active.Setup;
            if (bar <= setup.Bar) return;

            var buy = setup.Side == Side.Buy;

            var through = buy ? facts.Low <= _trailer.Level : facts.High >= _trailer.Level;
            var reached = buy ? facts.High >= setup.Target : facts.Low <= setup.Target;

            if (reached && _active.TargetBar < 0)
            {
                _active.TargetBar = bar;

                // Both on the same bar: bars do not record what happened first inside them, and
                // guessing would be inventing the outcome.
                if (through) _active.SameBar = true;
            }

            if (through)
            {
                _active.ExitBar = bar;
                _active.ExitLevel = _trailer.Level;
                _active.Levels.Add(_trailer.Level);
                _active = null;
                _trailer = null;
                return;
            }

            if (aggression == setup.Side)
                _trailer.Offer(buy ? facts.Low : facts.High);

            _active.Levels.Add(_trailer.Level);
        }
    }

    public static class EffortMath
    {
        /// <summary>
        /// The conventional volume value area for a single bar: start at the point of control and
        /// keep taking the heavier of the two pairs of levels above and below until the requested
        /// share of the volume is inside.
        ///
        /// The levels are laid out DENSELY first. A tick that never traded is a real zero, and
        /// walking a sparse list would step over it as if the price above were adjacent.
        ///
        /// Returns false -- and sets nothing -- when the bar has no volume, when the tick size is
        /// unknown, or when the bar spans more than maxTicks. It never truncates: a value area
        /// computed from part of a bar looks perfectly plausible and is simply wrong.
        /// </summary>
        public static bool ValueArea(IList<PriceVolume> levels, decimal tick, decimal percent,
                                     int maxTicks, out decimal poc, out decimal high, out decimal low)
        {
            poc = 0m;
            high = 0m;
            low = 0m;

            if (levels == null || levels.Count == 0 || tick <= 0m) return false;

            var minPrice = decimal.MaxValue;
            var maxPrice = decimal.MinValue;
            var any = false;

            for (var i = 0; i < levels.Count; i++)
            {
                if (levels[i].Volume <= 0m) continue;

                any = true;
                if (levels[i].Price < minPrice) minPrice = levels[i].Price;
                if (levels[i].Price > maxPrice) maxPrice = levels[i].Price;
            }

            if (!any) return false;

            var span = (int)Math.Round((maxPrice - minPrice) / tick, MidpointRounding.AwayFromZero);
            if (span < 0) return false;

            var count = span + 1;
            if (maxTicks > 0 && count > maxTicks) return false;

            var dense = new decimal[count];
            var total = 0m;

            for (var i = 0; i < levels.Count; i++)
            {
                if (levels[i].Volume <= 0m) continue;

                var index = (int)Math.Round((levels[i].Price - minPrice) / tick, MidpointRounding.AwayFromZero);
                if (index < 0 || index >= count) continue;

                dense[index] += levels[i].Volume;
                total += levels[i].Volume;
            }

            if (total <= 0m) return false;

            // Ties keep the lower price. Arbitrary, but fixed -- a point of control that flickers
            // between two equal levels as ticks land is worse than one that always picks the same.
            var pocIndex = 0;
            var max = dense[0];
            for (var i = 1; i < count; i++)
            {
                if (dense[i] <= max) continue;

                max = dense[i];
                pocIndex = i;
            }

            if (percent < 0m) percent = 0m;
            if (percent > 100m) percent = 100m;

            var target = total * percent / 100m;
            var lo = pocIndex;
            var hi = pocIndex;
            var inside = dense[pocIndex];

            while (inside < target && (lo > 0 || hi < count - 1))
            {
                var up = 0m;
                var upTo = hi;
                for (var i = 1; i <= 2 && hi + i < count; i++)
                {
                    up += dense[hi + i];
                    upTo = hi + i;
                }

                var down = 0m;
                var downTo = lo;
                for (var i = 1; i <= 2 && lo - i >= 0; i++)
                {
                    down += dense[lo - i];
                    downTo = lo - i;
                }

                var canUp = upTo != hi;
                var canDown = downTo != lo;

                if (canUp && (!canDown || up >= down))
                {
                    inside += up;
                    hi = upTo;
                }
                else if (canDown)
                {
                    inside += down;
                    lo = downTo;
                }
                else
                {
                    break;
                }
            }

            poc = minPrice + tick * pocIndex;
            low = minPrice + tick * lo;
            high = minPrice + tick * hi;
            return true;
        }

        /// <summary>
        /// Where the bar's value went relative to the one before it.
        ///
        /// The value area has to move by at least minTicks AND the point of control must not move
        /// against it. A bar that stretches its value area upward while its heaviest price slides
        /// down has not migrated anywhere -- it has widened, which is a different thing.
        /// </summary>
        public static Side Migration(BarFacts previous, BarFacts current, decimal tick, int minTicks)
        {
            if (tick <= 0m) return Side.None;
            if (!previous.HasValue || !current.HasValue) return Side.None;
            if (minTicks < 0) minTicks = 0;

            var shift = current.ValueMid - previous.ValueMid;
            var threshold = tick * minTicks;

            if (shift >= threshold && shift > 0m && current.Poc >= previous.Poc) return Side.Buy;
            if (-shift >= threshold && shift < 0m && current.Poc <= previous.Poc) return Side.Sell;

            return Side.None;
        }

        /// <summary>
        /// What each direction cost over bars [from, to]: contracts traded per tick of net
        /// progress that way. The side that got more ticks per contract is the one price is
        /// currently travelling more cheaply.
        ///
        /// Bars are counted by their net result, so a bar that opened and closed at the same
        /// price contributes its volume to neither side -- it made no progress for anyone.
        ///
        /// skew is how much dearer the losing side has to be before this says anything; at 1 the
        /// tiniest difference wins, which is noise. Below the progress floor the window has not
        /// gone anywhere and the verdict is Insufficient rather than a coin flip.
        /// </summary>
        public static EffortVerdict Effort(IList<BarFacts> bars, int from, int to, decimal tick,
                                           decimal skew, decimal minProgressTicks)
        {
            var verdict = new EffortVerdict();
            verdict.Side = Side.None;
            verdict.Basis = EffortBasis.Insufficient;

            if (bars == null || tick <= 0m) return verdict;
            if (from < 0) from = 0;
            if (to > bars.Count - 1) to = bars.Count - 1;
            if (to < from) return verdict;
            if (skew < 1m) skew = 1m;
            if (minProgressTicks < 0m) minProgressTicks = 0m;

            for (var i = from; i <= to; i++)
            {
                var bar = bars[i];
                if (bar.Volume <= 0m) continue;

                var ticks = (bar.Close - bar.Open) / tick;

                if (ticks > 0m)
                {
                    verdict.UpTicks += ticks;
                    verdict.UpVolume += bar.Volume;
                }
                else if (ticks < 0m)
                {
                    verdict.DownTicks += -ticks;
                    verdict.DownVolume += bar.Volume;
                }
            }

            var upCounts = verdict.UpTicks >= minProgressTicks && verdict.UpTicks > 0m;
            var downCounts = verdict.DownTicks >= minProgressTicks && verdict.DownTicks > 0m;

            if (upCounts)
            {
                verdict.HasUpCost = true;
                verdict.UpCost = verdict.UpVolume / verdict.UpTicks;
            }

            if (downCounts)
            {
                verdict.HasDownCost = true;
                verdict.DownCost = verdict.DownVolume / verdict.DownTicks;
            }

            if (!upCounts && !downCounts) return verdict;

            if (upCounts != downCounts)
            {
                // Only one direction cleared the progress floor, so there are not two costs to
                // compare. That is a fact about the window rather than a comparison, and it is
                // labelled as such instead of being dressed up as one.
                verdict.Basis = EffortBasis.OneSided;
                verdict.Side = upCounts ? Side.Buy : Side.Sell;
                return verdict;
            }

            verdict.Basis = EffortBasis.Both;

            if (verdict.UpCost * skew <= verdict.DownCost) verdict.Side = Side.Buy;
            else if (verdict.DownCost * skew <= verdict.UpCost) verdict.Side = Side.Sell;

            return verdict;
        }

        /// <summary>
        /// A bar where one side aggressed hard and got nothing for it: heavy volume, lopsided
        /// aggression, and a close that either went the other way or barely moved. Someone
        /// passive was on the other side and got filled.
        ///
        /// averageVolume is the yardstick for "heavy" and must be a real average of recent bars.
        /// With nothing to compare against this reports no absorption -- a fixed contract count
        /// would mean something different on every instrument and every session.
        /// </summary>
        public static bool Absorption(BarFacts bar, decimal averageVolume, decimal volumePercentOfAverage,
                                      decimal minLeanPercent, decimal tick, int maxResultTicks,
                                      out AbsorptionMark mark)
        {
            mark = new AbsorptionMark();

            if (tick <= 0m || bar.Volume <= 0m) return false;
            if (averageVolume <= 0m) return false;
            if (bar.Volume < averageVolume * volumePercentOfAverage / 100m) return false;

            var lean = bar.Lean;
            var absLean = lean < 0m ? -lean : lean;
            if (absLean * 100m < minLeanPercent) return false;

            var ticks = (bar.Close - bar.Open) / tick;
            var absTicks = ticks < 0m ? -ticks : ticks;

            var wentNowhere = absTicks <= maxResultTicks;
            var wentTheOtherWay = (lean > 0m && ticks < 0m) || (lean < 0m && ticks > 0m);
            if (!wentNowhere && !wentTheOtherWay) return false;

            mark.Bar = bar.Bar;
            mark.Absorbed = lean > 0m ? Side.Buy : Side.Sell;
            mark.Price = lean > 0m ? bar.High : bar.Low;
            mark.Volume = bar.Volume;
            mark.Delta = bar.Delta;
            return true;
        }

        /// <summary>
        /// A bar where one side aggressed hard, whichever way price then went. This is the
        /// reference the stop trails behind: the last place that side actually showed up.
        /// </summary>
        public static Side Aggression(BarFacts bar, decimal averageVolume, decimal volumePercentOfAverage,
                                      decimal minLeanPercent)
        {
            if (bar.Volume <= 0m || averageVolume <= 0m) return Side.None;
            if (bar.Volume < averageVolume * volumePercentOfAverage / 100m) return Side.None;

            var lean = bar.Lean;
            var absLean = lean < 0m ? -lean : lean;
            if (absLean * 100m < minLeanPercent) return Side.None;

            return lean > 0m ? Side.Buy : Side.Sell;
        }

        /// <summary>
        /// A setup only exists where all three layers agree: value migrating that way, the bar's
        /// own aggression and close agreeing, and the window's cheaper direction agreeing too.
        /// Any one of them silent is enough to refuse.
        ///
        /// The stop goes the far side of the bar's value area, which is the level that has to
        /// break for the migration to be wrong, and the target is the same distance again.
        ///
        /// requireLevel adds the profile: the entry must sit on a cluster area or an edge of
        /// value that argues the same way. This is the transcript's "use your confirmation to buy
        /// from levels that are relevant for the order flow".
        ///
        /// maxRiskTicks does not change any of that. It only marks the setup as one whose stop is
        /// further away than you would take; pass 0 to switch that off.
        /// </summary>
        public static bool Confluence(Side migration, BarFacts bar, EffortVerdict effort, Side absorbed,
                                      Location where, bool requireLevel,
                                      decimal tick, int stopBufferTicks, int maxRiskTicks, out Setup setup)
        {
            setup = new Setup();

            if (tick <= 0m || !bar.HasValue) return false;
            if (stopBufferTicks < 0) stopBufferTicks = 0;
            if (migration == Side.None || migration != effort.Side) return false;

            // The fourth layer: a price in mid-air belongs to nothing. The entry has to be standing
            // on a cluster the profile actually printed, or on an edge of value. An unknown
            // location refuses too -- not knowing where price is is not the same as it being fine.
            if (requireLevel && where.Favours != migration) return false;

            if (migration == Side.Buy)
            {
                if (bar.Delta <= 0m || bar.Close <= bar.Open) return false;

                // Buyers who just got absorbed are not confirmation of anything.
                if (absorbed == Side.Buy) return false;

                setup.Stop = bar.ValueLow - tick * stopBufferTicks;
                setup.Entry = bar.Close;
                if (setup.Entry <= setup.Stop) return false;

                setup.Target = setup.Entry + (setup.Entry - setup.Stop);
            }
            else
            {
                if (bar.Delta >= 0m || bar.Close >= bar.Open) return false;
                if (absorbed == Side.Sell) return false;

                setup.Stop = bar.ValueHigh + tick * stopBufferTicks;
                setup.Entry = bar.Close;
                if (setup.Entry >= setup.Stop) return false;

                setup.Target = setup.Entry - (setup.Stop - setup.Entry);
            }

            setup.Bar = bar.Bar;
            setup.Side = migration;
            setup.Kind = SetupKind.Continuation;
            setup.Where = where;

            // The cap is a fact about the setup, not a reason to move the stop. Pulling the stop
            // in to fit would put it somewhere the structure never justified, and the resulting
            // trade would be a different one wearing this one's confirmation.
            setup.OverCap = maxRiskTicks > 0 && setup.Risk > tick * maxRiskTicks;

            return true;
        }

        /// <summary>
        /// Which side the bar itself came down on: its aggression and its close have to agree.
        /// A bar that closed up on selling pressure has not made anyone's case.
        /// </summary>
        public static Side BarSide(BarFacts bar)
        {
            if (bar.Delta > 0m && bar.Close > bar.Open) return Side.Buy;
            if (bar.Delta < 0m && bar.Close < bar.Open) return Side.Sell;

            return Side.None;
        }

        /// <summary>
        /// Whether the three cheap layers already agree, so it is worth building a profile to test
        /// the fourth. Building one at every bar costs a full pass over the lookback for a
        /// question that is almost always answered "no" by then.
        /// </summary>
        public static Side MightBeSetup(Side migration, BarFacts bar, EffortVerdict effort)
        {
            if (migration == Side.None) return Side.None;
            if (migration != effort.Side) return Side.None;

            return BarSide(bar) == migration ? migration : Side.None;
        }

        /// <summary>
        /// The middle value of a set. Median rather than mean throughout the calibration, because
        /// one 5000-lot bar or one limit-move candle would otherwise set the scale for everything
        /// measured against it.
        /// </summary>
        private static decimal Median(List<decimal> values)
        {
            if (values == null || values.Count == 0) return 0m;

            values.Sort();
            var middle = values.Count / 2;

            return values.Count % 2 == 1
                ? values[middle]
                : (values[middle - 1] + values[middle]) / 2m;
        }

        /// <summary>
        /// The typical size of a bar's delta over a range, in contracts.
        ///
        /// This is what lets one setting mean the same thing on MNQ and on Bitcoin: a threshold in
        /// contracts is meaningless across instruments, but "twice what a bar here normally does"
        /// is the same idea everywhere. It is measured, not assumed -- an instrument that has not
        /// traded yet returns zero and the caller decides what that means.
        /// </summary>
        public static decimal TypicalDelta(IList<BarFacts> bars, int from, int to)
        {
            if (bars == null) return 0m;
            if (from < 0) from = 0;
            if (to > bars.Count - 1) to = bars.Count - 1;
            if (to < from) return 0m;

            var sizes = new List<decimal>();
            for (var i = from; i <= to; i++)
            {
                var delta = bars[i].Delta;
                if (delta < 0m) delta = -delta;
                if (delta > 0m) sizes.Add(delta);
            }

            return Median(sizes);
        }

        /// <summary>The typical high-to-low of a bar over a range, in ticks.</summary>
        public static decimal TypicalRangeTicks(IList<BarFacts> bars, int from, int to, decimal tick)
        {
            if (bars == null || tick <= 0m) return 0m;
            if (from < 0) from = 0;
            if (to > bars.Count - 1) to = bars.Count - 1;
            if (to < from) return 0m;

            var sizes = new List<decimal>();
            for (var i = from; i <= to; i++)
            {
                var range = bars[i].High - bars[i].Low;
                if (range > 0m) sizes.Add(Math.Round(range / tick));
            }

            return Median(sizes);
        }

        /// <summary>Mean volume of bars [from, to]. Zero when the range is empty.</summary>
        public static decimal AverageVolume(IList<BarFacts> bars, int from, int to)
        {
            if (bars == null) return 0m;
            if (from < 0) from = 0;
            if (to > bars.Count - 1) to = bars.Count - 1;
            if (to < from) return 0m;

            var total = 0m;
            var count = 0;

            for (var i = from; i <= to; i++)
            {
                total += bars[i].Volume;
                count++;
            }

            return count == 0 ? 0m : total / count;
        }

        /// <summary>
        /// Short enough to sit beside a bar. Under 1000 stays exact -- on a single bar that is
        /// most of them, and "0.1k" would hide the gap between 60 and 140. Same shape as the
        /// other Ocean indicators use, so the same number reads the same everywhere.
        /// </summary>
        public static string Compact(decimal value)
        {
            var negative = value < 0m;
            if (negative) value = -value;

            var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
            string text;

            if (rounded < 1000m)
            {
                text = ((long)rounded).ToString(CultureInfo.InvariantCulture);
            }
            else if (rounded < 1000000m)
            {
                var k = rounded / 1000m;
                text = k < 100m
                     ? k.ToString("0.#", CultureInfo.InvariantCulture) + "k"
                     : Math.Round(k, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture) + "k";
            }
            else
            {
                text = (rounded / 1000000m).ToString("0.#", CultureInfo.InvariantCulture) + "M";
            }

            return negative ? "-" + text : text;
        }
    }
}
