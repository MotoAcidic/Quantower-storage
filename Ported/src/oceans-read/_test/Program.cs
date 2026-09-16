using System;
using System.Collections.Generic;
using OceansRead;

// The math harness for Ocean's Read. Every file it compiles is free of ATAS types on purpose,
// so the whole read -- profile, rhythm, acceptance, waves, VWAP, orderflow and the box itself --
// can be exercised without the platform.
//
//     cd _test && dotnet run -c Release
//
// A suite that passes first try has not been believed yet. Run mutate.sh before trusting it.
namespace OceansRead.Tests
{
    internal static class Program
    {
        private static int _passed;
        private static readonly List<string> _failures = new List<string>();

        private static void Check(string what, bool ok)
        {
            if (ok) _passed++;
            else _failures.Add(what);
        }

        private static void Near(string what, decimal actual, decimal expected, decimal tolerance)
        {
            Check(what + " (got " + actual + ", wanted " + expected + ")",
                  Math.Abs(actual - expected) <= tolerance);
        }

        private static void Near(string what, double actual, double expected, double tolerance)
        {
            Check(what + " (got " + actual + ", wanted " + expected + ")",
                  Math.Abs(actual - expected) <= tolerance);
        }

        private static int Main()
        {
            ClockTests();
            ProfileTests();
            NodeTests();
            ShapeTests();
            SwingTests();
            RhythmTests();
            WaveTests();
            VwapTests();
            AcceptanceTests();
            AuctionTests();
            OrderflowTests();
            ModelTests();

            foreach (var failure in _failures) Console.WriteLine("FAIL  " + failure);

            Console.WriteLine(_failures.Count == 0
                ? "All Ocean Read tests passed -- " + _passed + " checks."
                : _passed + " passed, " + _failures.Count + " FAILED.");

            return _failures.Count == 0 ? 0 : 1;
        }

        #region The clock

        private static void ClockTests()
        {
            var afternoon = new DateTime(2026, 9, 10, 14, 30, 0);
            var evening = new DateTime(2026, 9, 10, 17, 30, 0);
            var earlyHours = new DateTime(2026, 9, 11, 3, 0, 0);

            Check("an afternoon bar belongs to its own date",
                  ReadClock.TradeDate(afternoon) == new DateTime(2026, 9, 10));

            Check("a bar after the 5 PM reopen belongs to the next date",
                  ReadClock.TradeDate(evening) == new DateTime(2026, 9, 11));

            Check("an overnight bar belongs to the date it settles on",
                  ReadClock.TradeDate(earlyHours) == new DateTime(2026, 9, 11));

            Check("5 PM exactly rolls to the next date",
                  ReadClock.TradeDate(new DateTime(2026, 9, 10, 17, 0, 0)) == new DateTime(2026, 9, 11));

            Check("one minute before 5 PM does not",
                  ReadClock.TradeDate(new DateTime(2026, 9, 10, 16, 59, 0)) == new DateTime(2026, 9, 10));

            Check("a trade date opens at 5 PM the evening before",
                  ReadClock.OpenOf(new DateTime(2026, 9, 11)) == new DateTime(2026, 9, 10, 17, 0, 0));

            // The halt is 16:00-17:00 Central. 15:00 is the cash close and trading carries on
            // through it; this has shipped wrong three times across these projects.
            Check("the futures day closes at the 4 PM halt, not the 3 PM cash close",
                  ReadClock.CloseOf(new DateTime(2026, 9, 11)) == new DateTime(2026, 9, 11, 16, 0, 0));

            Check("the halt hour is 16", ReadClock.HaltHour == 16);

            Check("cash hours run 08:30 to 15:00 Central",
                  ReadClock.RegularOpen == new TimeSpan(8, 30, 0) &&
                  ReadClock.RegularClose == new TimeSpan(15, 0, 0));

            Check("08:29 is not cash hours",
                  !ReadClock.InRegularHours(new DateTime(2026, 9, 10, 8, 29, 0)));

            Check("08:30 is",
                  ReadClock.InRegularHours(new DateTime(2026, 9, 10, 8, 30, 0)));

            Check("15:00 is not, it is the close",
                  !ReadClock.InRegularHours(new DateTime(2026, 9, 10, 15, 0, 0)));

            Check("14:30 is the power hour",
                  ReadClock.InPowerHour(new DateTime(2026, 9, 10, 14, 30, 0)));

            Check("13:30 is not",
                  !ReadClock.InPowerHour(new DateTime(2026, 9, 10, 13, 30, 0)));

            Check("the halt hour is outside the futures day",
                  !ReadClock.InScope(new DateTime(2026, 9, 10, 16, 30, 0), SessionScope.FuturesDay));

            Check("but 15:30 is inside it -- trading carries on after the cash close",
                  ReadClock.InScope(new DateTime(2026, 9, 10, 15, 30, 0), SessionScope.FuturesDay));

            Check("and 15:30 is outside cash hours",
                  !ReadClock.InScope(new DateTime(2026, 9, 10, 15, 30, 0), SessionScope.RegularHours));

            Check("the evening reopen is inside the futures day",
                  ReadClock.InScope(new DateTime(2026, 9, 10, 18, 0, 0), SessionScope.FuturesDay));

            // The futures week opens Sunday at 5 PM, which already carries Monday's trade date.
            Check("Sunday evening lands in the week that starts the next Monday",
                  ReadClock.WeekOf(ReadClock.TradeDate(new DateTime(2026, 9, 13, 17, 30, 0))) ==
                  new DateTime(2026, 9, 14));

            Check("Wednesday lands in the same week",
                  ReadClock.WeekOf(new DateTime(2026, 9, 16)) == new DateTime(2026, 9, 14));

            Check("Friday lands in the same week",
                  ReadClock.WeekOf(new DateTime(2026, 9, 18)) == new DateTime(2026, 9, 14));

            Check("quarters start in Jan, Apr, Jul, Oct",
                  ReadClock.QuarterOf(new DateTime(2026, 5, 20)) == new DateTime(2026, 4, 1) &&
                  ReadClock.QuarterOf(new DateTime(2026, 12, 31)) == new DateTime(2026, 10, 1) &&
                  ReadClock.QuarterOf(new DateTime(2026, 1, 1)) == new DateTime(2026, 1, 1));

            Check("the anchor key separates two different weeks",
                  ReadClock.AnchorKey(new DateTime(2026, 9, 16), VwapAnchor.Week) !=
                  ReadClock.AnchorKey(new DateTime(2026, 9, 23), VwapAnchor.Week));

            Check("and joins two days of the same month",
                  ReadClock.AnchorKey(new DateTime(2026, 9, 2), VwapAnchor.Month) ==
                  ReadClock.AnchorKey(new DateTime(2026, 9, 29), VwapAnchor.Month));

            Check("every anchor has its own short tag",
                  ReadClock.Tag(VwapAnchor.Year) != ReadClock.Tag(VwapAnchor.Week) &&
                  ReadClock.Tag(VwapAnchor.Day) != ReadClock.Tag(VwapAnchor.Session));
        }

        #endregion

        #region The profile

        /// <summary>A profile from a list of (price, volume, bracket) with a quarter-point tick.</summary>
        private static ReadProfile Build(params (decimal price, decimal volume, int bracket)[] rows)
        {
            var builder = new ProfileBuilder();
            foreach (var row in rows)
                builder.Add(row.price, row.volume, row.volume / 2m, row.volume / 2m, 1000L, row.bracket);

            return builder.Build(0.25m, 60000);
        }

