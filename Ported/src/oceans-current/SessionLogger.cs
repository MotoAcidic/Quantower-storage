using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace OceansCurrent
{
    /// <summary>
    /// One row per committed bar, appended to a file per session. This is the whole point of
    /// running the thing decision-passive for twenty sessions: without a log there is nothing to
    /// join to the Crabel session database, no fitted weights, and no honest way to find out
    /// whether the blend beats "long above VWAP" -- which is the test it has to pass to deserve
    /// the screen space.
    ///
    /// The row format is a pure function so the harness can assert it. Nothing about a log
    /// failure is allowed to reach the chart: a locked file loses a row, not the badge.
    /// </summary>
    public sealed class SessionLogger
    {
        public const string Header =
            "date,time,bar,close,sub1,sub2,sub3,sub4,sub5,sub6," +
            "regime,wall,score,raw,state,confidence,flip_level,gex_age_s";

        private readonly string _folder;
        private readonly HashSet<string> _owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _path;
        private DateTime _day;
        private string _problem;

        public SessionLogger(string folder) { _folder = folder; }

        /// <summary>
        /// Forget which files this run has written, so the next row to reach each one starts it
        /// over. Called when the engine is rebuilt from bar zero.
        ///
        /// This is what keeps a reload from doubling the history. The engine replays a rebuild
        /// bar-for-bar identically -- that is asserted -- so the honest thing for a replayed day
        /// is to *replace* its rows, not to append a second identical run underneath the first.
        /// Appending was silent, and it is the calibration that paid: the join in section 9 reads
        /// duplicated sessions as extra evidence and fits every weight against them.
        /// </summary>
        public void Reset()
        {
            _owned.Clear();
            _path = null;
        }

        /// <summary>The last failure, in the words the badge prints, or null while fine.</summary>
        public string Problem => _problem;

        public static string Row(BiasRecord r, decimal tick)
        {
            var sb = new StringBuilder(160);

            sb.Append(r.Local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.Local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(r.Bar.ToString(CultureInfo.InvariantCulture)).Append(',');
            sb.Append(Num(r.Close)).Append(',');

            for (var i = 0; i < BiasEngine.FactorCount; i++)
            {
                // An absent factor is written blank, never zero. A zero here would enter the
                // regression as a real neutral vote and quietly bias every fitted weight.
                sb.Append(r.Subs != null && i < r.Subs.Length && r.Subs[i].Available
                    ? Num(r.Subs[i].Value)
                    : "");
                sb.Append(',');
            }

            sb.Append((int)r.Regime).Append(',');
            sb.Append(r.InWallZone ? 1 : 0).Append(',');
            sb.Append(r.ScoreKnown ? Num(r.Score) : "").Append(',');
            sb.Append(r.ScoreKnown ? Num(r.Raw) : "").Append(',');
            sb.Append(BiasEngine.StateText(r.State)).Append(',');
            sb.Append(r.ScoreKnown ? Num(r.Confidence) : "").Append(',');
            sb.Append(r.Flip.Known ? Num(BiasEngine.Round(r.Flip.Value, tick)) : "").Append(',');
            sb.Append(r.GexAgeSeconds >= 0 ? r.GexAgeSeconds.ToString(CultureInfo.InvariantCulture) : "");

            return sb.ToString();
        }

        private static string Num(decimal value) =>
            Math.Round(value, 4, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

        /// <summary>Appends one row. Returns false and records why, rather than throwing.</summary>
        public bool Append(BiasRecord record, decimal tick)
        {
            try
            {
                var day = record.Local.Date;

                if (_path == null || day != _day)
                {
                    Directory.CreateDirectory(_folder);
                    _day = day;
                    _path = Path.Combine(_folder,
                        "obe_" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".csv");

                    // First row this run has sent to this day: take the file over and start it
                    // from the header, whatever a previous run left there. Anything still to
                    // come for this day is appended underneath.
                    if (_owned.Add(_path))
                        File.WriteAllText(_path, Header + Environment.NewLine);
                }

                File.AppendAllText(_path, Row(record, tick) + Environment.NewLine);
                _problem = null;
                return true;
            }
            catch (Exception ex)
            {
                _problem = ex.GetType().Name;
                return false;
            }
        }
    }
}
