using System.Globalization;
using System.Text;
using AuctionResponse.Core;

namespace AuctionResponse.Replay;

/// <summary>
/// Persists the self-observed baseline between sessions.
///
/// Written automatically to a fixed location beside the platform's own settings, so there is
/// no path for anyone to type and nothing to remember. A corrupt or partial file is discarded
/// rather than half-loaded: a baseline is the reference every alert is measured against, and a
/// silently truncated one would quietly change what "unusual volume" means.
/// </summary>
public static class LiveBaselineIo
{
    private const string Header = "sessionId,bucket,windowMs,buy,sell,absDelta,absResponse";

    /// <summary>Default location: beside the ATAS settings, one file per instrument.</summary>
    public static string DefaultPath(InstrumentKey instrument)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var safe = new string((instrument.Symbol + "_" + instrument.Expiry)
            .Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
        if (safe.Length == 0) safe = "unknown";
        return Path.Combine(appData, "ATAS", "auction-response-baseline-" + safe + ".csv");
    }

    public static void Save(LiveBaseline baseline, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(Header);

        foreach (var r in baseline.Export())
            sb.Append(r.SessionId).Append(',')
              .Append(r.Bucket.ToString(inv)).Append(',')
              .Append(r.WindowMs.ToString(inv)).Append(',')
              .Append(r.Buy.ToString("R", inv)).Append(',')
              .Append(r.Sell.ToString("R", inv)).Append(',')
              .Append(r.AbsDelta.ToString("R", inv)).Append(',')
              .Append(double.IsNaN(r.AbsResponse) ? "" : r.AbsResponse.ToString("R", inv))
              .AppendLine();

        // Written to a temporary file and moved into place, so a crash mid-write cannot leave
        // a half-file where the baseline used to be.
        var temp = path + ".tmp";
        File.WriteAllText(temp, sb.ToString(), Encoding.UTF8);
        File.Move(temp, path, overwrite: true);
    }

    public static LiveBaseline Load(string path)
    {
        var baseline = new LiveBaseline();
        if (!File.Exists(path)) return baseline;

        var inv = CultureInfo.InvariantCulture;
        var rows = new List<LiveBaseline.Row>();

        foreach (var line in File.ReadLines(path).Skip(1))
        {
            if (line.Length == 0) continue;
            var f = line.Split(',');
            if (f.Length < 7) continue;   // a truncated tail line is dropped, never guessed at

            if (!int.TryParse(f[1], NumberStyles.Integer, inv, out var bucket)) continue;
            if (!int.TryParse(f[2], NumberStyles.Integer, inv, out var window)) continue;
            if (!double.TryParse(f[3], NumberStyles.Float, inv, out var buy)) continue;
            if (!double.TryParse(f[4], NumberStyles.Float, inv, out var sell)) continue;
            if (!double.TryParse(f[5], NumberStyles.Float, inv, out var absDelta)) continue;

            var response = double.TryParse(f[6], NumberStyles.Float, inv, out var r) ? r : double.NaN;
            rows.Add(new LiveBaseline.Row(f[0], bucket, window, buy, sell, absDelta, response));
        }

        baseline.Import(rows);
        return baseline;
    }
}
