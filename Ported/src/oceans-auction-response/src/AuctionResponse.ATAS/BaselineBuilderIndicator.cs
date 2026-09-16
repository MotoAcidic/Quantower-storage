using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Drawing;
using System.Globalization;
using System.IO;
using ATAS.Indicators;
using AuctionResponse.Core;
using AuctionResponse.Replay;
using AuctionResponse.Research;
using AuctionResponse.Ui;
using OFT.Rendering.Context;

namespace AuctionResponse.Atas;

/// <summary>
/// Builds the frozen baseline artifact from loaded chart history.
///
/// Drop it on a MNQ chart with a 5-SECOND timeframe and enough history, tick "Write
/// artifact", and it writes the JSON the monitor needs. It reads aggressor-tagged volume
/// straight off ATAS footprint candles: Ask is buy-initiated volume, Bid is sell-initiated,
/// Betweens is unknown — exactly the B, S and U of Section 6.
///
/// It refuses to write anything it cannot honestly produce. A wrong timeframe, too few
/// sessions, or too few samples per bucket all stop the write and say why on the chart.
/// Nothing here invents a quantile.
/// </summary>
[DisplayName("Auction Response Baseline Builder")]
[Category("Ocean")]
[Display(Name = "Auction Response Baseline Builder",
    Description = "Offline tool: builds the frozen baseline artifact from loaded 5-second footprint history.")]
public sealed class BaselineBuilderIndicator : Indicator
{
    private readonly List<string> _report = new();
    private Palette _palette;
    private bool _pending;
    private bool _ranOnce;
    private string _status = "Set a 5-second chart, load history, then tick \"Write artifact\".";
    private Color _statusColour = Theme.Muted;

    public BaselineBuilderIndicator() : base(true)
    {
        DenyToChangePanel = true;
        EnableCustomDrawing = true;
        SubscribeToDrawingEvents(DrawingLayouts.Final);
        DrawAbovePrice = true;
    }

    // ---------------------------------------------------------------- settings

    [Display(GroupName = "Output", Name = "Artifact path", Order = 10,
        Description = "Where to write the baseline JSON. Point the monitor's \"Baseline artifact path\" at the same file.")]
    public string ArtifactPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS", "auction-response-baseline.json");

    [Display(GroupName = "Output", Name = "Write artifact", Order = 11,
        Description = "Tick to build and write. It clears itself once the run finishes, pass or fail.")]
    public bool WriteArtifact
    {
        get => _pending;
        set { _pending = value; if (value) RecalculateValues(); }
    }

    [Display(GroupName = "Instrument", Name = "Contract expiry", Order = 20,
        Description = "The dated contract this history belongs to, for example 202609. Stamped into the artifact as-is; never guess it.")]
    public string ContractExpiry { get; set; } = "";

    [Display(GroupName = "Session", Name = "Session timezone", Order = 30)]
    public string SessionTimezone { get; set; } = "America/Chicago";

    [Display(GroupName = "Session", Name = "Session start (local)", Order = 31)]
    public string SessionStartLocal { get; set; } = "08:30:00";

    [Display(GroupName = "Session", Name = "Session end, exclusive (local)", Order = 32)]
    public string SessionEndLocal { get; set; } = "15:00:00";

    [Display(GroupName = "Gates", Name = "Minimum sessions", Order = 40,
        Description = "Section 8 requires at least 10 prior sessions. Lowering this produces an artifact the monitor will still refuse.")]
    [Range(1, 200)]
    public int MinimumSessions { get; set; } = 10;

    [Display(GroupName = "Gates", Name = "Minimum samples per bucket", Order = 41,
        Description = "Section 8 requires at least 300 valid samples per 30-minute bucket and window.")]
    [Range(1, 100000)]
    public int MinimumSamplesPerBucket { get; set; } = 300;

    [Display(GroupName = "Gates", Name = "Write even if gates fail", Order = 42,
        Description = "Writes a short artifact anyway. The monitor will hold Warmup on the thin buckets rather than arm on them, so this is for inspection only.")]
    public bool WriteWhenGatesFail { get; set; }

    // ---------------------------------------------------------------- build

    protected override void OnCalculate(int bar, decimal value)
    {
        // Nothing per bar: the build walks the whole series once, on demand.
    }

    protected override void OnFinishRecalculate()
    {
        if (!_pending) return;
        _pending = false;

        try { Build(); }
        catch (Exception ex)
        {
            _status = "Build failed: " + ex.GetType().Name + " " + ex.Message;
            _statusColour = Theme.Coral;
        }

        _ranOnce = true;
        RedrawChart(new RedrawArg(Container.Region));
    }

