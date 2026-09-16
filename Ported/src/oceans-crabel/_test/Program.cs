using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OceansCrabel;

class Program
{
    static int _failures;

    static int Main()
    {
        var csv = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "nq_daily.csv");
        csv = Path.GetFullPath(csv);

        var sessions = Load(csv);
        Console.WriteLine($"Loaded {sessions.Count} sessions, {sessions[0].Date:yyyy-MM-dd} .. {sessions[^1].Date:yyyy-MM-dd}");
        Console.WriteLine();

        PrintTable(sessions, 12);
        Console.WriteLine();

        // ---- Expectations independently derived from the PowerShell pass over the same feed ----
        Console.WriteLine("== Assertions ==");

        var idx = Index(sessions, "2026-08-07");

        AssertRange(sessions, "2026-08-05", 543.25m);
        AssertRange(sessions, "2026-08-06", 445.00m);
        AssertRange(sessions, "2026-08-07", 414.25m);

        AssertFlag("08-05 NR4", CrabelMath.IsNarrowestRange(sessions, Index(sessions, "2026-08-05"), 4), true);
        AssertFlag("08-05 NR7", CrabelMath.IsNarrowestRange(sessions, Index(sessions, "2026-08-05"), 7), true);
        AssertFlag("08-06 NR4", CrabelMath.IsNarrowestRange(sessions, Index(sessions, "2026-08-06"), 4), true);
        AssertFlag("08-06 NR7", CrabelMath.IsNarrowestRange(sessions, Index(sessions, "2026-08-06"), 7), true);
        AssertFlag("08-07 NR4", CrabelMath.IsNarrowestRange(sessions, idx, 4), true);
        AssertFlag("08-07 NR7", CrabelMath.IsNarrowestRange(sessions, idx, 7), true);
        AssertFlag("08-07 2BarNR20", CrabelMath.IsTwoBarNarrowest(sessions, idx, 20), true);

        // None of the recent sessions were inside days -- each took out one side of the prior.
        AssertFlag("08-07 insideDay", CrabelMath.IsInsideDay(sessions, idx), false);
        AssertFlag("08-06 insideDay", CrabelMath.IsInsideDay(sessions, Index(sessions, "2026-08-06")), false);
        AssertFlag("08-07 ID/NR4", CrabelMath.Evaluate(sessions, idx).IdNr4, false);

        // Stretch is indexed by the session it APPLIES TO, built from the 10 sessions
        // behind it. These two differ by one trading day and must not be conflated:
        // 148.3 was the bracket for Friday itself; Monday's bracket is 150.8.
        AssertNear("stretch applied to 08-07", CrabelMath.Stretch(sessions, idx), 148.3m, 0.1m);
        AssertNear("stretch for next session", CrabelMath.Stretch(sessions, idx + 1), 150.8m, 0.1m);

        // Close location: 08-07 closed in the top of its range.
        AssertNear("08-07 CLV", CrabelMath.CloseLocationValue(sessions[idx]), 0.92m, 0.01m);
        AssertNear("08-07 range %20d", CrabelMath.RangePctOfAverage(sessions, idx, 20), 60m, 1m);

        // ---- Null-safety: insufficient history must yield null, never a confident false ----
        AssertNull("NR7 at index 0", CrabelMath.IsNarrowestRange(sessions, 0, 7));
        AssertNull("NR7 at index 5", CrabelMath.IsNarrowestRange(sessions, 5, 7));
        AssertNull("insideDay at index 0", CrabelMath.IsInsideDay(sessions, 0));
        AssertNull("stretch at index 9", CrabelMath.Stretch(sessions, 9));
        AssertNull("2BarNR20 at index 10", CrabelMath.IsTwoBarNarrowest(sessions, 10, 20));

