using System.ComponentModel;
using ATAS.Indicators;

namespace OceansEffort
{
    /// <summary>
    /// Ocean Effort tuned for crypto: the same model, different arithmetic underneath it.
    ///
    /// Nothing about the read changes -- value migrates, effort is paid, size gets absorbed, and
    /// delta refuses to follow a new extreme, on any instrument. What changes is every threshold
    /// that was ever expressed in contracts or ticks, because a futures contract and a coin are
    /// not the same unit and a number tuned for one is quietly wrong on the other.
    ///
    /// So this version leaves the calibration to measure itself, and widens the two windows that
    /// assume a session: crypto runs continuously, and a lookback sized for a six-hour equity
    /// session covers a different amount of market at three in the morning.
    /// </summary>
    [DisplayName("Oceans Effort Crypto")]
    [Category("Ocean")]
    public class OceansEffortCryptoIndicator : OceansEffortIndicator
    {
        public OceansEffortCryptoIndicator()
        {
            // Zero on both means measured from this instrument's own bars. On MNQ a fixed 150
            // contracts is a reasonable delta gap; on Bitcoin it is every bar, and on a small
            // altcoin it is none of them.
            DivergenceMinGap = 0;
            MaxRiskTicks = 0;

            // Ranking handles what a floor used to: with fractional coin volume there is no
            // contract count that means "big" across instruments.
            TopSizeFloor = 0;

            // No session to anchor to, so the profile and the regime both look at more of it.
            LevelLookback = 300;
            RegimeBars = 60;

            // Crypto ranges further inside a bar than index futures do, so the same absorption
            // rule needs a little more room before it calls a bar result-less.
            AbsorbMaxResult = 4;
        }
    }
}
