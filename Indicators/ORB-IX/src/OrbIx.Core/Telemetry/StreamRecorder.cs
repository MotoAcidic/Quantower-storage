using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OrbIx.Core.Abstractions;
using OrbIx.Core.Config;

namespace OrbIx.Core.Telemetry;

/// <summary>
/// Records the raw tick and book streams so they can be replayed.
///
/// This exists because §13's validation replays recorded streams through the same engine
/// that ran live, and a book cannot be captured retroactively: whatever was not written down
/// at the time is gone. Recording therefore starts on day one of the build, before any
/// signal is trusted, rather than being switched on once there is something worth measuring.
///
/// The format is one JSON object per line — greppable, streamable, and readable by a
/// half-line of Python without a schema. Compactness matters at this volume: the field names
/// are short because a busy session writes millions of lines and a descriptive name costs
/// more than it explains when the meaning is documented once, here.
///
///   t  timestamp, ISO-8601 UTC     p  price          s  size
///   a  aggressor: B, S or U        b  bid            k  ask
///   d  side: B or A                l  level index    n  order count
///   ts the platform's own timestamp, present ONLY when it was rejected as implausible
///      and "t" therefore carries the receive instant instead
///
/// Writes files and opens no port. Recordings are pulled with the existing SSH trust rather
/// than served (NIST CSF PR.IR-01).
/// </summary>
public sealed class StreamRecorder : IDisposable
{
    private readonly object gate = new();
    private readonly RecorderConfig config;
    private readonly TextWriter? trades;
    private readonly TextWriter? book;
    private readonly bool ownsWriters;

    private long tradesWritten;
    private long bookWritten;
    private DateTime lastFlushUtc = DateTime.MinValue;

    public StreamRecorder(
        RecorderConfig config, TextWriter? trades, TextWriter? book, bool ownsWriters = false)
    {
        this.config = config ?? throw new ArgumentNullException(nameof(config));
        this.trades = trades;
        this.book = book;
        this.ownsWriters = ownsWriters;

        if (config.Enabled && trades is null && book is null)
        {
            throw new ArgumentException(
                "The recorder is enabled but has nowhere to write. Supply at least one stream, "
                + "or disable it — a recorder that silently records nothing is worse than none.",
                nameof(config));
        }
    }

    /// <summary>
    /// Opens recordings under a directory, one file per stream per session date.
    ///
    /// Appending rather than replacing, so a restart mid-session adds to the recording
    /// instead of destroying what was captured before it.
    /// </summary>
    public static StreamRecorder OpenFiles(
        RecorderConfig config, string directory, string symbolId, DateOnly sessionDate)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A directory is required.", nameof(directory));
        if (string.IsNullOrWhiteSpace(symbolId))
            throw new ArgumentException("A symbol is required.", nameof(symbolId));

        Directory.CreateDirectory(directory);

        var stamp = sessionDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var safeSymbol = MakeFilenameSafe(symbolId);

        TextWriter? Open(string kind) => new StreamWriter(
            new FileStream(
                Path.Combine(directory, $"{safeSymbol}-{stamp}-{kind}.ndjson"),
                FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false));

