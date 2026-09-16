using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

// Constructs the indicator the way ATAS does, outside ATAS. A constructor that throws is
// invisible in the platform -- the indicator simply never appears, or appears and never draws --
// so it is worth catching here, where the exception is actually readable.
//
// It also prints every [Display] property, which is the cheapest way to catch a settings pane
// that will come up empty or mislabelled, and asserts the handful of defaults that have cost
// real debugging rounds across this suite.
//
// Main must not touch an ATAS type itself: the JIT resolves a method's types when the method is
// entered, which would happen before the resolver below is attached.
static class Program
{
    private const string AtasDir = @"C:\Program Files (x86)\ATAS Platform";

    private static int _problems;

    static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs e)
        {
            var path = Path.Combine(AtasDir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        return Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Run()
    {
        OceansAnchor.OceansAnchor anchor;

        try
        {
            anchor = new OceansAnchor.OceansAnchor();
            Console.WriteLine("Constructed OK.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("CONSTRUCTOR THREW: " + ex.GetType().Name);
            Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace);
            return 1;
        }

        try
        {
            var ind = (ATAS.Indicators.Indicator)anchor;

            Console.WriteLine("Panel: '" + ind.Panel + "'");
            Console.WriteLine("EnableCustomDrawing: " + ind.EnableCustomDrawing);
            Console.WriteLine("DataSeries count: " + ind.DataSeries.Count);

            for (var i = 0; i < ind.DataSeries.Count; i++)
                Console.WriteLine("  [" + i + "] " + ind.DataSeries[i].GetType().Name +
                                  "  Id=" + ind.DataSeries[i].Id);

            Check(ind.EnableCustomDrawing, "custom drawing is enabled (or nothing renders)");

            // The zone marks series has to exist or every absorption event is dropped silently.
            var marks = false;
            foreach (var series in ind.DataSeries)
                if (series is ATAS.Indicators.PriceSelectionDataSeries) marks = true;

            Check(marks, "the PriceSelection marks series is registered");

            // 16, not 15. 15:00 is the cash close and trading continues through it; the empty
            // hour is 16:00-17:00. With 15 the bar clock never settles and the indicator draws
            // nothing at all, with no error pointing here. This has shipped wrong three times
            // across the oceans-* suite, so it is asserted rather than trusted.
            Check(anchor.HaltHour == 16,
                  "daily halt hour is 16, not the cash close at 15 (got " + anchor.HaltHour + ")");

            Check(anchor.TimeZoneId == "Central Standard Time",
                  "the one time zone is Central (got '" + anchor.TimeZoneId + "')");

            // Houston time in the gate, and the personal rule on by default.
            Check(anchor.WindowStart == new TimeSpan(8, 30, 0), "A+ window opens at 08:30 Central");
            Check(anchor.WindowEnd == new TimeSpan(10, 30, 0), "A+ window closes at 10:30 Central");
            Check(anchor.SuppressFriday, "Friday suppression is on by default");
            Check(!anchor.AllowAllHours, "all-hours is off by default");

            // The ten-session run. It ships silent so it can sit on the trading chart, and
            // everything the CSV depends on is on by default as well as forced by silent mode.
            Check(anchor.SilentCalibration, "ships in silent calibration mode");
            Check(anchor.WriteLog, "CSV log is on by default");
            Check(anchor.UseLiveTape, "live tape is on by default");
            Check(anchor.UseClusterPath, "cluster path is on by default");

            // It is an indicator. If this ever inherits from a strategy type, that is a
            // different tool with different consequences.
            // Checked by name up the base chain rather than against the type, so the smoke test
            // does not have to reference ATAS.Strategies just to assert we are not one.
            var strategy = false;
            for (var t = anchor.GetType(); t != null; t = t.BaseType)
                if (t.Name.Contains("Strategy")) strategy = true;

            Check(!strategy, "this is an indicator and cannot place orders");

            Console.WriteLine();
            Console.WriteLine("Settings:");

            var group = string.Empty;
            var shown = 0;

            foreach (var p in anchor.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var display = (DisplayAttribute)Attribute.GetCustomAttribute(p, typeof(DisplayAttribute));
                if (display == null) continue;

                // Inherited settings can carry a [Display] with no Name or no GroupName at all,
                // so neither may be dereferenced blind.
                var groupName = display.GroupName ?? "(ungrouped)";
                if (groupName != group)
                {
                    group = groupName;
                    Console.WriteLine("  " + group);
                }

                object value;
                try { value = p.GetValue(anchor); }
                catch (Exception ex) { value = "<threw " + ex.GetType().Name + ">"; }

                Console.WriteLine("    " + (display.Name ?? p.Name).PadRight(30) + " = " + value);
                shown++;
            }

            Check(shown > 30, "the settings pane is populated (" + shown + " properties)");

            Console.WriteLine();
            Console.WriteLine("CSV log path: " + OceansAnchor.AnchorLog.DefaultPath());

            Console.WriteLine();
            Console.WriteLine(_problems == 0 ? "SMOKE OK" : _problems + " SMOKE PROBLEM(S)");

            return _problems == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("SMOKE THREW: " + ex.GetType().Name);
            Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static void Check(bool ok, string what)
    {
        if (ok) return;

        _problems++;
        Console.WriteLine("  PROBLEM  " + what);
    }
}