    private void Build()
    {
        _report.Clear();

        var info = DataProvider?.InstrumentInfo;
        if (info is null || info.TickSize <= 0m)
        {
            Fail("Instrument metadata is not available yet. Let the chart finish loading.");
            return;
        }

        // A 5-second chart is the whole premise: one candle is one non-overlapping 5-second
        // window. On any other timeframe the samples would silently mean something else.
        var timeframeSeconds = Timeframe.ParseSeconds(ChartInfo?.TimeFrame);
        if (timeframeSeconds != 5)
        {
            Fail("This chart is \"" + (ChartInfo?.TimeFrame ?? "unknown") + "\". Set the chart to a 5-second timeframe: " +
                 "one candle must be exactly one 5-second window.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ContractExpiry))
        {
            Fail("Set \"Contract expiry\" to the contract this history belongs to, for example 202609.");
            return;
        }

        var config = new Config
        {
            Instrument = new InstrumentConfig { TickSize = info.TickSize },
            Timing = new TimingConfig { WindowsMs = new[] { 5000, 30000 } },
            Session = new SessionConfig
            {
                Timezone = SessionTimezone,
                WindowsTimezoneEquivalent = "Central Standard Time",
                StartLocal = SessionStartLocal,
                EndLocalExclusive = SessionEndLocal
            },
            Baseline = new BaselineConfig
            {
                MinimumSessions = MinimumSessions,
                MinimumSamplesPerBucketWindow = MinimumSamplesPerBucket
            }
        };

        var calendar = new SessionCalendar(config.Session);
        var grid = new TickGrid(info.TickSize);
        var samples = new List<BaselineSample>();

        var bars = CurrentBar;
        if (bars <= 1) { Fail("No bars are loaded."); return; }

        var sessions = new HashSet<string>(StringComparer.Ordinal);
        var skippedOutsideSession = 0;
        var skippedEmpty = 0;

        // A rolling buffer of the last six candles gives the 30-second window without
        // overlapping samples: one 30s sample per six 5s candles.
        var thirtySecondBuy = 0m;
        var thirtySecondSell = 0m;
        var thirtySecondCount = 0;
        decimal? thirtySecondOpenClose = null;

        decimal? previousClose = null;

        for (var i = 0; i < bars; i++)
        {
            var candle = GetCandle(i);
            if (candle is null) continue;

            var utc = DateTime.SpecifyKind(candle.Time, DateTimeKind.Utc);
            if (!calendar.IsEligible(utc, out _)) { skippedOutsideSession++; previousClose = null; continue; }

            var minutes = calendar.MinutesFromStart(utc);
            if (minutes is not { } minute) { skippedOutsideSession++; continue; }

            // ATAS footprint semantics: Ask is volume that lifted the offer (buy-initiated),
            // Bid is volume that hit the bid (sell-initiated), Betweens is unattributed.
            var buy = candle.Ask;
            var sell = candle.Bid;
            var unknown = candle.Betweens;

            if (buy + sell + unknown <= 0m) { skippedEmpty++; previousClose = null; continue; }

            sessions.Add(calendar.SessionId(utc));

            // Response proxy: close-to-close in ticks. NOT the Section 6 midpoint response,
            // which needs quotes; the artifact records that provenance so it cannot pass as
            // the real thing, and it is used only to scale a display axis.
            double absResponse = 0d;
            if (previousClose is { } prev && grid.IsOnGrid(candle.Close) && grid.IsOnGrid(prev))
                absResponse = Math.Abs((double)((candle.Close - prev) / info.TickSize));
            previousClose = candle.Close;

            samples.Add(new BaselineSample(
                calendar.SessionId(utc), minute, 5000,
                (double)buy, (double)sell,
                Math.Abs((double)(buy - sell)), absResponse));

            thirtySecondBuy += buy;
            thirtySecondSell += sell;
            thirtySecondOpenClose ??= candle.Close;
            thirtySecondCount++;

            if (thirtySecondCount == 6)
            {
                var response = thirtySecondOpenClose is { } open && grid.IsOnGrid(candle.Close)
                    ? Math.Abs((double)((candle.Close - open) / info.TickSize))
                    : 0d;

                samples.Add(new BaselineSample(
                    calendar.SessionId(utc), minute, 30000,
                    (double)thirtySecondBuy, (double)thirtySecondSell,
                    Math.Abs((double)(thirtySecondBuy - thirtySecondSell)), response));

                thirtySecondBuy = thirtySecondSell = 0m;
                thirtySecondCount = 0;
                thirtySecondOpenClose = null;
            }
        }

        if (samples.Count == 0)
        {
            Fail("No candles fell inside " + SessionStartLocal + "-" + SessionEndLocal + " " + SessionTimezone +
                 ". Check the session settings and that the loaded history covers RTH.");
            return;
        }

        var instrument = new InstrumentKey(info.Instrument ?? "", info.Exchange ?? "", ContractExpiry.Trim());
        var feedMode = DataProvider?.Name ?? "Live";

        var builder = new BaselineBuilder(config);
        var result = builder.Build(samples, instrument, info.TickSize, feedMode,
            calendarVersion: SessionTimezone + " " + SessionStartLocal + "-" + SessionEndLocal,
            fitEndUtc: DateTime.UtcNow,
            sourceLogHashes: new[] { "atas-footprint-5s:" + bars + "bars" });

        var artifact = result.Artifact with
        {
            ResponseScaleSource = "candle-close-5s (not midpoint; display scale only)"
        };

        _report.Add("bars scanned: " + bars.ToString("N0", CultureInfo.InvariantCulture));
        _report.Add("sessions: " + result.SessionsUsed + " (minimum " + MinimumSessions + ")");
        _report.Add("samples: " + result.SamplesUsed.ToString("N0", CultureInfo.InvariantCulture));
        _report.Add("buckets: " + artifact.Buckets.Count);
        _report.Add("outside session: " + skippedOutsideSession.ToString("N0", CultureInfo.InvariantCulture) + " bars");
        if (skippedEmpty > 0) _report.Add("empty candles: " + skippedEmpty.ToString("N0", CultureInfo.InvariantCulture));

        var thinBuckets = artifact.Buckets.Count(b => b.SampleCount < MinimumSamplesPerBucket);
        if (thinBuckets > 0) _report.Add("thin buckets: " + thinBuckets + " below " + MinimumSamplesPerBucket + " samples");

        foreach (var warning in result.Warnings.Take(3)) _report.Add("! " + warning);

        if (!result.MeetsGates && !WriteWhenGatesFail)
        {
            Fail("Gates not met - nothing written. Load more history, or tick \"Write even if gates fail\" to inspect.");
            return;
        }

        var directory = Path.GetDirectoryName(ArtifactPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        BaselineArtifactIo.Save(artifact, ArtifactPath);

        _status = result.MeetsGates
            ? "Artifact written. Point the monitor at it."
            : "Artifact written, but the gates were NOT met: the monitor will hold Warmup on thin buckets.";
        _statusColour = result.MeetsGates ? Theme.Teal : Theme.Amber;
        _report.Add("written: " + ArtifactPath);
    }

    private void Fail(string message)
    {
        _status = message;
        _statusColour = Theme.Coral;
    }

    // ---------------------------------------------------------------- render

    protected override void OnRender(RenderContext context, DrawingLayouts layout)
    {
        var region = Container?.Region ?? Rectangle.Empty;
        if (region.Width <= 0 || region.Height <= 0) return;

        _palette ??= new Palette((float)((ChartInfo?.DpiX ?? 96m) / 96m));

        var lineHeight = context.MeasureString("Hg", _palette.Small).Height + 3;
        var x = region.Left + 12;
        var y = region.Top + 12;

        context.DrawString("AUCTION RESPONSE - BASELINE BUILDER", _palette.Heading, Theme.Text, x, y);
        y += context.MeasureString("Hg", _palette.Heading).Height + 6;

        context.DrawString(ChartInfo?.TimeFrame ?? "timeframe unknown", _palette.Small, Theme.Muted, x, y);
        y += lineHeight;

        foreach (var line in Wrap(context, _status, region.Width - 24))
        {
            context.DrawString(line, _palette.Strong, _statusColour, x, y);
            y += lineHeight;
        }

        if (!_ranOnce) return;

        y += 6;
        foreach (var line in _report)
        {
            context.DrawString(line, _palette.Small, Theme.Muted, x, y);
            y += lineHeight;
        }
    }

    private IEnumerable<string> Wrap(RenderContext context, string text, int maxWidth)
    {
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (context.MeasureString(candidate, _palette.Strong).Width <= maxWidth) { line = candidate; continue; }
            if (line.Length > 0) yield return line;
            line = word;
        }
        if (line.Length > 0) yield return line;
    }

    protected override void OnDispose()
    {
        _palette?.Dispose();
        _palette = null;
        base.OnDispose();
    }
}
