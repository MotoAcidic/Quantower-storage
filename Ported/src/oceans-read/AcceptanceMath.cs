using System;
using System.Collections.Generic;

namespace OceansRead
{
    /// <summary>How a break of a reference level resolved.</summary>
    public enum AcceptanceState
    {
        /// <summary>Nothing has been broken, or there is no rhythm to measure a break against.</summary>
        None,

        /// <summary>Price is beyond the level and neither threshold has been met yet.</summary>
        Testing,

        /// <summary>Enough time and enough ground on the far side. The market took the new condition.</summary>
        Accepted,

        /// <summary>Price closed back through before either threshold was met.</summary>
        Rejected
    }

    /// <summary>A price the market is measured against. The name is what prints in the read.</summary>
    public struct Reference
    {
        public string Name;
        public decimal Price;
        public int Rank;          // lower sorts first when two levels sit on the same price

        public Reference(string name, decimal price, int rank)
        {
            Name = name;
            Price = price;
            Rank = rank;
        }
    }

    /// <summary>One break of one level, followed from the break until it resolves.</summary>
    public sealed class AcceptanceTest
    {
        public string Name;
        public decimal Price;
        public bool Up;                  // the break was upward

        public DateTime BrokeAt;
        public int BrokeBar;
        public DateTime LastAt;

        public double MinutesBeyond;     // time on bars that CLOSED beyond, not time since the break
        public double MinutesElapsed;
        public decimal MaxGroundTicks;
        public decimal GroundTicks;
        public int ClosesBeyond;

        public double MinutesNeeded;
        public decimal GroundNeeded;

        public bool ValueFollowed;       // the developing point of control crossed to the far side

        public AcceptanceState State = AcceptanceState.Testing;
        public string Why = string.Empty;

        public bool Live => State == AcceptanceState.Testing;

        /// <summary>How far through each requirement the test has got, 0 to 1.</summary>
        public double TimeProgress => MinutesNeeded <= 0d ? 1d : Math.Min(1d, MinutesBeyond / MinutesNeeded);
        public double GroundProgress => GroundNeeded <= 0m ? 1d : Math.Min(1d, (double)(MaxGroundTicks / GroundNeeded));
    }

    /// <summary>
    /// The acceptance test, run against every reference level at once.
    ///
    /// A break is a bar CLOSING through the level, never a wick: a wick through is the auction
    /// probing, and calling it a break starts a test on every touch of every level. What
    /// settles the test is time and ground on the far side, both scaled by the market's own
    /// measured rhythm -- a move that has not covered one typical rotation has not proved
    /// anything, it is still inside the rotation that started it.
    ///
    /// With no rhythm there are no thresholds, and rather than substitute a fixed number of
    /// ticks the tester reports that it cannot judge. A tick count that means "far" on a quiet
    /// overnight means "noise" at the open, and a wrong acceptance call is the expensive kind.
    /// </summary>
    public sealed class AcceptanceTracker
    {
        private readonly Dictionary<string, AcceptanceTest> _live =
            new Dictionary<string, AcceptanceTest>();

        private readonly Dictionary<string, int> _side = new Dictionary<string, int>();
        private readonly List<AcceptanceTest> _resolved = new List<AcceptanceTest>();

        private const int ResolvedCap = 40;

        public IReadOnlyList<AcceptanceTest> Resolved => _resolved;

        public void Clear()
        {
            _live.Clear();
            _side.Clear();
            _resolved.Clear();
        }