        private static void ProfileTests()
        {
            var profile = Build((100.00m, 10m, 0), (100.25m, 30m, 0), (100.50m, 60m, 0),
                                (100.75m, 20m, 0), (101.00m, 10m, 0));

            Check("a profile is built", profile != null);
            Check("the ladder spans low to high", profile.Count == 5);
            Check("the lowest price is at index 0", profile.LowPrice == 100.00m);
            Check("prices step by the tick size", profile.PriceAt(2) == 100.50m);
            Check("total volume adds up", profile.TotalVolume == 130m);
            Check("the busiest level is remembered", profile.MaxVolume == 60m);

            ProfileMath.ComputeValueArea(profile, 70m, ProfileBasis.Volume);
            Check("the point of control is the busiest price", profile.Poc == 100.50m);
            Check("the value area contains the point of control",
                  profile.Val <= profile.Poc && profile.Vah >= profile.Poc);
            Check("the value area is inside the range",
                  profile.Val >= profile.LowPrice && profile.Vah <= profile.HighPrice);

            // 70% of 130 is 91. POC 60, then the pair above (20+10=30) against the pair below
            // (30+10=40): the heavier side goes first, so the band grows DOWNWARD.
            Check("the value area grows toward the heavier side", profile.Val == 100.00m);

            // A ladder with a hole in it must still be dense: a price nothing traded at is a
            // real zero, not a missing row.
            var gapped = Build((100.00m, 10m, 0), (101.00m, 40m, 0));
            Check("prices that never traded still get a row", gapped.Count == 5);
            Check("and the row is empty", gapped.Levels[2].Volume == 0m);
            Check("and its price is right", gapped.PriceAt(2) == 100.50m);

            // Ties break toward the middle so the point of control does not jump between two
            // equally busy prices depending on enumeration order.
            var tied = Build((100.00m, 50m, 0), (100.25m, 20m, 0), (100.50m, 50m, 0),
                             (100.75m, 20m, 0), (101.00m, 10m, 0));
            var poc = ProfileMath.FindPoc(tied, ProfileBasis.Volume);
            Check("a tie for the point of control breaks toward the middle", tied.PriceAt(poc) == 100.50m);

            // The two measures genuinely differ: one bracket can carry all the volume.
            var timed = Build((100.00m, 5m, 0), (100.00m, 5m, 1), (100.00m, 5m, 2),
                              (100.25m, 400m, 0));
            Check("volume picks the heavy price",
                  timed.PriceAt(ProfileMath.FindPoc(timed, ProfileBasis.Volume)) == 100.25m);
            Check("TPO picks the price that lasted",
                  timed.PriceAt(ProfileMath.FindPoc(timed, ProfileBasis.Tpo)) == 100.00m);

            Check("a level revisited in a later bracket counts twice", timed.Levels[0].Tpo == 3);

            // A bracket seen twice is one bracket, not two: brackets need not arrive in order.
            var revisited = Build((100.00m, 5m, 3), (100.00m, 5m, 1), (100.00m, 5m, 3));
            Check("a repeated bracket is not counted again", revisited.Levels[0].Tpo == 2);

            var wide = new ProfileBuilder();
            wide.Add(100m, 1m, 0m, 0m, 0L, 0);
            wide.Add(100000m, 1m, 0m, 0m, 0L, 0);
            Check("an over-wide profile is refused rather than truncated",
                  wide.Build(0.25m, 60000) == null);

            var empty = new ProfileBuilder();
            Check("an empty builder makes no profile", empty.Build(0.25m, 60000) == null);
            Check("a zero tick size makes no profile", Build((100m, 1m, 0)) != null &&
                  new ProfileBuilder().Build(0m, 60000) == null);

            Check("a price inside value is inside", profile.InValue(profile.Poc));
            Check("a price above the value area high is not", !profile.InValue(profile.Vah + 1m));

            Check("the index of a price round-trips", profile.IndexOfPrice(100.50m) == 2);
            Check("an index below the ladder clamps to the bottom", profile.ClampIndex(1m) == 0);
            Check("an index above it clamps to the top", profile.ClampIndex(9999m) == profile.Count - 1);
        }

        private static void NodeTests()
        {
            // Two shelves either side of a genuine valley.
            var profile = Build((100.00m, 100m, 0), (100.25m, 100m, 0), (100.50m, 5m, 0),
                                (100.75m, 5m, 0), (101.00m, 100m, 0), (101.25m, 100m, 0));

            var shelves = ProfileMath.FindNodes(profile, ProfileBasis.Volume, true, 70m, 2, 1, 4);
            Check("two shelves are found", shelves.Length == 2);
            Check("shelves come back in price order", shelves.Length == 2 && shelves[0].Low < shelves[1].Low);
            Check("a shelf spans its whole run",
                  shelves.Length == 2 && shelves[0].Low == 100.00m && shelves[0].High == 100.25m);

            var gaps = ProfileMath.FindNodes(profile, ProfileBasis.Volume, false, 10m, 2, 1, 4);
            Check("the thin ground between them is one gap", gaps.Length == 1);
            Check("and it sits between the shelves",
                  gaps.Length == 1 && gaps[0].Low == 100.50m && gaps[0].High == 100.75m);

            // Without gap bridging one thin tick reports two shelves where the market built one.
            var nicked = Build((100.00m, 100m, 0), (100.25m, 40m, 0), (100.50m, 100m, 0));
            var bridged = ProfileMath.FindNodes(nicked, ProfileBasis.Volume, true, 70m, 2, 1, 4);
            Check("a one-tick dip does not split a shelf in two", bridged.Length == 1);
            Check("and the bridged shelf covers the dip",
                  bridged.Length == 1 && bridged[0].Low == 100.00m && bridged[0].High == 100.50m);

            // With no bridging the dip splits the run in two -- and the minimum width has to
            // come down with it, or both halves are dropped for being one tick wide and the
            // check would pass for the wrong reason.
            var unbridged = ProfileMath.FindNodes(nicked, ProfileBasis.Volume, true, 70m, 1, 0, 4);
            Check("with no bridging allowed it does split", unbridged.Length == 2);
            Check("and the two halves sit either side of the dip",
                  unbridged.Length == 2 && unbridged[0].High == 100.00m && unbridged[1].Low == 100.50m);

            var narrow = ProfileMath.FindNodes(nicked, ProfileBasis.Volume, true, 70m, 4, 1, 4);
            Check("a shelf thinner than the minimum is dropped", narrow.Length == 0);

            // Trimming keeps the HEAVIEST, and the survivors are re-sorted into price order --
            // so the test data has to put the light one in the middle or trimming by weight and
            // trimming the tail would agree and the check would prove nothing.
            // All three runs have to clear the shelf threshold, or the light one is never a
            // candidate and the trim is not exercised at all. The gaps have to be wide enough
            // that bridging does not merge the three into one.
            var three = Build((100.00m, 100m, 0), (100.25m, 100m, 0),
                              (100.50m, 5m, 0), (100.75m, 5m, 0), (101.00m, 5m, 0),
                              (101.25m, 75m, 0), (101.50m, 75m, 0),
                              (101.75m, 5m, 0), (102.00m, 5m, 0), (102.25m, 5m, 0),
                              (102.50m, 90m, 0), (102.75m, 90m, 0));

            var all = ProfileMath.FindNodes(three, ProfileBasis.Volume, true, 70m, 2, 1, 0);
            Check("three separated shelves are all found", all.Length == 3);

            var kept = ProfileMath.FindNodes(three, ProfileBasis.Volume, true, 70m, 2, 1, 2);
            Check("trimming keeps the two heaviest shelves", kept.Length == 2);
            Check("and drops the light one in the middle",
                  kept.Length == 2 && kept[0].Low == 100.00m && kept[1].Low == 102.50m);

            // Single prints: one bracket, away from the extremes.
            var prints = Build((100.00m, 10m, 0), (100.00m, 10m, 1),
                               (100.25m, 5m, 0), (100.50m, 5m, 0), (100.75m, 5m, 0),
                               (101.00m, 10m, 0), (101.00m, 10m, 1));

            var found = ProfileMath.FindSinglePrints(prints, 3, 1);
            Check("a run of one-bracket prices inside the range is found", found.Length == 1);
            Check("and it is exactly the run",
                  found.Length == 1 && found[0].Low == 100.25m && found[0].High == 100.75m);

            Check("a run shorter than the minimum is not a single print",
                  ProfileMath.FindSinglePrints(prints, 4, 1).Length == 0);

            // A single print at the very top is a TAIL, which is the opposite reading.
            var tailed = Build((100.00m, 10m, 0), (100.00m, 10m, 1), (100.25m, 10m, 0), (100.25m, 10m, 1),
                               (100.50m, 3m, 0), (100.75m, 3m, 0));
            Check("single prints at the extreme are excluded",
                  ProfileMath.FindSinglePrints(tailed, 2, 1).Length == 0);
            Check("they are reported as a tail instead",
                  ProfileMath.TailTicks(tailed, true) == 2);
            Check("and there is no tail at the other end",
                  ProfileMath.TailTicks(tailed, false) == 0);

            var allSingle = Build((100.00m, 1m, 0), (100.25m, 1m, 0), (100.50m, 1m, 0));
            Check("a profile that is single prints throughout has no tail, it has no body",
                  ProfileMath.TailTicks(allSingle, true) == 0);

            var poor = Build((100.00m, 10m, 0), (100.00m, 10m, 1),
                             (100.25m, 10m, 0), (100.25m, 10m, 1),
                             (100.50m, 10m, 0), (100.50m, 10m, 1));
            Check("a flat high several brackets deep is a poor high",
                  ProfileMath.IsPoorHigh(poor, 2, 2));
            Check("and a flat low is a poor low",
                  ProfileMath.IsPoorLow(poor, 2, 2));
            Check("a high that ended in a tail is not poor",
                  !ProfileMath.IsPoorHigh(tailed, 2, 2));
        }

