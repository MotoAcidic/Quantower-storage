using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AuctionResponse.Core;

namespace AuctionResponse.Replay;

/// <summary>A destination the recorder is allowed to write to.</summary>
public sealed record RecordingPermission(string Directory, bool Confirmed)
{
    /// <summary>
    /// The runtime refuses an unapproved destination. Recording market data is the owner's
    /// decision and their market-data rights, not a default.
    /// </summary>
    public string? RefusalReason()
    {
        if (string.IsNullOrWhiteSpace(Directory)) return "no recording directory configured";
        if (!Confirmed) return "recording destination has not been approved by the owner";
        return null;
    }
}

/// <summary>
/// Append-only JSONL recorder running on its own ordered writer, so a slow disk cannot
/// block a host callback (Section 16, Section 17).
///
/// A recorder fault is not swallowed: it is reported back to the engine, which puts the
/// research profile in Faulted because the results stop being auditable.
/// </summary>
public sealed class JsonlRecorder : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _prefix;
    private StreamWriter? _events;
    private StreamWriter? _transitions;
    private long _firstSequence = -1;
    private long _lastSequence = -1;
    private int _eventLines;
    private int _transitionLines;

    public JsonlRecorder(RecordingPermission permission, InstrumentKey instrument, string sessionId)
    {
        var refusal = permission.RefusalReason();
        if (refusal is not null) throw new InvalidOperationException("Recorder refused: " + refusal);

        _directory = permission.Directory;
        Directory_CreateIfMissing(_directory);
        var safeSymbol = string.Concat(instrument.Symbol.Where(char.IsLetterOrDigit));
        _prefix = safeSymbol + "_" + instrument.Expiry + "_" + sessionId;

        _events = new StreamWriter(Path.Combine(_directory, _prefix + ".events.jsonl"), append: true, Encoding.UTF8);
        _transitions = new StreamWriter(Path.Combine(_directory, _prefix + ".transitions.jsonl"), append: true, Encoding.UTF8);
    }

    public bool Faulted { get; private set; }
    public string? FaultReason { get; private set; }
    public int EventLines => _eventLines;
    public int TransitionLines => _transitionLines;

    private static void Directory_CreateIfMissing(string dir)
    {
        if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
    }

    public void Append(MarketEvent e)
    {
        if (Faulted) return;
        try
        {
            lock (_gate)
            {
                if (_firstSequence < 0) _firstSequence = e.EventSequence;
                _lastSequence = e.EventSequence;
                _events!.WriteLine(Jsonl.Serialize(e));
                _eventLines++;
            }
        }
        catch (Exception ex) { Fault(ex); }
    }

    public void Append(DecisionTick t)
    {
        if (Faulted) return;
        try
        {
            lock (_gate)
            {
                _lastSequence = t.EventSequence;
                _events!.WriteLine(Jsonl.Serialize(t));
                _eventLines++;
            }
        }
        catch (Exception ex) { Fault(ex); }
    }

    public void Append(TransitionRecord t)
    {
        if (Faulted) return;
        try
        {
            lock (_gate)
            {
                _transitions!.WriteLine(Jsonl.Serialize(t));
                _transitionLines++;
            }
        }
        catch (Exception ex) { Fault(ex); }
    }

    private void Fault(Exception ex)
    {
        Faulted = true;
        FaultReason = ex.GetType().Name + ": " + ex.Message;
    }

    public void Flush()
    {
        lock (_gate) { _events?.Flush(); _transitions?.Flush(); }
    }

    /// <summary>
    /// Writes the manifest: file hashes and the sequence range each file covers, so a log
    /// can be proven complete rather than assumed so.
    /// </summary>
    public void WriteManifest()
    {
        try
        {
            Flush();
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"schemaVersion\": \"" + Versioning.SchemaVersion + "\",");
            sb.AppendLine("  \"engineVersion\": \"" + Versioning.EngineVersion + "\",");
            sb.AppendLine("  \"firstSequence\": " + _firstSequence.ToString(CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"lastSequence\": " + _lastSequence.ToString(CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"eventLines\": " + _eventLines + ",");
            sb.AppendLine("  \"transitionLines\": " + _transitionLines + ",");
            sb.AppendLine("  \"files\": {");
            sb.AppendLine("    \"events\": \"" + HashFile(_prefix + ".events.jsonl") + "\",");
            sb.AppendLine("    \"transitions\": \"" + HashFile(_prefix + ".transitions.jsonl") + "\"");
            sb.AppendLine("  }");
            sb.AppendLine("}");
            File.WriteAllText(Path.Combine(_directory, _prefix + ".manifest.json"), sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex) { Fault(ex); }
    }

    private string HashFile(string name)
    {
        var path = Path.Combine(_directory, name);
        if (!File.Exists(path)) return "";
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public void Dispose()
    {
        try { WriteManifest(); } catch { /* already faulted */ }
        lock (_gate)
        {
            _events?.Dispose(); _events = null;
            _transitions?.Dispose(); _transitions = null;
        }
    }
}

public sealed record LogReadResult(
    IReadOnlyList<object> Records,
    bool HasIncompleteTail,
    string? IncompleteTailText);

/// <summary>
/// Reads an event log back. A truncated last line is recoverable as an INCOMPLETE TAIL and
/// is reported as such; it is never silently accepted as valid data.
/// </summary>
public static class LogReader
{
    public static LogReadResult ReadEvents(string path)
    {
        var records = new List<object>();
        string? tail = null;
        var lines = File.ReadAllLines(path);

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            try
            {
                records.Add(line.Contains("\"kind\":\"DecisionTick\"", StringComparison.Ordinal)
                    ? Jsonl.DeserializeTick(line)
                    : Jsonl.DeserializeEvent(line));
            }
            catch (Exception) when (i == lines.Length - 1)
            {
                tail = line;   // only the LAST line may be an incomplete tail
            }
        }

        return new LogReadResult(records, tail is not null, tail);
    }
}
