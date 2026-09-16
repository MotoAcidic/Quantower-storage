using System;
using System.Collections.Generic;

namespace OceansAnchor
{
    /// <summary>A prior session POC and whether price has been back to it since.</summary>
    public sealed class NakedPoc
    {
        public decimal Price;
        public DateTime TradeDate;
        public int Bar;
        public bool Naked = true;
    }

    /// <summary>
    /// Tracks which prior POCs are still untested. A naked POC is the strongest single level
    /// this indicator draws, and the moment price trades through it the reason it was strong is
    /// gone -- so the test has to run on every bar after it was born, not on a rebuild.
    /// </summary>
    public sealed class NakedPocTracker
    {
        private readonly List<NakedPoc> _pocs = new List<NakedPoc>();

        public IList<NakedPoc> All { get { return _pocs; } }

        public void Clear() { _pocs.Clear(); }

        public void Add(decimal price, DateTime tradeDate, int bar, int keepSessions)
        {
            foreach (var p in _pocs)
                if (p.TradeDate == tradeDate) return; // one POC per session

            _pocs.Add(new NakedPoc { Price = price, TradeDate = tradeDate, Bar = bar });

            _pocs.Sort(delegate (NakedPoc a, NakedPoc b) { return a.TradeDate.CompareTo(b.TradeDate); });

            while (_pocs.Count > keepSessions) _pocs.RemoveAt(0);
        }

        /// <summary>
        /// A bar whose range covers the POC has tested it. Only bars AFTER the session that
        /// created it count -- the session that printed the POC obviously traded there.
        /// </summary>
        public void NoteBar(int bar, decimal high, decimal low)
        {
            foreach (var p in _pocs)
            {
                if (!p.Naked || bar <= p.Bar) continue;
                if (low <= p.Price && p.Price <= high) p.Naked = false;
            }
        }

        public bool IsNaked(decimal price)
        {
            foreach (var p in _pocs)
                if (p.Price == price) return p.Naked;

            return false;
        }
    }

    /// <summary>Everything the zone builder needs for one rebuild.</summary>
    public sealed class ZoneBuildInput
    {
        public SessionProfile PriorRth;
        public IDictionary<decimal, decimal> Composite;
        public SessionProfile Overnight;

        public NakedPocTracker Naked;

        public decimal TickSize;
        public HvnSettings Hvn = new HvnSettings();

        public int MaxZones = 4;

        /// <summary>Overnight needs this share of prior RTH volume before its POC is admitted.</summary>
        public decimal MinOnVolumePct = 15m;

        /// <summary>Average daily range. Zero disables the distance filter rather than dimming everything.</summary>
        public decimal Adr;
        public decimal SessionOpen;
        public decimal DistanceAdrMult = 1.5m;

        /// <summary>Bar the zones become drawable from.</summary>
        public int StartBar;

        public DateTime BornSession;
    }

    public static class ZoneBuilder
    {
        /// <summary>
        /// Turns the profiles into the small ranked set of zones the state machine is allowed to
        /// arm on.
        ///
        /// The cap is the point. A chart with fourteen shelves on it is a volume profile, and a
        /// volume profile has never told anyone where to put a trade. Four ranked zones is a
        /// decision; everything below the cut is noise that would have talked you into a
        /// mediocre location.
        /// </summary>
        public static List<Zone> Build(ZoneBuildInput input)
        {
            var zones = new List<Zone>();
            if (input == null || input.TickSize <= 0m) return zones;

            var compositeShelves = input.Composite != null
                ? ProfileMath.ExtractShelves(input.Composite, input.TickSize, input.Hvn)
                : new List<HvnShelf>();

            AddPriorRthZones(zones, input, compositeShelves);
            AddCompositeZones(zones, input, compositeShelves);
            AddOvernightZone(zones, input);

            var kept = Rank(zones, input.MaxZones, input.SessionOpen);

            foreach (var z in kept)
            {
                z.StartBar = input.StartBar;
                z.BornSession = input.BornSession;
                z.TooFar = IsTooFar(z, input);
            }

            return kept;
        }