        private static void ShapeTests()
        {
            var minBrackets = 2;

            var balanced = Build((100.00m, 10m, 0), (100.25m, 40m, 1), (100.50m, 90m, 2),
                                 (100.75m, 40m, 3), (101.00m, 10m, 4));
            ProfileMath.ComputeValueArea(balanced, 70m, ProfileBasis.Volume);
            Check("a fat middle is a balanced profile",
                  ProfileMath.Shape(balanced, ProfileBasis.Volume, minBrackets) == ProfileShape.Normal);

            var top = Build((100.00m, 5m, 0), (100.25m, 8m, 1), (100.50m, 10m, 2),
                            (100.75m, 60m, 3), (101.00m, 90m, 4), (101.25m, 55m, 5));
            ProfileMath.ComputeValueArea(top, 70m, ProfileBasis.Volume);
            Check("fat above with a tail below is a P",
                  ProfileMath.Shape(top, ProfileBasis.Volume, minBrackets) == ProfileShape.PShape);

            var bottom = Build((100.00m, 55m, 0), (100.25m, 90m, 1), (100.50m, 60m, 2),
                               (100.75m, 10m, 3), (101.00m, 8m, 4), (101.25m, 5m, 5));
            ProfileMath.ComputeValueArea(bottom, 70m, ProfileBasis.Volume);
            Check("fat below with a tail above is a b",
                  ProfileMath.Shape(bottom, ProfileBasis.Volume, minBrackets) == ProfileShape.BShape);

            var twin = Build((100.00m, 90m, 0), (100.25m, 90m, 1), (100.50m, 3m, 2),
                             (100.75m, 3m, 3), (101.00m, 90m, 4), (101.25m, 90m, 5));
            ProfileMath.ComputeValueArea(twin, 70m, ProfileBasis.Volume);
            Check("two shelves with a real valley are a double distribution",
                  ProfileMath.Shape(twin, ProfileBasis.Volume, minBrackets) == ProfileShape.DoubleDistribution);

            // Two shelves either side of a MILD dip are one distribution with a rough top.
            var rough = Build((100.00m, 90m, 0), (100.25m, 90m, 1), (100.50m, 55m, 2),
                              (100.75m, 55m, 3), (101.00m, 90m, 4), (101.25m, 90m, 5));
            ProfileMath.ComputeValueArea(rough, 70m, ProfileBasis.Volume);
            Check("a mild dip between shelves is not a double distribution",
                  ProfileMath.Shape(rough, ProfileBasis.Volume, minBrackets) != ProfileShape.DoubleDistribution);

            // The value area has to cover MOST of the range without covering all of it, or a
            // threshold anywhere below 100% would pass and the number would be untested.
            var flat = Build((100.00m, 20m, 0), (100.25m, 20m, 1), (100.50m, 20m, 2),
                             (100.75m, 20m, 3), (101.00m, 20m, 4), (101.25m, 5m, 5));
            ProfileMath.ComputeValueArea(flat, 70m, ProfileBasis.Volume);
            Check("a profile thin all the way through is elongated",
                  ProfileMath.Shape(flat, ProfileBasis.Volume, minBrackets) == ProfileShape.Elongated);
            Check("and its value area really does cover most of the range, not all of it",
                  flat.ValueWidth / flat.Range >= 0.8m && flat.ValueWidth < flat.Range);

            Check("too few brackets means the shape is still forming",
                  ProfileMath.Shape(balanced, ProfileBasis.Volume, 99) == ProfileShape.Forming);

            Check("a null profile has no shape",
                  ProfileMath.Shape(null, ProfileBasis.Volume, 2) == ProfileShape.Forming);

            Near("two identical value areas overlap completely",
                 ProfileMath.Overlap(100m, 110m, 100m, 110m), 1m, 0.001m);

            Near("two areas that do not touch overlap not at all",
                 ProfileMath.Overlap(100m, 110m, 120m, 130m), 0m, 0.001m);

            Near("half-overlapping areas measure against their combined span",
                 ProfileMath.Overlap(100m, 110m, 105m, 115m), 5m / 15m, 0.001m);

            Near("two zero-width areas at the same price overlap completely",
                 ProfileMath.Overlap(100m, 100m, 100m, 100m), 1m, 0.001m);

            Near("two zero-width areas at different prices do not",
                 ProfileMath.Overlap(100m, 100m, 101m, 101m), 0m, 0.001m);
        }

        #endregion

        #region Rotations and rhythm

        private static void SwingTests()
        {
            var tracker = new SwingTracker();
            var start = new DateTime(2026, 9, 10, 9, 0, 0);
            var bar = 0;

            void Feed(decimal high, decimal low)
            {
                tracker.Add(bar, start.AddMinutes(bar), high, low, 1m, 10m);
                bar++;
            }

            // Up to 120, back to 105 (15 > 10, confirms the high), up to 130.
            Feed(100m, 99m);
            Feed(110m, 105m);
            Feed(120m, 115m);
            Feed(118m, 105m);

            Check("a pullback past the threshold confirms the high", tracker.Swings.Count == 1);
            Check("the pivot is the extreme, not the bar that confirmed it",
                  tracker.Swings.Count == 1 && tracker.Swings[0].Price == 120m);
            Check("the pivot is a high", tracker.Swings.Count == 1 && tracker.Swings[0].IsHigh);
            Check("the leg is measured from the previous pivot",
                  tracker.Swings.Count == 1 && tracker.Swings[0].FromPrice == 99m);
            Check("the leg size is in ticks",
                  tracker.Swings.Count == 1 && tracker.Swings[0].Ticks == 21m);
            Check("the leg duration is in minutes",
                  tracker.Swings.Count == 1 && tracker.Swings[0].Minutes == 2d);
            Check("a leg in progress runs the other way after a pivot", tracker.Direction == -1);

            Feed(112m, 104m);
            Feed(130m, 120m);
            Check("a rally past the threshold confirms the low", tracker.Swings.Count == 2);
            Check("the second pivot is a low", tracker.Swings.Count == 2 && !tracker.Swings[1].IsHigh);
            Check("and it is the lowest price reached",
                  tracker.Swings.Count == 2 && tracker.Swings[1].Price == 104m);

            // An outside bar sets an extreme and reverses far enough in the same bar. Bar data
            // does not record which end traded first, so this must not confirm a rotation.
            var outside = new SwingTracker();
            var b = 0;

            void FeedOutside(decimal high, decimal low)
            {
                outside.Add(b, start.AddMinutes(b), high, low, 1m, 10m);
                b++;
            }

            FeedOutside(100m, 99m);
            FeedOutside(112m, 111m);     // settles the direction: an up leg from 99
            Check("the direction is up before the outside bar arrives", outside.Direction == 1);

            // This bar makes a new high AND falls far enough from it to look like a completed
            // rotation. It must not be read as one.
            FeedOutside(140m, 100m);
            Check("an outside bar does not confirm its own reversal", outside.Swings.Count == 0);
            Check("but it does extend the leg", outside.ProvisionalPrice == 140m);

            FeedOutside(135m, 120m);
            Check("the next bar does", outside.Swings.Count == 1);
            Check("and the pivot is the outside bar's high",
                  outside.Swings.Count == 1 && outside.Swings[0].Price == 140m);

            // The same trap, mirrored. A down leg has its own branch, and a guard that only
            // holds on one side is half a guard.
            var falling = new SwingTracker();
            var d = 0;

            void FeedFalling(decimal high, decimal low)
            {
                falling.Add(d, start.AddMinutes(d), high, low, 1m, 10m);
                d++;
            }

            FeedFalling(100m, 99m);
            FeedFalling(89m, 88m);       // settles the direction: a down leg from 100
            Check("the direction is down before the outside bar arrives", falling.Direction == -1);

            FeedFalling(100m, 60m);      // new low AND a rally far enough to look like a turn
            Check("an outside bar does not confirm its own reversal going down",
                  falling.Swings.Count == 0);
            Check("but it does extend the down leg", falling.ProvisionalPrice == 60m);

            FeedFalling(80m, 65m);
            Check("the next bar does, going down", falling.Swings.Count == 1);
            Check("and the pivot is the outside bar's low",
                  falling.Swings.Count == 1 && falling.Swings[0].Price == 60m);

            // Before the first pivot there is no direction, so both extremes are carried.
            var seeding = new SwingTracker();
            seeding.Add(0, start, 100m, 99m, 1m, 50m);
            seeding.Add(1, start.AddMinutes(1), 101m, 98m, 1m, 50m);
            Check("no direction is invented before the range is wide enough", seeding.Direction == 0);

            seeding.Add(2, start.AddMinutes(2), 160m, 150m, 1m, 50m);
            Check("the first direction is settled once the range opens up", seeding.Direction == 1);
            Check("and it runs from the extreme that came first", seeding.AnchorPrice == 98m);

            var cleared = new SwingTracker();
            cleared.Add(0, start, 100m, 90m, 1m, 5m);
            cleared.Add(1, start.AddMinutes(1), 101m, 99m, 1m, 5m);
            cleared.Clear();
            Check("clearing forgets everything", cleared.Swings.Count == 0 && cleared.Direction == 0);

            var zeroTick = new SwingTracker();
            zeroTick.Add(0, start, 100m, 90m, 0m, 5m);
            Check("a zero tick size is refused rather than dividing by it", zeroTick.Direction == 0);
        }