        var early = CrabelMath.Evaluate(sessions, 3);
        AssertNull("Evaluate(3).Nr7", early.Nr7);
        // Index 3 has exactly 4 sessions behind it, so NR4 -- and therefore ID/NR4 -- is
        // legitimately knowable there. Index 2 is the first point where it is not.
        AssertNotNull("Evaluate(3).IdNr4", early.IdNr4);
        AssertNull("Evaluate(2).IdNr4", CrabelMath.Evaluate(sessions, 2).IdNr4);
        AssertNotNull("Evaluate(3).Nr4", early.Nr4);

        // Boundary: stretch becomes available exactly at index 10, not before.
        AssertNotNull("stretch at index 10", CrabelMath.Stretch(sessions, 10));

        // Zero-range session has undefined CLV rather than a fabricated midpoint.
        var flat = new SessionBar { Open = 100, High = 100, Low = 100, Close = 100 };
        AssertNull("CLV of zero-range session", CrabelMath.CloseLocationValue(flat));

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"{_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    static int Index(List<SessionBar> s, string date) =>
        s.FindIndex(x => x.Date.ToString("yyyy-MM-dd") == date);

    static List<SessionBar> Load(string path)
    {
        var list = new List<SessionBar>();
        foreach (var line in File.ReadAllLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var p = line.Split(',').Select(x => x.Trim('"')).ToArray();
            list.Add(new SessionBar
            {
                Date = DateTime.ParseExact(p[0], "yyyy-MM-dd", CultureInfo.InvariantCulture),
                Open = decimal.Parse(p[1], CultureInfo.InvariantCulture),
                High = decimal.Parse(p[2], CultureInfo.InvariantCulture),
                Low = decimal.Parse(p[3], CultureInfo.InvariantCulture),
                Close = decimal.Parse(p[4], CultureInfo.InvariantCulture),
                Valid = true,
                Complete = true,
                BarCount = 78
            });
        }
        return list;
    }

    static void PrintTable(List<SessionBar> s, int tail)
    {
        Console.WriteLine($"{"Date",-12}{"Range",10}{"Stretch",10}  Flags");
        for (var i = Math.Max(0, s.Count - tail); i < s.Count; i++)
        {
            var f = CrabelMath.Evaluate(s, i);
            var st = CrabelMath.Stretch(s, i);
            var flags = new List<string>();
            if (f.InsideDay == true) flags.Add("ID");
            if (f.Nr4 == true) flags.Add("NR4");
            if (f.Nr7 == true) flags.Add("NR7");
            if (f.Nr20 == true) flags.Add("NR20");
            if (f.TwoBarNr20 == true) flags.Add("2BarNR20");

            Console.WriteLine($"{s[i].Date:yyyy-MM-dd}  {s[i].Range,10:0.0}{(st.HasValue ? st.Value.ToString("0.0") : "-"),10}  {string.Join("/", flags)}");
        }
    }

    static void AssertRange(List<SessionBar> s, string date, decimal expected)
    {
        var i = Index(s, date);
        Check($"{date} range", i >= 0 && Math.Abs(s[i].Range - expected) < 0.01m,
              i >= 0 ? s[i].Range.ToString("0.00") : "missing", expected.ToString("0.00"));
    }

    static void AssertFlag(string name, bool? actual, bool expected) =>
        Check(name, actual == expected, actual?.ToString() ?? "null", expected.ToString());

    static void AssertNear(string name, decimal? actual, decimal expected, decimal tol) =>
        Check(name, actual.HasValue && Math.Abs(actual.Value - expected) <= tol,
              actual?.ToString("0.000") ?? "null", $"{expected} +/-{tol}");

    static void AssertNull(string name, object actual) =>
        Check(name, actual == null, actual?.ToString() ?? "null", "null");

    static void AssertNotNull(string name, object actual) =>
        Check(name, actual != null, actual?.ToString() ?? "null", "non-null");

    static void Check(string name, bool ok, string actual, string expected)
    {
        if (!ok) _failures++;
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name,-28} actual={actual,-12} expected={expected}");
    }
}