        /// <summary>
        /// The prior regular-hours POC, plus the second distribution when the day had one.
        ///
        /// The POC gets a zone by construction -- it is the global maximum of its own ladder, so
        /// it passes both the peak test and the outlier test. That is deliberate: the prior RTH
        /// POC is the level this whole playbook is named after, and a threshold change must
        /// never be able to make it vanish.
        /// </summary>
        private static void AddPriorRthZones(List<Zone> zones, ZoneBuildInput input,
                                             List<HvnShelf> compositeShelves)
        {
            var rth = input.PriorRth;
            if (rth == null || rth.VolByPrice.Count == 0) return;

            var shelves = ProfileMath.ExtractShelves(rth.VolByPrice, input.TickSize, input.Hvn);
            if (shelves.Count == 0) return;

            HvnShelf pocShelf = null;
            foreach (var s in shelves)
                if (s.Contains(rth.Poc)) { pocShelf = s; break; }

            if (pocShelf == null) pocShelf = Strongest(shelves);

            var naked = input.Naked != null && input.Naked.IsNaked(rth.Poc);
            var onComposite = InAnyShelf(compositeShelves, rth.Poc);

            zones.Add(new Zone
            {
                Bottom = pocShelf.Bottom,
                Top = pocShelf.Top,
                Poc = rth.Poc,
                Kind = naked && onComposite ? ZoneKind.NakedPoc : ZoneKind.PriorRthPoc,
                Naked = naked,
                Rank = naked && onComposite ? 1 : 2
            });

            // The second distribution: only when a genuine LVN valley separates it, which is
            // what distinguishes a two-auction day from a wide single one.
            var second = SecondPeak(shelves, pocShelf);
            if (second == null) return;
            if (!ProfileMath.IsDoubleDistribution(rth.VolByPrice, input.TickSize, input.Hvn,
                                                  pocShelf, second)) return;

            var secondNaked = input.Naked != null && input.Naked.IsNaked(second.Peak);

            zones.Add(new Zone
            {
                Bottom = second.Bottom,
                Top = second.Top,
                Poc = second.Peak,
                Kind = ZoneKind.SecondaryPoc,
                Naked = secondNaked,
                Rank = secondNaked && InAnyShelf(compositeShelves, second.Peak) ? 1 : 2
            });
        }

        /// <summary>
        /// Composite shelves that the prior session did not already account for. These are the
        /// levels the last two weeks agree on but yesterday did not, which is exactly the kind
        /// of level that gets defended by someone who has been there a while.
        /// </summary>
        private static void AddCompositeZones(List<Zone> zones, ZoneBuildInput input,
                                              List<HvnShelf> compositeShelves)
        {
            foreach (var shelf in compositeShelves)
            {
                if (OverlapsAny(zones, shelf.Bottom, shelf.Top)) continue;

                var naked = input.Naked != null && input.Naked.IsNaked(shelf.Peak);

                zones.Add(new Zone
                {
                    Bottom = shelf.Bottom,
                    Top = shelf.Top,
                    Poc = shelf.Peak,
                    Kind = naked ? ZoneKind.NakedPoc : ZoneKind.CompositeHvn,
                    Naked = naked,
                    Rank = naked ? 1 : 3
                });
            }
        }

        /// <summary>
        /// The overnight POC, but only from an overnight that actually traded. A thin Globex
        /// session builds a POC out of a few hundred contracts, and treating that as structure
        /// is how you end up long into the cash open for no reason.
        /// </summary>
        private static void AddOvernightZone(List<Zone> zones, ZoneBuildInput input)
        {
            var on = input.Overnight;
            if (on == null || on.VolByPrice.Count == 0) return;

            var rthVol = input.PriorRth != null ? input.PriorRth.TotalVol : 0m;
            if (rthVol <= 0m) return;

            if (on.TotalVol * 100m < input.MinOnVolumePct * rthVol) return;

            var shelves = ProfileMath.ExtractShelves(on.VolByPrice, input.TickSize, input.Hvn);
            if (shelves.Count == 0) return;

            HvnShelf pocShelf = null;
            foreach (var s in shelves)
                if (s.Contains(on.Poc)) { pocShelf = s; break; }

            if (pocShelf == null) pocShelf = Strongest(shelves);

            if (OverlapsAny(zones, pocShelf.Bottom, pocShelf.Top)) return;

            zones.Add(new Zone
            {
                Bottom = pocShelf.Bottom,
                Top = pocShelf.Top,
                Poc = on.Poc,
                Kind = ZoneKind.OvernightPoc,
                Naked = false,
                Rank = 4
            });
        }