        /// <summary>A chain of legs, each starting where the last one ended, with a tick size of 1.</summary>
        private static List<Swing> Chain(decimal start, params decimal[] pivots)
        {
            var swings = new List<Swing>();
            var price = start;
            var bar = 0;
            var time = new DateTime(2026, 9, 10, 9, 0, 0);

            foreach (var pivot in pivots)
            {
                swings.Add(new Swing
                {
                    FromBar = bar,
                    FromTime = time.AddMinutes(bar),
                    FromPrice = price,
                    PivotBar = bar + 10,
                    PivotTime = time.AddMinutes(bar + 10),
                    Price = pivot,
                    IsHigh = pivot > price,
                    Ticks = Math.Abs(pivot - price),
                    Bars = 10,
                    Minutes = 10d
                });

                price = pivot;
                bar += 10;
            }

            return swings;
        }

        private static void RhythmTests()
        {
            var sorted = new[] { 1m, 2m, 3m, 4m, 5m };

            Near("the median of five is the middle one", RhythmMath.Quantile(sorted, 0.5), 3m, 0.001m);
            Near("the lowest quantile is the first", RhythmMath.Quantile(sorted, 0.0), 1m, 0.001m);
            Near("the highest is the last", RhythmMath.Quantile(sorted, 1.0), 5m, 0.001m);
            Near("quantiles interpolate between samples", RhythmMath.Quantile(sorted, 0.25), 2m, 0.001m);
            Near("and interpolate off a sample boundary too",
                 RhythmMath.Quantile(new[] { 0m, 10m }, 0.3), 3m, 0.001m);

            // Durations run through the double overload, which is a separate implementation.
            Near("the duration quantiles interpolate as well",
                 RhythmMath.Quantile(new[] { 0d, 10d }, 0.3), 3d, 0.001d);
            Near("and their median is the middle sample",
                 RhythmMath.Quantile(new[] { 1d, 2d, 3d, 4d, 5d }, 0.5), 3d, 0.001d);
            Near("an empty set has no quantile", RhythmMath.Quantile(new decimal[0], 0.5), 0m, 0.001m);

            Near("a percentile counts the samples at or below",
                 RhythmMath.Percentile(sorted, 3m), 0.6d, 0.001d);
            Near("nothing is below the smallest", RhythmMath.Percentile(sorted, 0m), 0d, 0.001d);
            Near("everything is below the largest", RhythmMath.Percentile(sorted, 99m), 1d, 0.001d);

            var swings = Chain(100m, 120m, 100m, 130m, 105m, 140m, 110m, 150m, 120m, 160m, 130m);

            var rhythm = RhythmMath.Measure(swings, 40, 5);
            Check("a rhythm is measured once there are enough rotations", rhythm.Valid);
            Check("it counts the rotations it used", rhythm.Legs == swings.Count);
            Check("the median rotation is positive", rhythm.MedianTicks > 0m);
            Check("the median duration is the leg length", Math.Abs(rhythm.MedianMinutes - 10d) < 0.001d);
            Check("quartiles are ordered",
                  rhythm.QuarterTicks <= rhythm.MedianTicks &&
                  rhythm.MedianTicks <= rhythm.ThreeQuarterTicks &&
                  rhythm.ThreeQuarterTicks <= rhythm.NinetyTicks);

            Check("too few rotations means no rhythm at all",
                  !RhythmMath.Measure(Chain(100m, 110m, 100m), 40, 8).Valid);

            Check("no rotations means no rhythm", !RhythmMath.Measure(new List<Swing>(), 40, 5).Valid);

            // Up and down rotations are measured separately, because they are not symmetric.
            var lopsided = Chain(100m, 160m, 155m, 215m, 210m, 270m, 265m, 325m, 320m, 380m, 375m);
            var skew = RhythmMath.Measure(lopsided, 40, 5);
            Check("up and down rotations are measured apart",
                  skew.MedianUpTicks > skew.MedianDownTicks);
            Check("and a market whose sides differ by half is reported lopsided", skew.Lopsided);
            Check("a symmetric market is not", !rhythm.Lopsided);

            // The lookback really is a lookback: the oldest rotations drop out.
            var recent = RhythmMath.Measure(swings, 3, 3);
            Check("the lookback limits how many rotations count", recent.Legs == 3);

            // Efficiency: a market that travelled a long way to go nowhere was rotating.
            var rotating = Chain(100m, 120m, 100m, 120m, 100m, 120m, 100m);
            Near("a market that ends where it began is inefficient",
                 RhythmMath.Efficiency(rotating, 20), 0m, 0.001m);

            var trending = Chain(100m, 200m, 195m, 300m, 295m, 400m);
            Check("a market that kept its ground is efficient",
                  RhythmMath.Efficiency(trending, 20) > 0.8m);

            Check("efficiency of nothing is zero",
                  RhythmMath.Efficiency(new List<Swing>(), 10) == 0m);

            // Average true range picks up the gap, not just the bar's own range.
            var high = new List<decimal> { 100m, 110m, 112m };
            var low = new List<decimal> { 99m, 108m, 110m };
            var close = new List<decimal> { 100m, 109m, 111m };
            // Bar 1 gapped up from a close of 100 to a high of 110: its true range is the 10
            // point gap, not the 2 point bar. Bar 2 is an ordinary 3 point range.
            Near("true range includes the gap from the previous close",
                 RhythmMath.AtrTicks(high, low, close, 2, 2, 1m), (10m + 3m) / 2m, 0.001m);
            Check("no bars means no range", RhythmMath.AtrTicks(high, low, close, 0, 2, 1m) == 0m);
            Check("a zero tick size is refused", RhythmMath.AtrTicks(high, low, close, 2, 2, 0m) == 0m);

            // The leg in progress, read against the rhythm.
            var tracker = new SwingTracker();
            var start = new DateTime(2026, 9, 10, 9, 0, 0);
            for (var i = 0; i < 3; i++)
                tracker.Add(i, start.AddMinutes(i), 100m + i * 20m, 99m + i * 20m, 1m, 5m);

            var leg = RhythmMath.ReadLeg(tracker, rhythm, start.AddMinutes(5), 1m);
            Check("a leg in progress is reported", leg.Active);
            Check("with a direction", leg.Up);
            Check("a leg with no rhythm behind it is unmeasured",
                  RhythmMath.ReadLeg(tracker, new Rhythm(), start.AddMinutes(5), 1m).Maturity ==
                  LegMaturity.Unknown);
            Check("an empty tracker has no leg",
                  !RhythmMath.ReadLeg(new SwingTracker(), rhythm, start, 1m).Active);

            // Maturity takes the FURTHER of the two readings. A leg that has run a long way in
            // no time at all is mature, and taking the lower reading would call it early.
            var fast = new Rhythm { Legs = 5 };
            fast.SetSamples(new[] { 10m, 20m, 30m, 40m, 50m }, new[] { 60d, 70d, 80d, 90d, 100d });

            var quickTracker = new SwingTracker();
            quickTracker.Add(0, start, 100m, 99m, 1m, 5m);
            quickTracker.Add(1, start.AddMinutes(1), 200m, 199m, 1m, 5m);

            var quick = RhythmMath.ReadLeg(quickTracker, fast, start.AddMinutes(1), 1m);
            Check("a leg far past the usual size is beyond, whatever the clock says",
                  quick.Maturity == LegMaturity.Beyond);
            Check("its size percentile is at the top", quick.TickPercentile >= 0.99d);
            Check("and its time percentile is at the bottom", quick.MinutePercentile <= 0.01d);
        }

        #endregion

        #region Waves

