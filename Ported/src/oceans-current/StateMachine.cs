using System;

namespace OceansCurrent
{
    /// <summary>
    /// Hysteresis plus a minimum dwell, and nothing else.
    ///
    /// This is the part of Ocean's Current that earns its place. Any weighted blend can produce
    /// a number; what makes the number worth acting on is that it costs three closed bars to
    /// change the answer, that leaving a state is easier than entering one, and that long has to
    /// pass through neutral to become short. The behavioural target is roughly three committed
    /// flips a day. More than that and this class is set too loose.
    ///
    /// It is separate from the engine so it can be driven straight from the harness, where the
    /// score is an input rather than something a rigged price series has to produce.
    /// </summary>
    public sealed class StateMachine
    {
        private readonly StateConfig _cfg;

        public StateMachine(StateConfig cfg) { _cfg = cfg; }

        public StateMachine CloneForWhatIf() => (StateMachine)MemberwiseClone();

        public BiasState State { get; private set; } = BiasState.Neutral;

        /// <summary>Committed bars this state has been held, the current one included.</summary>
        public int BarsInState { get; private set; }

        /// <summary>
        /// One committed bar. A bar nothing could score still serves its dwell but cannot move
        /// the state: an absent score is not evidence for neutral, it is no evidence at all.
        /// </summary>
        public BiasState Advance(bool scoreKnown, decimal score)
        {
            BarsInState++;

            var next = Next(State, BarsInState, scoreKnown, score, _cfg);
            if (next == State) return State;

            State = next;
            BarsInState = 1;

            return State;
        }

        /// <summary>
        /// What the NEXT committed bar would do with this score, changing nothing. The trigger
        /// scan asks this a few thousand times a bar; it is the same rule Advance applies, so a
        /// level the panel prints is a level the machine will actually act on.
        /// </summary>
        public BiasState Peek(bool scoreKnown, decimal score) =>
            Next(State, BarsInState + 1, scoreKnown, score, _cfg);

        /// <summary>
        /// The transition the next bar would make if the dwell were already served. The trigger
        /// levels are computed with this, and the dwell is reported beside them as "earliest in N
        /// bars" -- otherwise a freshly entered state would show no levels at all, which reads
        /// as "nothing can change it" when the truth is "nothing can change it YET".
        /// </summary>
        public BiasState PeekIgnoringDwell(bool scoreKnown, decimal score) =>
            Next(State, int.MaxValue / 2, scoreKnown, score, _cfg);

        /// <summary>Committed bars still to serve before any change is possible. 0 = the next bar can.</summary>
        public int DwellLeft => Math.Max(0, Math.Max(1, _cfg.MinDwellBars) - (BarsInState + 1));

        /// <summary>
        /// The one transition rule. A bar nothing could score still serves its dwell but cannot
        /// move the state: an absent score is not evidence for neutral, it is no evidence at all.
        /// </summary>
        private static BiasState Next(BiasState state, int barsInState, bool scoreKnown,
                                      decimal score, StateConfig cfg)
        {
            if (!scoreKnown) return state;
            if (barsInState < Math.Max(1, cfg.MinDwellBars)) return state;

            switch (state)
            {
                case BiasState.Neutral:
                    if (score >= cfg.EnterThreshold) return BiasState.Long;
                    if (score <= -cfg.EnterThreshold) return BiasState.Short;
                    return state;

                case BiasState.Long:
                    // Only neutral is reachable from long. Reversing outright would let one
                    // violent bar carry the state across the whole range in a single commit.
                    return score <= cfg.ExitThreshold ? BiasState.Neutral : state;

                case BiasState.Short:
                    return score >= -cfg.ExitThreshold ? BiasState.Neutral : state;
            }

            return state;
        }
    }
}