        /// <summary>
        /// Best rank first, then nearest the session open, capped.
        ///
        /// The proximity tiebreak is what makes the cap mean something. Twelve composite shelves
        /// all score rank 3, and without a second key the four that survive are whichever four
        /// the sort happened to leave in front -- which can easily be four levels sixty points
        /// away while the shelf price is actually approaching gets dropped.
        /// </summary>
        private static List<Zone> Rank(List<Zone> zones, int maxZones, decimal reference)
        {
            zones.Sort(delegate (Zone a, Zone b)
            {
                if (a.Rank != b.Rank) return a.Rank.CompareTo(b.Rank);

                if (reference > 0m)
                {
                    var da = Math.Abs(a.Poc - reference);
                    var db = Math.Abs(b.Poc - reference);
                    if (da != db) return da.CompareTo(db);
                }

                return b.Kind.CompareTo(a.Kind);
            });

            if (maxZones > 0 && zones.Count > maxZones)
                zones.RemoveRange(maxZones, zones.Count - maxZones);

            return zones;
        }

        /// <summary>
        /// Zones this far from the open do not arm. Price reaching them means the day already
        /// went somewhere, and a mean-reversion fade at the end of a 1.5 ADR run is the trade
        /// that takes the account, not the one that builds it.
        /// </summary>
        private static bool IsTooFar(Zone z, ZoneBuildInput input)
        {
            if (input.Adr <= 0m || input.SessionOpen <= 0m || input.DistanceAdrMult <= 0m) return false;

            var distance = z.Poc > input.SessionOpen
                ? z.Bottom - input.SessionOpen
                : input.SessionOpen - z.Top;

            if (distance < 0m) distance = 0m;

            return distance > input.DistanceAdrMult * input.Adr;
        }

        private static HvnShelf Strongest(List<HvnShelf> shelves)
        {
            var best = shelves[0];
            foreach (var s in shelves)
                if (s.PeakVol > best.PeakVol) best = s;

            return best;
        }

        private static HvnShelf SecondPeak(List<HvnShelf> shelves, HvnShelf exclude)
        {
            HvnShelf best = null;
            foreach (var s in shelves)
            {
                if (ReferenceEquals(s, exclude)) continue;
                if (best == null || s.PeakVol > best.PeakVol) best = s;
            }

            return best;
        }

        private static bool InAnyShelf(List<HvnShelf> shelves, decimal price)
        {
            foreach (var s in shelves)
                if (s.Contains(price)) return true;

            return false;
        }

        private static bool OverlapsAny(List<Zone> zones, decimal bottom, decimal top)
        {
            foreach (var z in zones)
                if (z.Bottom <= top && bottom <= z.Top) return true;

            return false;
        }
    }

    public static class ZoneMaintenance
    {
        /// <summary>
        /// A zone gets used up by being traversed. The test is a bar that opens on one side and
        /// closes beyond the other: price walked straight through without anyone defending it.
        /// Three of those and whoever was there is gone, so the zone stops arming.
        ///
        /// A bar that merely touches the zone is not a traversal -- that is the setup, not the
        /// failure of one.
        /// </summary>
        public static void NoteBar(Zone zone, decimal open, decimal close)
        {
            if (zone == null || zone.Spent) return;

            var crossedUp = open < zone.Bottom && close > zone.Top;
            var crossedDown = open > zone.Top && close < zone.Bottom;

            if (crossedUp || crossedDown) zone.TraversalCount++;
        }

        /// <summary>
        /// Which way a zone is being tested, from where price came into it. Approaching from
        /// above makes it support and the trade a long.
        /// </summary>
        public static TestSide SideFor(Zone zone, decimal priorClose)
        {
            return priorClose >= zone.Poc ? TestSide.SupportLong : TestSide.ResistanceShort;
        }
    }
}
