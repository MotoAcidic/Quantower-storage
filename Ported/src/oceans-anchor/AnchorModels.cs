using System;
using System.Collections.Generic;

namespace OceansAnchor
{
    /// <summary>What kind of auction memory a zone is built from. Drives Rank.</summary>
    public enum ZoneKind
    {
        PriorRthPoc = 0,
        SecondaryPoc = 1,
        NakedPoc = 2,
        CompositeHvn = 3,
        OvernightPoc = 4
    }

    public enum SignalState
    {
        /// <summary>Price is nowhere near this zone.</summary>
        Dormant = 0,

        /// <summary>Price is inside a live, ranked, non-spent zone.</summary>
        Armed = 1,

        /// <summary>Qualifying absorption printed while Armed.</summary>
        Triggered = 2,

        /// <summary>Confirmation landed inside the clock. This is the one that alerts.</summary>
        Confirmed = 3,

        /// <summary>The clock ran out. Absorption that keeps absorbing is distribution.</summary>
        Expired = 4,

        /// <summary>The cluster extreme traded through. No re-fade this session.</summary>
        Broken = 5
    }

    /// <summary>Which way a test of the zone would be taken.</summary>
    public enum TestSide
    {
        SupportLong = 0,
        ResistanceShort = 1
    }

    /// <summary>
    /// Aggressor side, kept ATAS-free on purpose so the absorption logic can be tested without
    /// the platform. Maps 1:1 to ATAS.Indicators.TradeDirection at the indicator boundary.
    /// </summary>
    public enum Aggressor
    {
        Between = 0,
        Buy = 1,
        Sell = 2
    }

    /// <summary>Which engine produced an absorption event. Their agreement rate is a calibration output.</summary>
    public enum EventPath
    {
        /// <summary>Real cumulative market orders off the tape. The actual signal.</summary>
        Tape = 0,

        /// <summary>Reconstructed from footprint bid/ask so history renders on load.</summary>
        Cluster = 1
    }

    /// <summary>
    /// One HVN shelf: an outlier volume node wide enough to be a shelf rather than a line.
    ///
    /// Top/Bottom are the shelf edges, Poc the node peak. Everything the state machine and the
    /// renderer need about a zone lives here, because OnRender must not compute anything.
    /// </summary>
    public sealed class Zone
    {
        public decimal Top;
        public decimal Bottom;
        public decimal Poc;

        public ZoneKind Kind;

        /// <summary>1 best (naked POC that is also a composite shelf) through 4 (overnight POC).</summary>
        public int Rank;

        public DateTime BornSession;

        /// <summary>Bar the zone becomes drawable from -- the session that created it.</summary>
        public int StartBar;

        /// <summary>Full crosses without a reaction. Three and the zone is used up.</summary>
        public int TraversalCount;

        public bool Spent { get { return TraversalCount >= 3; } }

        /// <summary>Untouched since creation. Only meaningful for the naked-POC kinds.</summary>
        public bool Naked = true;

        /// <summary>True when the zone sits further than the distance cut from session open.</summary>
        public bool TooFar;

        public SignalState State = SignalState.Dormant;

        /// <summary>Which way this zone is being tested. Set when it arms.</summary>
        public TestSide Side;

        public int ArmedBar = -1;
        public int TriggeredBar = -1;

        /// <summary>
        /// Absorption extremes -- the stop reference. A long stops below ClusterLow: the trade
        /// is wrong the moment the size that was absorbing gets traded through.
        /// </summary>
        public decimal ClusterLow;
        public decimal ClusterHigh;

        public bool HasCluster;

        /// <summary>The episode currently in flight, or null. Finalized into CSV on resolution.</summary>
        public Episode Live;

        /// <summary>Per-state alert dedup. Keyed by state, holds the time it last fired.</summary>
        public readonly Dictionary<SignalState, DateTime> LastAlert = new Dictionary<SignalState, DateTime>();

        public bool Contains(decimal p) { return p >= Bottom && p <= Top; }

        /// <summary>Zone with the edge tolerance applied, which is how arming actually tests it.</summary>
        public bool ContainsWithBuffer(decimal p, decimal buffer)
        {
            return p >= Bottom - buffer && p <= Top + buffer;
        }

        public decimal HeightTicks(decimal tickSize)
        {
            return tickSize <= 0m ? 0m : (Top - Bottom) / tickSize;
        }

        /// <summary>
        /// A stable identity across rebuilds, so alert dedup and episode continuity survive the
        /// nightly zone rebuild. Two zones at the same POC of the same kind are the same zone.
        /// </summary>
        public string Key { get { return Kind + "@" + Poc.ToString(System.Globalization.CultureInfo.InvariantCulture); } }

        public void NoteCluster(decimal low, decimal high)
        {
            if (!HasCluster)
            {
                ClusterLow = low;
                ClusterHigh = high;
                HasCluster = true;
                return;
            }

            if (low < ClusterLow) ClusterLow = low;
            if (high > ClusterHigh) ClusterHigh = high;
        }
    }

    /// <summary>One qualifying absorption print, from either engine.</summary>
    public sealed class AbsorptionEvent
    {
        public DateTime Time;
        public decimal Price;

        /// <summary>Cumulative trade volume -- the Bookmap bubble, in contracts.</summary>
        public decimal Volume;

        public Aggressor Direction;
        public EventPath Path;

        /// <summary>How far price progressed in the aggressor direction after the print.</summary>
        public decimal DisplacementTicks;

        public int Bar;

        public bool FromLiveTape { get { return Path == EventPath.Tape; } }
    }

    /// <summary>
    /// One Triggered episode, from the absorption print to whatever resolved it. This is the
    /// CSV row, and the CSV is the calibration -- so every field the protocol reads offline is
    /// captured here at the moment it is knowable, not reconstructed afterwards.
    /// </summary>
    public sealed class Episode
    {
        public DateTime StartedLocal;
        public string ZoneKind;
        public int Rank;
        public TestSide Side;

        /// <summary>How far price travelled to get here, in ADRs. Arrival matters: a zone reached
        /// on the third leg of a trend is not the same trade as one reached on the first.</summary>
        public decimal ArrivalAtrMult;

        public decimal LargestTrade;
        public int StackedEvents;
        public EventPath Path;

        /// <summary>Delta of the bar that touched the zone.</summary>
        public decimal TouchDelta;

        public decimal ClosePosPct;
        public decimal DisplacementTicks;

        public bool Confirmed;
        public bool Expired;
        public bool Broken;

        /// <summary>Resolved one way or the other before the clock ran out.</summary>
        public bool ResolvedInClock;

        public int TriggerBar;
        public bool Written;
    }
}