        /// <summary>
        /// One closed bar against the current set of reference levels.
        ///
        /// Levels move -- a developing value area high is a different price every bar -- so a
        /// live test keeps the price it was OPENED at. Re-reading the level each bar would let
        /// the test chase price and never resolve.
        /// </summary>
        public void Update(DateTime time, decimal close, decimal high, decimal low,
                           double barMinutes, decimal tickSize, decimal developingPoc,
                           IReadOnlyList<Reference> references, double minutesNeeded,
                           decimal groundNeeded, int bar)
        {
            if (tickSize <= 0m || references == null) return;

            foreach (var reference in references)
            {
                if (reference.Price <= 0m || string.IsNullOrEmpty(reference.Name)) continue;

                var side = close > reference.Price ? 1 : close < reference.Price ? -1 : 0;

                _side.TryGetValue(reference.Name, out var was);

                // A break needs a side to break FROM. The first bar only records where we are.
                if (was != 0 && side != 0 && side != was && !_live.ContainsKey(reference.Name))
                {
                    if (minutesNeeded > 0d && groundNeeded > 0m)
                    {
                        _live[reference.Name] = new AcceptanceTest
                        {
                            Name = reference.Name,
                            Price = reference.Price,
                            Up = side > 0,
                            BrokeAt = time,
                            BrokeBar = bar,
                            LastAt = time,
                            MinutesNeeded = minutesNeeded,
                            GroundNeeded = groundNeeded
                        };
                    }
                }

                if (side != 0) _side[reference.Name] = side;
            }

            if (_live.Count == 0) return;

            var finished = new List<string>();

            foreach (var pair in _live)
            {
                var test = pair.Value;
                var beyond = test.Up ? close > test.Price : close < test.Price;
                var excursion = test.Up ? high - test.Price : test.Price - low;
                var current = test.Up ? close - test.Price : test.Price - close;

                test.LastAt = time;
                test.MinutesElapsed += barMinutes;
                test.GroundTicks = current / tickSize;

                if (excursion > 0m)
                {
                    var ticks = excursion / tickSize;
                    if (ticks > test.MaxGroundTicks) test.MaxGroundTicks = ticks;
                }

                if (developingPoc > 0m)
                {
                    var pocBeyond = test.Up ? developingPoc > test.Price : developingPoc < test.Price;
                    if (pocBeyond) test.ValueFollowed = true;
                }

                if (beyond)
                {
                    test.MinutesBeyond += barMinutes;
                    test.ClosesBeyond++;
                }
                else
                {
                    // Back through on a close. Whatever the excursion reached, the market did
                    // not keep it, and that is the definition of a rejection.
                    test.State = AcceptanceState.Rejected;
                    test.Why = "closed back through after " + Format.Minutes(test.MinutesBeyond) +
                               " and " + Format.Ticks(test.MaxGroundTicks) + " beyond";

                    finished.Add(pair.Key);
                    continue;
                }

                if (test.MinutesBeyond >= test.MinutesNeeded && test.MaxGroundTicks >= test.GroundNeeded)
                {
                    test.State = AcceptanceState.Accepted;
                    test.Why = test.ValueFollowed
                             ? "time, ground and value all beyond"
                             : "time and ground beyond, value has not followed";

                    finished.Add(pair.Key);
                }
            }

            foreach (var key in finished)
            {
                _resolved.Add(_live[key]);
                _live.Remove(key);
            }

            if (_resolved.Count > ResolvedCap) _resolved.RemoveRange(0, _resolved.Count - ResolvedCap);
        }

        /// <summary>
        /// The test worth printing: the live one that has run longest, because that is the
        /// question the market is actually working on. Ties go to the bigger excursion.
        /// </summary>
        public AcceptanceTest Current()
        {
            AcceptanceTest best = null;

            foreach (var test in _live.Values)
            {
                if (best == null ||
                    test.BrokeBar < best.BrokeBar ||
                    (test.BrokeBar == best.BrokeBar && test.MaxGroundTicks > best.MaxGroundTicks))
                {
                    best = test;
                }
            }

            return best;
        }

        /// <summary>The most recently resolved test, for the line under the live one.</summary>
        public AcceptanceTest Last()
        {
            return _resolved.Count == 0 ? null : _resolved[_resolved.Count - 1];
        }

        public IEnumerable<AcceptanceTest> LiveTests => _live.Values;
    }

    /// <summary>Finding the level the market is currently working on.</summary>
    public static class AcceptanceMath
    {
        /// <summary>
        /// The nearest reference level within a distance, or a name of null if price is not at
        /// one. Ties break on rank so the same level wins every frame rather than alternating.
        /// </summary>
        public static bool Nearest(IReadOnlyList<Reference> references, decimal price,
                                   decimal withinTicks, decimal tickSize, out Reference found)
        {
            found = default;
            if (references == null || tickSize <= 0m) return false;

            var bestDistance = decimal.MaxValue;
            var have = false;

            foreach (var reference in references)
            {
                if (reference.Price <= 0m) continue;

                var distance = Math.Abs(price - reference.Price) / tickSize;
                if (distance > withinTicks) continue;

                if (!have || distance < bestDistance ||
                   (distance == bestDistance && reference.Rank < found.Rank))
                {
                    found = reference;
                    bestDistance = distance;
                    have = true;
                }
            }

            return have;
        }
    }

    /// <summary>Shared number formatting, so the same quantity reads the same in every line.</summary>
    public static class Format
    {
        public static string Minutes(double minutes)
        {
            if (minutes < 1d) return "<1 min";
            if (minutes < 90d) return Math.Round(minutes) + " min";

            var hours = minutes / 60d;

            return Math.Round(hours, 1).ToString("0.#") + " h";
        }

        public static string Ticks(decimal ticks) => Math.Round(ticks) + "t";

        public static string Signed(decimal value)
        {
            var rounded = Math.Round(value);

            return (rounded > 0m ? "+" : "") + rounded.ToString("#,##0");
        }

        public static string Percent(double fraction) => Math.Round(fraction * 100d) + "%";
    }
}