        private static void WaveTests()
        {
            // A clean five up: 100-120, back to 110, up to 160, back to 140, up to 180.
            var clean = Chain(100m, 120m, 110m, 160m, 140m, 180m);
            var count = WaveMath.Count(clean);

            Check("a clean five is a valid count", count.Valid);
            Check("it runs upward", count.Up);
            Check("it carries six points", count.Points.Length == 6);
            Check("and the last is the fifth wave's end", count.Points[5] == 180m);
            Check("every rule is recorded, not just the broken ones", count.Rules.Count == 4);
            Near("wave 2 retraced half of wave 1", count.Wave2Retrace, 0.5m, 0.001m);
            Near("wave 3 ran two and a half times wave 1", count.Wave3Extension, 2.5m, 0.001m);
            Near("wave 4 retraced two fifths of wave 3", count.Wave4Retrace, 0.4m, 0.001m);
            Check("a five that exceeded the third is not truncated", !count.Truncated);

            // Rule 1: wave 2 must hold the start of wave 1.
            var deep2 = Chain(100m, 120m, 95m, 160m, 140m, 180m);
            var broken2 = WaveMath.Count(deep2);
            Check("a wave 2 through the start of wave 1 breaks the count", !broken2.Valid);
            Check("and the failure names that rule", broken2.Failure.Contains("wave 2"));

            // Rule 2: wave 3 must not be the shortest of the three.
            // Only the shortest-wave rule may break here, or the failure would name whichever
            // rule happened to be checked first and the assertion below would prove nothing.
            var short3 = Chain(100m, 200m, 190m, 250m, 230m, 400m);
            var broken3 = WaveMath.Count(short3);
            Check("a wave 3 shorter than both 1 and 5 breaks the count", !broken3.Valid);
            Check("and the failure names that rule", broken3.Failure.Contains("shortest"));

            // A wave 3 shorter than wave 1 but longer than wave 5 is allowed: the rule is that
            // it must not be the SHORTEST, not that it must be the longest.
            var middling = Chain(100m, 150m, 130m, 170m, 160m, 175m);
            Check("a wave 3 that is merely not the longest is allowed",
                  WaveMath.Count(middling).Valid);

            // Rule 3: wave 4 must stay out of wave 1.
            var overlap4 = Chain(100m, 130m, 110m, 200m, 125m, 220m);
            var broken4 = WaveMath.Count(overlap4);
            Check("a wave 4 back inside wave 1 breaks the count", !broken4.Valid);
            Check("and the failure names that rule", broken4.Failure.Contains("wave 4"));

            // Rule 4: wave 3 must clear the end of wave 1.
            var stunted = Chain(100m, 150m, 120m, 145m, 130m, 250m);
            Check("a wave 3 that never cleared wave 1 breaks the count",
                  !WaveMath.Count(stunted).Valid);

            var truncated = Chain(100m, 120m, 110m, 200m, 150m, 190m);
            var short5 = WaveMath.Count(truncated);
            Check("a fifth that failed to exceed the third is flagged truncated", short5.Truncated);
            Check("but is still a valid count", short5.Valid);

            var downward = Chain(200m, 180m, 190m, 140m, 160m, 120m);
            var downCount = WaveMath.Count(downward);
            Check("a five down counts the same way", downCount.Valid);
            Check("and knows it is down", !downCount.Up);

            Check("fewer than five rotations is not a count",
                  !WaveMath.Count(Chain(100m, 120m, 110m, 160m)).Valid);
            Check("no rotations at all is not a count", !WaveMath.Count(null).Valid);

            // A list that does not chain pivot to pivot is refused rather than counted.
            var brokenChain = Chain(100m, 120m, 110m, 160m, 140m, 180m);
            var fixup = brokenChain[2];
            fixup.FromPrice = 999m;
            brokenChain[2] = fixup;
            Check("rotations that do not chain are refused",
                  WaveMath.Count(brokenChain).Failure.Contains("chain"));

            // Searching back finds an impulse that finished a few rotations ago.
            var after = Chain(100m, 120m, 110m, 160m, 140m, 180m, 150m, 170m);
            var recent = WaveMath.Recent(after, 6, out var endIndex);
            Check("an impulse that completed earlier is still found", recent.Valid);
            Check("and the caller is told where it ended", endIndex == 4);

            var correction = WaveMath.Correction(after, recent, endIndex);
            Check("the rotations after it read as a correction", correction.Present);
            Check("running the other way", correction.Up == !recent.Up);
            Check("with a retracement measured against the impulse",
                  correction.Retrace > 0m && correction.Retrace < 1m);

            Check("there is no correction after an invalid count",
                  !WaveMath.Correction(after, new WaveCount(), 4).Present);

            Check("a count that ends on the last rotation has nothing after it",
                  !WaveMath.Correction(clean, count, clean.Count - 1).Present);

            // Character: overlap is the whole test, and it needs no count.
            var impulsive = Chain(100m, 200m, 180m, 280m, 260m, 360m, 340m, 440m);
            Check("legs clear of each other read as impulsive",
                  WaveMath.Character(impulsive, 8, 0.5m) == Sequence.Impulsive);

            var choppy = Chain(100m, 120m, 100m, 120m, 100m, 120m, 100m, 120m);
            Check("legs treading on each other read as corrective",
                  WaveMath.Character(choppy, 8, 0.5m) == Sequence.Corrective);

            Check("too few rotations says so rather than guessing",
                  WaveMath.Character(Chain(100m, 120m, 110m), 8, 0.5m) == Sequence.TooShort);
        }

        #endregion

        #region VWAP

        private static void VwapTests()
        {
            var track = new VwapTrack(VwapAnchor.Session);
            track.Reset(new DateTime(2026, 9, 10), new DateTime(2026, 9, 9, 17, 0, 0), true);

            track.Add(100m, 100m);
            track.Add(102m, 100m);

            Near("the VWAP is the volume-weighted average", track.Value, 101m, 0.0001m);
            Check("it is valid once volume has traded", track.Valid);
            Near("the deviation is measured from the same sums", track.Sd, 1m, 0.0001m);

            track.Add(110m, 800m);
            Check("heavier volume pulls the VWAP toward it", track.Value > 105m);

            Near("one deviation out is the VWAP plus the deviation",
                 track.Band(1), track.Value + track.Sd, 0.0001m);
            Near("and minus, going down", track.Band(-1), track.Value - track.Sd, 0.0001m);
            Near("band zero is the VWAP itself", track.Band(0), track.Value, 0.0001m);

            var flat = new VwapTrack(VwapAnchor.Day);
            flat.Reset(new DateTime(2026, 9, 10), new DateTime(2026, 9, 9, 17, 0, 0), true);
            flat.Add(100m, 50m);
            flat.Add(100m, 50m);
            Check("a flat series has no deviation, and never a negative one", flat.Sd == 0m);

            Check("a rising VWAP has a positive slope", track.Slope(3) > 0m);
            Check("slope over no bars is zero", track.Slope(0) == 0m);

            Check("price above the VWAP reads above",
                  track.SideOf(track.Value + 10m, 0.5m) == VwapSide.Above);
            Check("price below reads below",
                  track.SideOf(track.Value - 10m, 0.5m) == VwapSide.Below);
            Check("price within the tolerance reads at",
                  track.SideOf(track.Value, 0.5m) == VwapSide.At);
            Check("an empty track has no side",
                  new VwapTrack(VwapAnchor.Day).SideOf(100m, 0.5m) == VwapSide.Unknown);

            track.Reset(new DateTime(2026, 9, 11), new DateTime(2026, 9, 10, 17, 0, 0), true);
            Check("a reset drops everything accumulated", !track.Valid && track.Bars == 0);

            // A yearly VWAP built from three weeks of loaded bars is not a yearly VWAP.
            var partial = new VwapTrack(VwapAnchor.Year);
            partial.Reset(new DateTime(2026, 1, 1), new DateTime(2025, 12, 31, 17, 0, 0), false);
            partial.Add(100m, 10m);
            Check("an anchor the history does not reach is marked incomplete", !partial.Complete);

            var stack = new VwapStack();
            var tradeDate = new DateTime(2026, 9, 10);
            var sessionKey = ReadClock.OpenOf(tradeDate);
            var early = new DateTime(2000, 1, 1);

            stack.Add(tradeDate, sessionKey, sessionKey, 100m, 100m, early);
            stack.Add(tradeDate, sessionKey, sessionKey.AddMinutes(1), 104m, 100m, early);

            foreach (var anchor in VwapStack.Order)
                Check("the " + anchor + " anchor accumulated", stack[anchor].Valid);

            var text = stack.Describe(200m, 0.5m, out var above, out var below, out var order);
            Check("a price above everything is above every anchor", above == VwapStack.Order.Length);
            Check("and below none", below == 0);
            Check("the description names the anchors", text.Contains("yV") && text.Contains("sV"));

            stack.Describe(1m, 0.5m, out above, out below, out order);
            Check("a price under everything is below every anchor", below == VwapStack.Order.Length);

            // All anchors sit at the same value here, so the stack is neither up nor down but is
            // not broken either -- non-decreasing and non-increasing at once counts as ordered.
            Check("anchors all at one price are not a broken stack", order != 0);

            var week = new DateTime(2026, 9, 14);
            var nextWeek = new DateTime(2026, 9, 21);
            Check("two different weeks are different anchor instances",
                  ReadClock.AnchorKey(week, VwapAnchor.Week) != ReadClock.AnchorKey(nextWeek, VwapAnchor.Week));

            var rolling = new VwapStack();
            rolling.Add(week, ReadClock.OpenOf(week), ReadClock.OpenOf(week), 100m, 100m, early);
            var weekly = rolling[VwapAnchor.Week].Value;
            rolling.Add(nextWeek, ReadClock.OpenOf(nextWeek), ReadClock.OpenOf(nextWeek), 200m, 100m, early);

            Check("a new week restarts the weekly VWAP", rolling[VwapAnchor.Week].Value == 200m);
            Check("but the monthly one carries on", rolling[VwapAnchor.Month].Value == 150m);
            Check("the first week really was measured", weekly == 100m);
        }

        #endregion

        #region Acceptance

