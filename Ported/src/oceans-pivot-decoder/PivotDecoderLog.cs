using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OceansPivotDecoder
{
    /// <summary>One CSV line: a caller level, what it matched, and how it behaved.</summary>
    public sealed class LogRow
    {
        public string Date;
        public string Instrument;
        public string CallerLevel;

        public string BestMatchFamily;
        public string BestMatchLevelName;
        public string BestMatchPrice;
        public string DeltaPts;
        public string SessionModeOfMatch;
        public string MatchVerdict;

        public string Direction = string.Empty;
        public string Touched = string.Empty;
        public string TouchTimeCt = string.Empty;
        public string MaxAwayPts = string.Empty;
        public string MovedAway = string.Empty;
        public string MaxFavorablePts = string.Empty;
        public string MaxAdversePts = string.Empty;
        public string Outcome = string.Empty;

        /// <summary>
        /// Zone context. A caller level inside an x8 band carrying a naked POC is a different
        /// finding from one matching a lone Camarilla line, and the decode mission depends on the
        /// log being able to tell them apart months later.
        /// </summary>
        public string ZoneScore = string.Empty;
        public string ZoneMembers = string.Empty;
        public string AbsorptionValidated = string.Empty;

        /// <summary>The overnight inventory read at the time the row was written.</summary>
        public string InventoryRead = string.Empty;

        /// <summary>OPEN while the session is live, CLOSED once it has ended.</summary>
        public string Status = "OPEN";

        /// <summary>Identity of the row across rewrites: one row per caller level per session.</summary>
        public string Key
        {
            get { return Date + "~" + Instrument + "~" + CallerLevel; }
        }

        public string[] Fields
        {
            get
            {
                return new[]
                {
                    Date, Instrument, CallerLevel,
                    BestMatchFamily, BestMatchLevelName, BestMatchPrice, DeltaPts,
                    SessionModeOfMatch, MatchVerdict,
                    Direction, Touched, TouchTimeCt, MaxAwayPts, MovedAway,
                    MaxFavorablePts, MaxAdversePts, Outcome,
                    ZoneScore, ZoneMembers, AbsorptionValidated, InventoryRead, Status
                };
            }
        }
    }

    /// <summary>
    /// The session log. Rows are written the moment a session's levels are known, with the
    /// reaction columns blank and status OPEN, and the SAME row is rewritten in place as the
    /// session plays out and again when it ends. Writing only at session end would lose the whole
    /// day if ATAS were closed early -- which is exactly when you most want the record.
    ///
    /// Nothing in here may ever throw into the indicator: a locked file, a full disk, or a
    /// OneDrive-synced Documents folder must cost you the log line, not the levels on the chart.
    /// </summary>
    public static class PivotLog
    {
        public static readonly string[] Header =
        {
            "date", "instrument", "callerLevel",
            "bestMatchFamily", "bestMatchLevelName", "bestMatchPrice", "deltaPts",
            "sessionModeOfMatch", "matchVerdict",
            "direction", "touched", "touchTimeCt", "maxAwayPts", "movedAway",
            "maxFavorablePts", "maxAdversePts", "outcome",
            "zoneScore", "zoneMembers", "absorptionValidated", "inventoryRead", "status"
        };

        /// <summary>Guards against a corrupt or runaway file turning every rewrite into a stall.</summary>
        private const long MaxBytes = 8L * 1024 * 1024;

        /// <summary>
        /// Inserts or replaces the given rows by key, preserving every other line and the file's
        /// order. Returns null on success or a short reason on failure, for the chart readout.
        /// </summary>
        public static string Upsert(string path, List<LogRow> rows)
        {
            if (rows == null || rows.Count == 0) return null;

            try
            {
                if (string.IsNullOrWhiteSpace(path)) return "no log path set";

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var lines = new List<string>();
                var existing = new Dictionary<string, int>();

                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (info.Length > MaxBytes)
                        return "log file over " + (MaxBytes / 1024 / 1024) + " MB; rotate it";

                    lines.AddRange(File.ReadAllLines(path, Encoding.UTF8));

                    for (var i = 1; i < lines.Count; i++)
                    {
                        var key = KeyOf(lines[i]);
                        if (key != null) existing[key] = i;
                    }
                }

                if (lines.Count == 0) lines.Add(Join(Header));

                foreach (var row in rows)
                {
                    var line = Join(row.Fields);

                    int at;
                    if (existing.TryGetValue(row.Key, out at)) lines[at] = line;
                    else
                    {
                        existing[row.Key] = lines.Count;
                        lines.Add(line);
                    }
                }

                // Write beside the target and swap, so an interrupted write cannot truncate a
                // log that already holds weeks of sessions.
                var temp = path + ".tmp";
                File.WriteAllLines(temp, lines.ToArray(), Encoding.UTF8);

                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);

                return null;
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        /// <summary>First three fields of an existing line, as its key.</summary>
        private static string KeyOf(string line)
        {
            var fields = Split(line);
            if (fields.Count < 3) return null;

            return fields[0] + "~" + fields[1] + "~" + fields[2];
        }

        public static string Join(string[] fields)
        {
            var sb = new StringBuilder();

            for (var i = 0; i < fields.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Escape(fields[i]));
            }

            return sb.ToString();
        }

        private static string Escape(string value)
        {
            var v = value ?? string.Empty;

            if (v.IndexOf(',') < 0 && v.IndexOf('"') < 0 && v.IndexOf('\n') < 0) return v;

            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>Minimal RFC-4180 reader -- enough to find the key of a line we wrote.</summary>
        public static List<string> Split(string line)
        {
            var fields = new List<string>();
            if (line == null) return fields;

            var sb = new StringBuilder();
            var quoted = false;

            for (var i = 0; i < line.Length; i++)
            {
                var ch = line[i];

                if (quoted)
                {
                    if (ch != '"') { sb.Append(ch); continue; }

                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else quoted = false;
                }
                else if (ch == '"') quoted = true;
                else if (ch == ',') { fields.Add(sb.ToString()); sb.Length = 0; }
                else sb.Append(ch);
            }

            fields.Add(sb.ToString());
            return fields;
        }

        public static string Num(decimal v)
        {
            return PivotMath.Round2(v).ToString("F2", CultureInfo.InvariantCulture);
        }
    }
}
