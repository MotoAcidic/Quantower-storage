using AuctionResponse.Core;

namespace AuctionResponse.Replay;

public sealed record ReplayOutput(
    IReadOnlyList<TransitionRecord> Transitions,
    IReadOnlyList<ViewSnapshot> Views,
    int EventsApplied,
    int TicksApplied);

/// <summary>
/// Feeds a recorded log through the SAME core, in sequence order.
///
/// Playback speed changes wall time, never feature time: the engine only ever sees the
/// recorded elapsed stamps. That is what makes "replay reproduces the outputs" a real
/// claim rather than a coincidence of timing.
/// </summary>
public sealed class ReplayRunner
{
    private readonly Engine _engine;

    public ReplayRunner(Engine engine) { _engine = engine; }

    /// <summary>
    /// Runs the log. <paramref name="renderEvery"/> only affects how many views are
    /// captured; it must not change a single transition.
    /// </summary>
    public ReplayOutput Run(IEnumerable<object> records, int renderEvery = 1)
    {
        var transitions = new List<TransitionRecord>();
        var views = new List<ViewSnapshot>();
        var events = 0;
        var ticks = 0;

        foreach (var record in records)
        {
            switch (record)
            {
                case MarketEvent e:
                    _engine.Accept(e);
                    events++;
                    break;

                case DecisionTick t:
                    var result = _engine.Decide(t);
                    transitions.AddRange(result.Transitions);
                    ticks++;
                    if (renderEvery > 0 && ticks % renderEvery == 0) views.Add(result.View);
                    break;
            }
        }

        return new ReplayOutput(transitions, views, events, ticks);
    }

    /// <summary>
    /// Semantic identity of a transition stream, excluding non-semantic runtime telemetry.
    /// Two runs of the same log must produce byte-identical output under this projection.
    /// </summary>
    public static string SemanticDigest(IEnumerable<TransitionRecord> transitions)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in transitions)
            sb.Append(t.TransitionId).Append('|')
              .Append(t.CandidateId).Append('|')
              .Append(t.LevelId).Append('|')
              .Append(t.DetectionElapsedNs).Append('|')
              .Append(t.SourceSequence).Append('|')
              .Append(t.OldState).Append("->").Append(t.NewState).Append('|')
              .Append(t.Reason).Append('|')
              .Append(t.ConfirmationHalfTicks).Append('|')
              .Append(t.FailureHalfTicks).Append('|')
              .Append(t.ConfigurationHash).Append('\n');
        return sb.ToString();
    }
}