        private static void AcceptanceTests()
        {
            var references = new List<Reference> { new Reference("prior VAH", 100m, 1) };
            var start = new DateTime(2026, 9, 10, 9, 0, 0);

            // Time needed 10 minutes, ground needed 8 ticks at a quarter-point tick = 2.00.
            var tracker = new AcceptanceTracker();

            void Bar(int index, decimal close, decimal high, decimal low)
            {
                tracker.Update(start.AddMinutes(index * 5), close, high, low, 5d, 0.25m, 0m,
                               references, 10d, 8m, index);
            }

            Bar(0, 99m, 99.5m, 98m);
            Check("the first bar only records which side we are on", tracker.Current() == null);

            Bar(1, 101m, 101.5m, 99m);
            var test = tracker.Current();
            Check("a close through the level opens a test", test != null);
            Check("the test knows the level it opened on", test != null && test.Price == 100m);
            Check("and the direction of the break", test != null && test.Up);
            Check("it is live", test != null && test.Live);

            Bar(2, 103m, 103m, 101m);
            Check("both thresholds met resolves the test",
                  tracker.Current() == null && tracker.Last() != null);
            var resolved = tracker.Last();
            Check("as accepted", resolved != null && resolved.State == AcceptanceState.Accepted);
            Check("with the reason spelled out", resolved != null && resolved.Why.Length > 0);
            Check("and value noted as not having followed", resolved != null && !resolved.ValueFollowed);

            // A wick through is not a break. Only a close is.
            var wicks = new AcceptanceTracker();
            wicks.Update(start, 99m, 99.5m, 98m, 5d, 0.25m, 0m, references, 10d, 8m, 0);
            wicks.Update(start.AddMinutes(5), 99m, 104m, 98m, 5d, 0.25m, 0m, references, 10d, 8m, 1);
            Check("a wick through the level does not open a test", wicks.Current() == null);

            // Closing back through before either threshold is a rejection, whatever the
            // excursion reached.
            var rejected = new AcceptanceTracker();
            rejected.Update(start, 99m, 99.5m, 98m, 5d, 0.25m, 0m, references, 60d, 8m, 0);
            rejected.Update(start.AddMinutes(5), 101m, 104m, 100.5m, 5d, 0.25m, 0m, references, 60d, 8m, 1);
            Check("the test is live after the break", rejected.Current() != null);

            rejected.Update(start.AddMinutes(10), 98m, 101m, 97m, 5d, 0.25m, 0m, references, 60d, 8m, 2);
            Check("a close back through resolves it", rejected.Current() == null);
            var thrown = rejected.Last();
            Check("as a rejection", thrown != null && thrown.State == AcceptanceState.Rejected);
            Check("even though the excursion had covered the ground",
                  thrown != null && thrown.MaxGroundTicks >= 8m);

            // Ground alone is not acceptance; time alone is not either.
            var groundOnly = new AcceptanceTracker();
            groundOnly.Update(start, 99m, 99.5m, 98m, 1d, 0.25m, 0m, references, 60d, 8m, 0);
            groundOnly.Update(start.AddMinutes(1), 104m, 104m, 100.5m, 1d, 0.25m, 0m, references, 60d, 8m, 1);
            Check("ground without time does not accept",
                  groundOnly.Current() != null && groundOnly.Current().Live);

            var timeOnly = new AcceptanceTracker();
            timeOnly.Update(start, 99m, 99.5m, 98m, 60d, 0.25m, 0m, references, 10d, 400m, 0);
            timeOnly.Update(start.AddMinutes(60), 100.25m, 100.3m, 100m, 60d, 0.25m, 0m, references, 10d, 400m, 1);
            Check("time without ground does not accept either",
                  timeOnly.Current() != null && timeOnly.Current().Live);

            // With no rhythm there are no thresholds, and the tester says so by not testing.
            var noRhythm = new AcceptanceTracker();
            noRhythm.Update(start, 99m, 99.5m, 98m, 5d, 0.25m, 0m, references, 0d, 0m, 0);
            noRhythm.Update(start.AddMinutes(5), 101m, 101m, 99m, 5d, 0.25m, 0m, references, 0d, 0m, 1);
            // Not just "nothing live": a test opened with zero thresholds resolves as ACCEPTED
            // on its first bar, which would leave Current() empty and look identical.
            Check("with no measured rhythm no test is opened at all",
                  noRhythm.Current() == null && noRhythm.Last() == null);

            // Value following the break is the strongest form of acceptance and is reported.
            var withValue = new AcceptanceTracker();
            withValue.Update(start, 99m, 99.5m, 98m, 5d, 0.25m, 0m, references, 10d, 8m, 0);
            withValue.Update(start.AddMinutes(5), 101m, 101.5m, 99m, 5d, 0.25m, 102m, references, 10d, 8m, 1);
            withValue.Update(start.AddMinutes(10), 103m, 103m, 101m, 5d, 0.25m, 102m, references, 10d, 8m, 2);
            var followed = withValue.Last();
            Check("value crossing to the far side is noticed", followed != null && followed.ValueFollowed);
            Check("and said so in the reason", followed != null && followed.Why.Contains("value"));

            var progress = new AcceptanceTracker();
            progress.Update(start, 99m, 99.5m, 98m, 5d, 0.25m, 0m, references, 20d, 8m, 0);
            progress.Update(start.AddMinutes(5), 100.25m, 100.5m, 99.5m, 5d, 0.25m, 0m, references, 20d, 8m, 1);
            var live = progress.Current();
            Check("a part-way test is still live", live != null);
            Near("time progress is a fraction of what is needed",
                 live == null ? -1d : live.TimeProgress, 0.25d, 0.001d);
            Check("ground progress is too",
                  live != null && live.GroundProgress > 0d && live.GroundProgress < 1d);

            // Nearest: the level the market is actually working on.
            var levels = new List<Reference>
            {
                new Reference("prior POC", 100m, 0),
                new Reference("prior VAH", 110m, 1)
            };

            Check("the nearer level is the one being worked",
                  AcceptanceMath.Nearest(levels, 101m, 40m, 0.25m, out var near) && near.Name == "prior POC");

            Check("a level out of range is not returned",
                  !AcceptanceMath.Nearest(levels, 500m, 40m, 0.25m, out _));

            Check("no levels means nothing near", !AcceptanceMath.Nearest(null, 100m, 40m, 0.25m, out _));

            // A tie breaks on rank, so the same level wins every frame rather than alternating.
            var tied = new List<Reference>
            {
                new Reference("second", 100m, 5),
                new Reference("first", 100m, 0)
            };
            Check("a tie for nearest breaks on rank",
                  AcceptanceMath.Nearest(tied, 100m, 40m, 0.25m, out var pick) && pick.Name == "first");

            Check("minutes read as minutes below ninety", Format.Minutes(45d) == "45 min");
            Check("and as hours above it", Format.Minutes(180d).Contains("h"));
            Check("a positive number carries its sign", Format.Signed(120m) == "+120");
            Check("a negative one carries its own", Format.Signed(-120m) == "-120");
        }

        #endregion

        #region Auction theory