        return new StreamRecorder(
            config,
            config.Enabled && config.RecordTrades ? Open("trades") : null,
            config.Enabled && config.RecordBook ? Open("book") : null,
            ownsWriters: true);
    }

    /// <summary>
    /// Opens recordings, stepping aside to a numbered name where one is already held, and
    /// returning null rather than throwing when none can be opened.
    ///
    /// THE SAME DEFECT AS THE JOURNAL'S, and it would have been the next one to bite. The
    /// filename stamp is <c>yyyyMMdd</c> — a DATE — so two charts on the same symbol on the
    /// same day ask for the same file, and <see cref="FileShare.Read"/> refuses the second.
    /// The recorder is enabled by default and is opened immediately after the journal, so
    /// fixing only the journal would have moved the startup failure by one line.
    ///
    /// The share mode is deliberately NOT widened: two writers appending to one recording
    /// interleave partial lines and corrupt it.
    /// </summary>
    /// <param name="config">What to record.</param>
    /// <param name="directory">Where the recordings go.</param>
    /// <param name="symbolId">The instrument, used in the file name.</param>
    /// <param name="sessionDate">The session date, used in the file name.</param>
    /// <param name="reason">Why nothing could be opened, when nothing could.</param>
    public static StreamRecorder? TryOpenFiles(
        RecorderConfig config, string directory, string symbolId, DateOnly sessionDate,
        out string reason)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("A directory is required.", nameof(directory));
        if (string.IsNullOrWhiteSpace(symbolId))
            throw new ArgumentException("A symbol is required.", nameof(symbolId));

        reason = string.Empty;

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException)
        {
            reason = $"{directory} could not be created ({ex.GetType().Name}: {ex.Message})";
            return null;
        }

        var stamp = sessionDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var safeSymbol = MakeFilenameSafe(symbolId);

        // Both streams take the SAME suffix, so a session's trades and book stay a matched
        // pair. Numbering them independently would leave a reader unable to tell which book
        // file belongs to which trades file.
        var suffix = FreeSuffix(directory, safeSymbol, stamp, config);

        if (suffix < 0)
        {
            reason = $"{safeSymbol}-{stamp}-*.ndjson and its numbered alternatives are all held "
                     + "by another writer";
            return null;
        }

        TextWriter Open(string kind) => new StreamWriter(
            new FileStream(
                Path.Combine(directory, NameFor(safeSymbol, stamp, kind, suffix)),
                FileMode.Append, FileAccess.Write, FileShare.Read),
            new UTF8Encoding(false));

        try
        {
            return new StreamRecorder(
                config,
                config.Enabled && config.RecordTrades ? Open("trades") : null,
                config.Enabled && config.RecordBook ? Open("book") : null,
                ownsWriters: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"{safeSymbol}-{stamp} could not be recorded ({ex.GetType().Name}: {ex.Message})";
            return null;
        }
    }

    /// <summary>
    /// The lowest suffix at which every stream this configuration needs is free, or -1.
    ///
    /// Probed by opening and closing, because the only reliable way to know a file can be
    /// written is to write to it — a existence check would race anything else starting at the
    /// same moment, and File.Exists says nothing about whether a handle is held.
    /// </summary>
    private static int FreeSuffix(string directory, string symbol, string stamp, RecorderConfig config)
        => ChooseSuffix(KindsFor(config), name =>
        {
            try
            {
                using var probe = new FileStream(
                    Path.Combine(directory, name),
                    FileMode.Append, FileAccess.Write, FileShare.Read);

                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }, symbol, stamp);

    /// <summary>Which streams this configuration will write.</summary>
    public static IReadOnlyList<string> KindsFor(RecorderConfig config)
    {
        if (config is null)
            throw new ArgumentNullException(nameof(config));

        var kinds = new List<string>(2);

        if (config.Enabled && config.RecordTrades) kinds.Add("trades");
        if (config.Enabled && config.RecordBook) kinds.Add("book");

        return kinds;
    }

    /// <summary>
    /// The lowest suffix at which EVERY stream is free, or -1 when none is.
    ///
    /// The opener is injected for the same reason the journal's is: the collision being
    /// survived is a Windows file lock, and .NET on Unix does not enforce
    /// <see cref="FileShare"/> — verified by execution — so a suite running on Linux cannot
    /// reproduce it. Separating the decision from the platform behaviour makes the rule
    /// checkable anywhere.
    ///
    /// BOTH STREAMS TAKE THE SAME SUFFIX, which is why this asks about all of them together
    /// rather than numbering each. Numbering them independently would leave a reader unable to
    /// tell which book file belongs to which trades file.
    /// </summary>
    public static int ChooseSuffix(
        IReadOnlyList<string> kinds, Func<string, bool> canOpen, string symbol, string stamp)
    {
        if (kinds is null) throw new ArgumentNullException(nameof(kinds));
        if (canOpen is null) throw new ArgumentNullException(nameof(canOpen));

        // Nothing to write means nothing to collide over, and the caller still needs a suffix
        // to name files it will not open.
        if (kinds.Count == 0)
            return 1;

        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            var free = true;

            foreach (var kind in kinds)
            {
                if (canOpen(NameFor(symbol, stamp, kind, attempt)))
                    continue;

                free = false;
                break;
            }

            if (free)
                return attempt;
        }

        return -1;
    }

    /// <summary>The file name for one stream at one suffix.</summary>
    public static string NameFor(string symbol, string stamp, string kind, int attempt)
        => attempt == 1
            ? $"{symbol}-{stamp}-{kind}.ndjson"
            : $"{symbol}-{stamp}-{kind}-{attempt}.ndjson";

    /// <summary>How many suffixes to try before reporting that none are free.</summary>
    private const int MaxNameAttempts = 8;

    public long TradesWritten
    {
        get { lock (this.gate) { return this.tradesWritten; } }
    }

    public long BookEventsWritten
    {
        get { lock (this.gate) { return this.bookWritten; } }
    }

    /// <summary>
    /// Records a print.
    ///
    /// Called from the market-data path, so it formats and writes and does nothing else. The
    /// flush is time-bounded rather than per-line: flushing every print on an instrument
    /// that prints thousands of times a second would make the recorder the bottleneck it
    /// exists to observe.
    /// </summary>
    public void OnTick(in TickEvent tick, DateTime nowUtc)
    {
        if (this.trades is null)
            return;

        var line = string.Format(
            CultureInfo.InvariantCulture,
            "{{\"t\":\"{0:O}\",\"p\":{1},\"s\":{2},\"a\":\"{3}\",\"b\":{4},\"k\":{5}}}",
            tick.TimestampUtc,
            Number(tick.Price),
            Number(tick.Size),
            AggressorCode(tick.Aggressor),
            Number(tick.Bid),
            Number(tick.Ask));

        lock (this.gate)
        {
            this.trades.WriteLine(line);
            this.tradesWritten++;
            this.MaybeFlush(nowUtc);
        }
    }

    /// <summary>Records a book change.</summary>
    public void OnBook(in BookDelta delta, DateTime nowUtc)
    {
        if (this.book is null)
            return;

        // BOTH CLOCKS ARE WRITTEN when they differ. "t" is the usable instant every consumer
        // reads; "ts" is what the platform actually said, and it appears only when the two are
        // not the same thing — which is when the platform's value was rejected as implausible.
        //
        // Keeping the original matters because the platform's behaviour here is unexplained:
        // about 45% of live Level 2 updates arrive stamped 1970-01-01. Replacing the evidence
        // in the recording would make that unexplainable from the recording.
        // THE PLATFORM'S OWN MARKING IS RECORDED, not this engine's reading of it. "c" is
        // Level2Quote.Closed exactly as it arrived, and "r" says the boundary admitted the
        // event as a withdrawal rather than as a level. Both are written only when true, so
        // the ordinary line keeps its existing shape and an old recording stays readable.
        //
        // Recording the flag is what makes the next question answerable at all: whether a
        // priceless removal marker clears one level or the whole book is NOT established, and
        // nothing on this machine could have been asked before, because nothing kept it.
        var flags = string.Concat(
            delta.Closed ? ",\"c\":true" : string.Empty,
            delta.IsReset ? ",\"r\":true" : string.Empty);

        var line = delta.SourceTimeUsable
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{{\"t\":\"{0:O}\",\"d\":\"{1}\",\"p\":{2},\"s\":{3},\"l\":{4},\"n\":{5}{6}}}",
                delta.TimestampUtc,
                delta.Side == BookSide.Bid ? "B" : "A",
                Number(delta.Price),
                Number(delta.Size),
                delta.LevelIndex.ToString(CultureInfo.InvariantCulture),
                delta.OrderCount.ToString(CultureInfo.InvariantCulture),
                flags)
            : string.Format(
                CultureInfo.InvariantCulture,
                "{{\"t\":\"{0:O}\",\"d\":\"{1}\",\"p\":{2},\"s\":{3},\"l\":{4},\"n\":{5}{6},\"ts\":\"{7:O}\"}}",
                delta.TimestampUtc,
                delta.Side == BookSide.Bid ? "B" : "A",
                Number(delta.Price),
                Number(delta.Size),
                delta.LevelIndex.ToString(CultureInfo.InvariantCulture),
                delta.OrderCount.ToString(CultureInfo.InvariantCulture),
                flags,
                delta.SourceTimestampUtc);

        lock (this.gate)
        {
            this.book.WriteLine(line);
            this.bookWritten++;
            this.MaybeFlush(nowUtc);
        }
    }

    /// <summary>
    /// Flushes both streams if the configured interval has elapsed.
    ///
    /// A recording that is buffered when the process dies is a recording that did not
    /// happen for the last few seconds, which is exactly the window a crash is most
    /// interesting in. The interval bounds that loss rather than eliminating it, and the
    /// trade-off is stated here rather than left implicit.
    /// </summary>
    private void MaybeFlush(DateTime nowUtc)
    {
        if (this.lastFlushUtc == DateTime.MinValue)
        {
            this.lastFlushUtc = nowUtc;
            return;
        }

        if (nowUtc - this.lastFlushUtc < TimeSpan.FromMilliseconds(this.config.FlushIntervalMs))
            return;

        this.Flush();
        this.lastFlushUtc = nowUtc;
    }

    /// <summary>Forces both streams to disk.</summary>
    public void Flush()
    {
        this.trades?.Flush();
        this.book?.Flush();
    }

    /// <summary>
    /// Reads a recorded trade stream back. The inverse of <see cref="OnTick"/>, kept beside
    /// it so the two cannot drift: a reader written elsewhere would silently stop matching
    /// the writer the first time a field changed.
    /// </summary>
    public static IEnumerable<TickEvent> ReadTrades(TextReader reader)
    {
        if (reader is null)
            throw new ArgumentNullException(nameof(reader));

        string? line;
        var lineNumber = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var document = System.Text.Json.JsonDocument.Parse(line);
            var root = document.RootElement;

            yield return new TickEvent(
                ReadTimestamp(root, lineNumber),
                ReadNumber(root, "p", lineNumber),
                ReadNumber(root, "s", lineNumber),
                (root.GetProperty("a").GetString() ?? "U") switch
                {
                    "B" => Aggressor.Buy,
                    "S" => Aggressor.Sell,
                    _ => Aggressor.Unknown,
                },
                ReadNumber(root, "b", lineNumber),
                ReadNumber(root, "k", lineNumber));
        }
    }

    /// <summary>
    /// Reads the timestamp, naming the problem when the line did not come from this
    /// recorder. Letting the dictionary lookup throw would surface a bare "key not present"
    /// with no indication of which file or which line, which is the kind of error that
    /// costs an hour.
    /// </summary>
    private static DateTime ReadTimestamp(System.Text.Json.JsonElement root, int lineNumber)
    {
        if (!root.TryGetProperty("t", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String)
        {
            throw new FormatException(
                $"Recording line {lineNumber} has no 't' timestamp field; the file was not written by this recorder.");
        }

        return value.GetDateTime().ToUniversalTime();
    }

    private static double ReadNumber(System.Text.Json.JsonElement root, string name, int lineNumber)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            throw new FormatException(
                $"Recording line {lineNumber} has no '{name}' field; the file was not written by this recorder.");
        }

        // An unmeasured value is recorded as null and read back as NaN, so "no quote was
        // known" survives the round trip instead of becoming a zero that looks like a price.
        return value.ValueKind == System.Text.Json.JsonValueKind.Null ? double.NaN : value.GetDouble();
    }

    private static string Number(double value)
        => double.IsNaN(value) || double.IsInfinity(value)
            ? "null"
            : value.ToString("R", CultureInfo.InvariantCulture);

    private static string AggressorCode(Aggressor aggressor) => aggressor switch
    {
        Aggressor.Buy => "B",
        Aggressor.Sell => "S",
        _ => "U",
    };

    private static string MakeFilenameSafe(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var buffer = new char[value.Length];

        for (var i = 0; i < value.Length; i++)
            buffer[i] = Array.IndexOf(invalid, value[i]) >= 0 ? '_' : value[i];

        return new string(buffer);
    }

    public void Dispose()
    {
        lock (this.gate)
        {
            this.Flush();

            if (!this.ownsWriters)
                return;

            this.trades?.Dispose();
            this.book?.Dispose();
        }
    }
}
