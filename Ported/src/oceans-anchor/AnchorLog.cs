using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace OceansAnchor
{
    /// <summary>
    /// The calibration log. One row per Triggered episode, finalized when it confirms, expires
    /// or breaks.
    ///
    /// This file IS the ten-session protocol. Every threshold in the indicator ships as a guess
    /// -- SizeFloor 100 is an NQ round number, not a measurement -- and the only way any of them
    /// become real is reading them back off what actually printed. So the columns are chosen to
    /// answer the specific offline questions: p95 of largest_trade sets the floor, the expiry
    /// rate says whether the clock is too short, and the tape-vs-cluster agreement rate says
    /// whether the historical marks can be trusted at all.
    ///
    /// Nothing here is allowed to throw. A locked file or a full disk must cost a log row, never
    /// the indicator.
    /// </summary>
    public sealed class AnchorLog
    {
        public const string Header =
            "date,time_ct,zone_kind,rank,side,arrival_atr_mult,largest_trade,stacked_events," +
            "path,touch_delta,resolved_in_clock,confirmed,expired,broken,close_pos_pct," +
            "displacement_ticks";

        private readonly string _path;
        private bool _headerChecked;

        public string LastError { get; private set; }
        public string Path { get { return _path; } }

        public AnchorLog(string path) { _path = path; }

        /// <summary>Documents, not %APPDATA%: this file is meant to be opened and read by a human.</summary>
        public static string DefaultPath()
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return System.IO.Path.Combine(docs, "ATAS", "OceansAnchor", "anchor_log.csv");
        }

        public static string Format(Episode e)
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder(160);

            sb.Append(e.StartedLocal.ToString("yyyy-MM-dd", c)).Append(',');
            sb.Append(e.StartedLocal.ToString("HH:mm:ss", c)).Append(',');
            sb.Append(Clean(e.ZoneKind)).Append(',');
            sb.Append(e.Rank.ToString(c)).Append(',');
            sb.Append(e.Side == TestSide.SupportLong ? "long" : "short").Append(',');
            sb.Append(Round(e.ArrivalAtrMult, 3)).Append(',');
            sb.Append(Round(e.LargestTrade, 0)).Append(',');
            sb.Append(e.StackedEvents.ToString(c)).Append(',');
            sb.Append(e.Path == EventPath.Tape ? "tape" : "cluster").Append(',');
            sb.Append(Round(e.TouchDelta, 0)).Append(',');
            sb.Append(Bool(e.ResolvedInClock)).Append(',');
            sb.Append(Bool(e.Confirmed)).Append(',');
            sb.Append(Bool(e.Expired)).Append(',');
            sb.Append(Bool(e.Broken)).Append(',');
            sb.Append(Round(e.ClosePosPct, 1)).Append(',');
            sb.Append(Round(e.DisplacementTicks, 1));

            return sb.ToString();
        }

        /// <summary>
        /// What makes two rows the same episode: when it started, what kind of zone, which side.
        /// Rank and the outcome columns are left out on purpose -- they are results, and a
        /// result can come out differently on a replay without it being a different event.
        /// </summary>
        public static string KeyOf(string row)
        {
            if (string.IsNullOrEmpty(row)) return null;

            var c = row.Split(',');
            if (c.Length < 5) return null;

            return c[0] + "," + c[1] + "," + c[2] + "," + c[4];
        }

        /// <summary>
        /// True when the row reached the file.
        ///
        /// ATAS re-runs the whole series on every load, restart and setting change, and every
        /// replay finishes the same historical episodes again. Without the key check, ten
        /// sessions with a restart each would put every early row in the file ten times, and
        /// the p95 and hit rates would be computed off copies. The file is re-read on every
        /// write rather than cached, so a second chart writing the same file is caught too.
        /// </summary>
        public bool Write(Episode e)
        {
            if (e == null || e.Written) return false;

            try
            {
                var dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                var row = Format(e);

                if (AlreadyLogged(KeyOf(row)))
                {
                    e.Written = true;
                    LastError = null;
                    return false;
                }

                if (!_headerChecked)
                {
                    // Length rather than Exists: an empty file left behind by a failed write
                    // still needs its header, and appending rows under no header silently
                    // produces a CSV nothing will parse.
                    var needsHeader = !File.Exists(_path) || new FileInfo(_path).Length == 0;
                    if (needsHeader) File.AppendAllText(_path, Header + Environment.NewLine);

                    _headerChecked = true;
                }

                File.AppendAllText(_path, row + Environment.NewLine);

                e.Written = true;
                LastError = null;
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return false;
            }
        }

        private bool AlreadyLogged(string key)
        {
            if (key == null || !File.Exists(_path)) return false;

            foreach (var line in File.ReadLines(_path))
                if (KeyOf(line) == key) return true;

            return false;
        }

        private static string Bool(bool v) { return v ? "1" : "0"; }

        private static string Round(decimal v, int places)
        {
            return Math.Round(v, places).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>No quoting logic anywhere: strip what would need it instead.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace(',', ' ').Replace('"', ' ').Replace('\n', ' ').Replace('\r', ' ');
        }
    }
}