        private static void AuctionTests()
        {
            Check("value entirely above yesterday's is higher",
                  AuctionMath.Relate(120m, 130m, 100m, 110m, 1m) == ValueRelation.Higher);

            Check("value entirely below is lower",
                  AuctionMath.Relate(80m, 90m, 100m, 110m, 1m) == ValueRelation.Lower);

            Check("value that pokes above but still overlaps is overlapping higher",
                  AuctionMath.Relate(105m, 115m, 100m, 110m, 1m) == ValueRelation.OverlappingHigher);

            Check("and the other way is overlapping lower",
                  AuctionMath.Relate(95m, 105m, 100m, 110m, 1m) == ValueRelation.OverlappingLower);

            Check("value inside yesterday's is inside",
                  AuctionMath.Relate(103m, 107m, 100m, 110m, 1m) == ValueRelation.Inside);

            Check("value wrapped around yesterday's is outside",
                  AuctionMath.Relate(95m, 115m, 100m, 110m, 1m) == ValueRelation.Outside);

            // A one-tick difference is not a migration. Below the tolerance it is the same area.
            Check("a difference under the tolerance is unchanged",
                  AuctionMath.Relate(100.5m, 110.5m, 100m, 110m, 1m) == ValueRelation.Unchanged);

            Check("above the tolerance it is not",
                  AuctionMath.Relate(102m, 112m, 100m, 110m, 1m) != ValueRelation.Unchanged);

            Check("an inverted area is unknown rather than guessed",
                  AuctionMath.Relate(110m, 100m, 100m, 110m, 1m) == ValueRelation.Unknown);

            Check("every relation has words", AuctionMath.Describe(ValueRelation.OverlappingHigher).Length > 0);

            // Balancing and imbalancing come from two independent measurements.
            Check("heavy overlap and low efficiency is balancing",
                  AuctionMath.Judge(0.8m, 0.1m, true, true, 0.5m, 0.4m, out _) == Condition.Balancing);

            Check("no overlap and high efficiency is imbalancing",
                  AuctionMath.Judge(0.1m, 0.9m, true, true, 0.5m, 0.4m, out _) == Condition.Imbalancing);

            // When they disagree the answer is Transitioning, not a casting vote.
            Check("still inside value but travelling one way is transitioning",
                  AuctionMath.Judge(0.8m, 0.9m, true, true, 0.5m, 0.4m, out _) == Condition.Transitioning);

            Check("and the other disagreement is too",
                  AuctionMath.Judge(0.1m, 0.1m, true, true, 0.5m, 0.4m, out _) == Condition.Transitioning);

            Check("with nothing measured the condition is unknown",
                  AuctionMath.Judge(0m, 0m, false, false, 0.5m, 0.4m, out _) == Condition.Unknown);

            Check("one measurement alone still gives an answer",
                  AuctionMath.Judge(0m, 0.9m, false, true, 0.5m, 0.4m, out _) == Condition.Imbalancing);

            AuctionMath.Judge(0.8m, 0.1m, true, true, 0.5m, 0.4m, out var why);
            Check("the reason carries both numbers", why.Contains("overlap") && why.Contains("efficiency"));

            // Day types, measured against the initial balance.
            var ib = new InitialBalance { Set = true, High = 110m, Low = 100m };
            Check("the initial balance range is high minus low", ib.Range == 10m);
            Near("extension is measured in initial balances", ib.ExtensionUp(115m), 0.5m, 0.001m);
            Check("no extension below the initial balance low", ib.ExtensionDown(105m) == 0m);

            Check("an initial balance that held is a normal day",
                  AuctionMath.ClassifyDay(ib, 111m, 99.5m, ProfileShape.Normal, 1.0m, 0.15m, out _) ==
                  DayType.Normal);

            Check("half an initial balance of extension is a normal variation",
                  AuctionMath.ClassifyDay(ib, 115m, 100m, ProfileShape.Normal, 1.0m, 0.15m, out _) ==
                  DayType.NormalVariation);

            Check("more than a whole one is a trend day",
                  AuctionMath.ClassifyDay(ib, 125m, 100m, ProfileShape.Normal, 1.0m, 0.15m, out _) ==
                  DayType.Trend);

            Check("extension both ways is a neutral day",
                  AuctionMath.ClassifyDay(ib, 116m, 94m, ProfileShape.Normal, 1.0m, 0.15m, out _) ==
                  DayType.Neutral);

            Check("two distributions beat every extension reading",
                  AuctionMath.ClassifyDay(ib, 125m, 100m, ProfileShape.DoubleDistribution, 1.0m, 0.15m, out _) ==
                  DayType.DoubleDistribution);

            Check("before the initial balance closes the day is still forming",
                  AuctionMath.ClassifyDay(new InitialBalance(), 125m, 90m, ProfileShape.Normal,
                                          1.0m, 0.15m, out _) == DayType.Forming);

            AuctionMath.ClassifyDay(ib, 125m, 100m, ProfileShape.Normal, 1.0m, 0.15m, out var dayWhy);
            Check("the day type carries its measurement", dayWhy.Contains("IB"));

            // The point of control trail: which way value is going, over TIME not bars.
            var trail = new PocTrail();
            var start = new DateTime(2026, 9, 10, 9, 0, 0);

            Check("an empty trail measures nothing", !trail.Measure(60d, 0.25m).Set);

            trail.Add(start, 100m);
            Check("one point is not a migration", !trail.Measure(60d, 0.25m).Set);

            trail.Add(start.AddMinutes(30), 101m);
            var migration = trail.Measure(60d, 0.25m);
            Check("two points are", migration.Set);
            Near("the migration is in ticks", migration.Ticks, 4m, 0.001m);
            Check("and it reads as higher", migration.Direction == "higher");
            Near("over the span it actually covered", migration.Minutes, 30d, 0.001d);

            trail.Add(start.AddMinutes(200), 99m);
            var windowed = trail.Measure(60d, 0.25m);
            Check("the window really is a window -- the old point drops out",
                  windowed.Ticks < 0m);

            Check("the latest point is the developing one", trail.Latest == 99m);

            trail.Clear();
            Check("clearing empties the trail", !trail.Measure(60d, 0.25m).Set && trail.Latest == 0m);
        }

        #endregion

        #region Orderflow

        private static void OrderflowTests()
        {
            var builder = new ProfileBuilder();
            builder.Add(100.00m, 100m, 80m, 20m, 0L, 0);
            builder.Add(100.25m, 200m, 150m, 50m, 0L, 0);
            builder.Add(100.50m, 100m, 20m, 80m, 0L, 0);
            var profile = builder.Build(0.25m, 60000);

            var exact = OrderflowMath.AtLevel(profile, 100.25m, 0);
            Check("the exact tick is summed", exact.Volume == 200m);

            var band = OrderflowMath.AtLevel(profile, 100.25m, 1);
            Check("a band takes the neighbours in too", band.Volume == 400m);
            Check("with the aggression split out", band.Bid == 250m && band.Ask == 150m);
            Near("and the buyers' share computed", band.AskShare, 0.375d, 0.001d);

            Check("a level off the ladder has nothing", !OrderflowMath.AtLevel(profile, 500m, 0).Have);
            Check("a null profile has nothing", !OrderflowMath.AtLevel(null, 100m, 1).Have);

            var neutral = new LevelFlow();
            Near("an empty level is not lopsided either way", neutral.AskShare, 0.5d, 0.001d);

            // Absorption needs all three: heavy, one-sided, and no ground given up.
            var heavy = new LevelFlow { Have = true, Volume = 400m, Bid = 300m, Ask = 100m };

            Check("heavy, one-sided and going nowhere is absorption",
                  OrderflowMath.IsAbsorption(heavy, 1000m, 2m, 0.04m, 0.62d, 6m, out var absorbWhy));
            Check("and the reason carries all three numbers",
                  absorbWhy.Contains("session volume") && absorbWhy.Contains("buyers"));

            Check("the same flow that moved price is a breakout, not absorption",
                  !OrderflowMath.IsAbsorption(heavy, 1000m, 40m, 0.04m, 0.62d, 6m, out _));

            var light = new LevelFlow { Have = true, Volume = 5m, Bid = 4m, Ask = 1m };
            Check("light trade is not absorption however lopsided",
                  !OrderflowMath.IsAbsorption(light, 1000m, 0m, 0.04m, 0.62d, 6m, out _));

            var even = new LevelFlow { Have = true, Volume = 400m, Bid = 200m, Ask = 200m };
            Check("a two-sided fight is not absorption",
                  !OrderflowMath.IsAbsorption(even, 1000m, 0m, 0.04m, 0.62d, 6m, out _));

            Check("no volume at the level is not absorption",
                  !OrderflowMath.IsAbsorption(new LevelFlow(), 1000m, 0m, 0.04m, 0.62d, 6m, out _));

            // Open interest against price: the four standard readings.
            Check("price up on rising open interest is new longs",
                  OrderflowMath.ReadOpenInterest(500m, 20m, 50m, 4m) == OiRead.NewLongs);

            Check("price up on falling open interest is short covering",
                  OrderflowMath.ReadOpenInterest(-500m, 20m, 50m, 4m) == OiRead.ShortCovering);

            Check("price down on rising open interest is new shorts",
                  OrderflowMath.ReadOpenInterest(500m, -20m, 50m, 4m) == OiRead.NewShorts);

            Check("price down on falling open interest is long liquidation",
                  OrderflowMath.ReadOpenInterest(-500m, -20m, 50m, 4m) == OiRead.LongLiquidation);

            Check("too small a change either way is flat",
                  OrderflowMath.ReadOpenInterest(10m, 20m, 50m, 4m) == OiRead.Flat);

            Check("too small a price move is flat too",
                  OrderflowMath.ReadOpenInterest(500m, 1m, 50m, 4m) == OiRead.Flat);

            Check("new longs back an upward move", OrderflowMath.Backs(OiRead.NewLongs, true));
            Check("short covering does not", !OrderflowMath.Backs(OiRead.ShortCovering, true));
            Check("new shorts back a downward move", OrderflowMath.Backs(OiRead.NewShorts, false));

            // Divergence at the turn: a new extreme the tape did not pay for.
            var pivots = new List<PivotFlow>
            {
                new PivotFlow { IsHigh = true, Price = 100m, CumulativeDelta = 500m, Bar = 10 },
                new PivotFlow { IsHigh = false, Price = 90m, CumulativeDelta = 100m, Bar = 20 },
                new PivotFlow { IsHigh = true, Price = 105m, CumulativeDelta = 300m, Bar = 30 }
            };

            Check("a higher high on less delta is a divergence",
                  OrderflowMath.Divergence(pivots, true, out var divergenceWhy));
            Check("and the reason names both readings", divergenceWhy.Contains("higher high"));

            var paid = new List<PivotFlow>
            {
                new PivotFlow { IsHigh = true, Price = 100m, CumulativeDelta = 300m, Bar = 10 },
                new PivotFlow { IsHigh = true, Price = 105m, CumulativeDelta = 800m, Bar = 30 }
            };
            Check("a higher high the tape paid for is not a divergence",
                  !OrderflowMath.Divergence(paid, true, out _));

            var lower = new List<PivotFlow>
            {
                new PivotFlow { IsHigh = true, Price = 105m, CumulativeDelta = 800m, Bar = 10 },
                new PivotFlow { IsHigh = true, Price = 100m, CumulativeDelta = 300m, Bar = 30 }
            };
            Check("a LOWER high is not a divergence, it is just a lower high",
                  !OrderflowMath.Divergence(lower, true, out _));

            Check("one pivot cannot diverge from anything",
                  !OrderflowMath.Divergence(new List<PivotFlow> { pivots[0] }, true, out _));

            Check("no pivots at all cannot either", !OrderflowMath.Divergence(null, true, out _));

            // Silence is a distinct answer from disagreement.
            var quiet = new OrderflowRead();
            Check("a chart with no tape is silent, not contradicting",
                  OrderflowMath.Judge(quiet, true, 200m, out var quietWhy) == FlowVerdict.Silent);
            Check("and says why", quietWhy.Contains("no footprint"));

            var behind = new OrderflowRead
            {
                HaveFootprint = true,
                LegDelta = 1000m,
                Oi = OiRead.NewLongs,
                OiChange = 500m
            };
            Check("delta and open interest both with the move confirms it",
                  OrderflowMath.Judge(behind, true, 200m, out _) == FlowVerdict.Confirms);

            Check("the same flow against a short diverges",
                  OrderflowMath.Judge(behind, false, 200m, out _) == FlowVerdict.Diverges);

            var split = new OrderflowRead
            {
                HaveFootprint = true,
                LegDelta = 1000m,
                Oi = OiRead.ShortCovering,
                OiChange = -500m
            };
            Check("buying into short covering is mixed",
                  OrderflowMath.Judge(split, true, 200m, out _) == FlowVerdict.Mixed);

            var tiny = new OrderflowRead { HaveFootprint = true, LegDelta = 10m };
            Check("delta under the threshold is no opinion rather than a vote",
                  OrderflowMath.Judge(tiny, true, 200m, out _) == FlowVerdict.Silent);

            var absorbed = new OrderflowRead
            {
                HaveFootprint = true,
                Absorption = true,
                LevelName = "prior VAH",
                AtLevel = new LevelFlow { Have = true, Volume = 400m, Bid = 100m, Ask = 300m }
            };
            Check("buyers being absorbed is against the upward move",
                  OrderflowMath.Judge(absorbed, true, 200m, out _) == FlowVerdict.Diverges);
            Check("and for the downward one",
                  OrderflowMath.Judge(absorbed, false, 200m, out _) == FlowVerdict.Confirms);

            Check("every verdict has a word", OrderflowMath.Describe(FlowVerdict.Mixed) == "MIXED");
            Check("and every open-interest reading does",
                  OrderflowMath.Describe(OiRead.ShortCovering).Length > 0);
        }

