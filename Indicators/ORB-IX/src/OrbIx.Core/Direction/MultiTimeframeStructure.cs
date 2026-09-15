using System;
using System.Collections.Generic;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Sessions;
using OrbIx.Core.Structure;

namespace OrbIx.Core.Direction;

/// <summary>
/// Structure trend on several timeframes at once, from one tick stream.
/// </summary>
/// <remarks>
/// THE LOOKAHEAD IS DESIGNED OUT, NOT TESTED AWAY.
///     Every higher timeframe here is built by feeding the same prints into its
///     own <see cref="BarAggregator"/> and consuming ONLY what <c>Add</c> hands
///     back as a CLOSED bar. There is no resample and no as-of join, so the
///     still-forming-bar defect cannot occur: a 60-minute bar simply does not
///     exist until its final print has landed.
///
///     This matters because the same mistake, made with a resample and an as-of
///     lookup, silently returned a bar up to 14 minutes ahead of itself and
///     FLIPPED THE SIGN of six separate trials before it was found. The fix is
///     not a test; it is making the wrong thing unrepresentable.
///
/// THERE IS NO PER-SESSION RESET, AND THAT IS THE POINT.
///     A 60-minute structure trend that forgot itself at every session boundary
///     would be permanently undecided, because it would never hold enough closed
///     bars to form a pivot. Structure is a multi-session reading by nature.
///     <see cref="Reset"/> exists for a symbol change, where the old instrument's
///     pivots are genuinely meaningless.
/// </remarks>
public sealed class MultiTimeframeStructure
{
    private readonly List<Lane> lanes = new();
    private readonly int leftBars;
    private readonly int rightBars;

    /// <summary>
    /// Creates one lane per period.
    /// </summary>
    /// <param name="periods">Label and bar period for each timeframe, e.g. ("5m", 5 minutes).</param>
    /// <param name="leftBars">Pivot lookback, matching the chart's HH/LL setting.</param>
    /// <param name="rightBars">Pivot lookforward, matching the chart's HH/LL setting.</param>
    /// <exception cref="ArgumentNullException">If <paramref name="periods"/> is null.</exception>
    /// <exception cref="ArgumentException">If a period is not positive, or a label repeats.</exception>
    /// <exception cref="ArgumentOutOfRangeException">If either pivot bound is not positive.</exception>
    public MultiTimeframeStructure(
        IReadOnlyList<(string Name, TimeSpan Period)> periods, int leftBars, int rightBars)
    {
        ArgumentNullException.ThrowIfNull(periods);

        if (leftBars <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(leftBars), leftBars, "A pivot needs at least one bar to its left.");

        if (rightBars <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(rightBars), rightBars, "A pivot needs at least one bar to its right.");

        this.leftBars = leftBars;
        this.rightBars = rightBars;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string name, TimeSpan period) in periods)
        {
            if (period <= TimeSpan.Zero)
                throw new ArgumentException(
                    $"timeframe '{name}' has a non-positive period of {period}.", nameof(periods));

            // Two lanes with one label would render as a single row on the panel
            // and silently drop the other's vote from view while it still counted
            // toward the verdict.
            if (!seen.Add(name))
                throw new ArgumentException(
                    $"timeframe label '{name}' appears more than once.", nameof(periods));

            this.lanes.Add(new Lane(name, period, leftBars, rightBars));
        }
    }

    /// <summary>How many lanes are being tracked.</summary>
    public int Count => this.lanes.Count;

    /// <summary>
    /// Feeds a print to every lane. Only lanes whose bar CLOSES on this print
    /// advance their structure engine.
    /// </summary>
    public void OnTick(in TickEvent tick)
    {
        foreach (Lane lane in this.lanes)
        {
            if (lane.Aggregator.Add(tick, out BarEvent closed))
            {
                lane.Engine.Feed(closed.High, closed.Low, closed.Close);
                lane.ClosedBars++;
            }
        }
    }

    /// <summary>
    /// The current reading on every lane, in the order the lanes were configured.
    /// </summary>
    public IReadOnlyList<DirectionVote> Votes()
    {
        var votes = new List<DirectionVote>(this.lanes.Count);

        foreach (Lane lane in this.lanes)
        {
            // No closed bar means the structure engine has been fed nothing. That
            // is UNAVAILABLE -- it has no opinion because it has seen nothing --
            // and it is not the same as having looked and found no direction.
            if (lane.ClosedBars == 0)
            {
                votes.Add(DirectionVote.NotAvailable(lane.Name, "no closed bar yet"));
                continue;
            }

            IReadOnlyList<double> trends = lane.Engine.TrendSeries;

            if (trends.Count == 0)
            {
                votes.Add(DirectionVote.NotAvailable(lane.Name, "no trend series"));
                continue;
            }

            double trend = trends[^1];
            string detail = $"{lane.ClosedBars} bars";

            DirectionState state = trend switch
            {
                > 0 => DirectionState.Up,
                < 0 => DirectionState.Down,

                // Zero is the engine's own "not yet decided": it holds this value
                // until price has broken a pivot in one direction or the other.
                // Reported as Undecided, and it does not vote.
                _ => DirectionState.Undecided,
            };

            votes.Add(new DirectionVote(lane.Name, state, detail));
        }

        return votes;
    }

    /// <summary>
    /// Drops every lane's bars and pivots. For a symbol change, not a session roll.
    /// </summary>
    public void Reset()
    {
        foreach (Lane lane in this.lanes)
            lane.Restart(this.leftBars, this.rightBars);
    }

    private sealed class Lane
    {
        public Lane(string name, TimeSpan period, int leftBars, int rightBars)
        {
            this.Name = name;
            this.Period = period;
            this.Aggregator = new BarAggregator(period);
            this.Engine = new HhLlEngine(leftBars, rightBars);
        }

        public string Name { get; }

        public TimeSpan Period { get; }

        public BarAggregator Aggregator { get; private set; }

        public HhLlEngine Engine { get; private set; }

        public int ClosedBars { get; set; }

        public void Restart(int leftBars, int rightBars)
        {
            this.Aggregator = new BarAggregator(this.Period);
            this.Engine = new HhLlEngine(leftBars, rightBars);
            this.ClosedBars = 0;
        }
    }
}