        #endregion

        #region The read itself

        private static ReadInputs Inputs()
        {
            var input = new ReadInputs
            {
                Now = new DateTime(2026, 9, 10, 9, 45, 0),
                TickSize = 0.25m,
                Price = 105m,
                ZoneAbbrev = "CDT"
            };

            var builder = new ProfileBuilder();
            for (var i = 0; i < 20; i++)
                builder.Add(100m + i * 0.25m, 10m + i, 5m, 5m, 1000L, i / 4);

            input.Session = builder.Build(0.25m, 60000);
            ProfileMath.ComputeValueArea(input.Session, 70m, ProfileBasis.Volume);

            input.Rhythm = RhythmMath.Measure(Chain(100m, 120m, 100m, 130m, 105m, 140m, 110m, 150m,
                                                    120m, 160m, 130m), 40, 5);

            input.Leg = new LegRead
            {
                Active = true,
                Up = true,
                Ticks = 10m,
                Minutes = 5d,
                Maturity = LegMaturity.AtTheTurn,
                FromBar = 1
            };

            return input;
        }

        private static void ModelTests()
        {
            var box = ReadModel.Build(Inputs());
            Check("the read builds", box != null && box.Lines.Count > 0);

            var text = string.Empty;
            foreach (var line in box.Lines) text += line.Label + " " + line.Text + "\n";

            Check("it opens with the header", box.Lines[0].Label.StartsWith("OCEANS READ"));
            Check("it names the condition", text.Contains("CONDITION"));
            Check("it says where price is", text.Contains("LOCATION"));
            Check("it reads the structure", text.Contains("STRUCTURE"));
            Check("it reports the rhythm", text.Contains("RHYTHM"));
            Check("it reports developing value", text.Contains("VALUE"));
            Check("it reports acceptance", text.Contains("ACCEPTANCE"));
            Check("it reports the tape", text.Contains("ORDERFLOW"));
            Check("and it ends on a stance", text.Contains("READ"));

            // A clock that has not resolved stops everything: a misplaced level looks right.
            var broken = Inputs();
            broken.ClockError = "could not tell whether bar times are UTC or Houston time";
            var refused = ReadModel.Build(broken);
            Check("an unresolved clock stands the read down", refused.Stance == Stance.StandAside);
            Check("and says why", refused.StanceWhy.Contains("clock"));
            Check("without printing any of the measurements", refused.Lines.Count <= 3);

            // No rhythm, no thresholds, nothing to call early or late.
            var noRhythm = Inputs();
            noRhythm.Rhythm = new Rhythm();
            var standing = ReadModel.Build(noRhythm);
            Check("no measured rhythm stands the read down too", standing.Stance == Stance.StandAside);
            Check("and names the reason", standing.StanceWhy.Contains("rhythm"));

            // Balance puts us in the fade book.
            var balanced = Inputs();
            balanced.Auction.Condition = Condition.Balancing;
            var fade = ReadModel.Build(balanced);
            Check("balancing puts us in the fade book", fade.Playbook == Playbook.FadeTheEdge);
            Check("with gates to meet", fade.Gates.Count > 0);

            // Imbalance puts us in the other one.
            var moving = Inputs();
            moving.Auction.Condition = Condition.Imbalancing;
            var go = ReadModel.Build(moving);
            Check("imbalancing puts us in the go-with book", go.Playbook == Playbook.GoWithTheMove);

            // Disagreement is its own answer, and it is not a trade.
            var torn = Inputs();
            torn.Auction.Condition = Condition.Transitioning;
            var aside = ReadModel.Build(torn);
            Check("transitioning is stand aside", aside.Stance == Stance.StandAside);
            Check("with no gates pretending otherwise", aside.Gates.Count == 0);

            // Every condition met is the only way to ACT.
            var ready = Inputs();
            ready.Auction.Condition = Condition.Imbalancing;
            ready.Auction.ValueMigration = new Migration { Set = true, Ticks = 20m, Minutes = 60d };
            ready.LastAcceptance = new AcceptanceTest
            {
                Name = "prior VAH",
                Up = true,
                State = AcceptanceState.Accepted,
                Why = "time, ground and value all beyond"
            };
            ready.VwapAbove = 5;
            ready.VwapBelow = 0;
            ready.VwapText = "yV+ qV+ mV+ wV+ dV+";
            ready.Flow.HaveFootprint = true;
            ready.Flow.Verdict = FlowVerdict.Confirms;

            var acting = ReadModel.Build(ready);
            Check("every gate met is an ACT", acting.Stance == Stance.Act);
            Check("with a side", acting.Direction == 1);

            // One gate short is PREPARE, and it names the gate.
            var almost = Inputs();
            almost.Auction.Condition = Condition.Imbalancing;
            almost.Auction.ValueMigration = new Migration { Set = true, Ticks = 20m, Minutes = 60d };
            almost.LastAcceptance = ready.LastAcceptance;
            almost.VwapAbove = 5;
            almost.VwapText = "yV+ qV+ mV+ wV+ dV+";
            almost.Flow.HaveFootprint = true;
            almost.Flow.Verdict = FlowVerdict.Mixed;

            var preparing = ReadModel.Build(almost);
            Check("one gate short is PREPARE", preparing.Stance == Stance.Prepare);
            Check("and it names the gate", preparing.StanceWhy.Contains("orderflow"));

            // Two short is WAIT.
            var early = Inputs();
            early.Auction.Condition = Condition.Imbalancing;
            early.Auction.ValueMigration = new Migration { Set = true, Ticks = 20m, Minutes = 60d };
            var waiting = ReadModel.Build(early);
            Check("more than one gate short is WAIT", waiting.Stance == Stance.Wait);

            // A chart with no footprint says so, loudly, rather than reading value off closes
            // as though it were the tape.
            var noTape = Inputs();
            noTape.Notes.Add("no footprint on this chart");
            var warned = ReadModel.Build(noTape);
            var warnedText = string.Empty;
            foreach (var line in warned.Lines) warnedText += line.Text;
            Check("a missing footprint is printed in the box", warnedText.Contains("no footprint"));

            Check("a quarter-point tick prints two decimals", ReadModel.Decimals(0.25m) == 2);
            Check("a whole-number tick prints none", ReadModel.Decimals(1m) == 0);
            Check("a five-decimal tick prints five", ReadModel.Decimals(0.00001m) == 5);
            Check("a zero tick falls back to two rather than looping", ReadModel.Decimals(0m) == 2);

            Check("a null input still produces a box", ReadModel.Build(null).Lines.Count > 0);
        }

        #endregion
    }
}
