using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

// Constructs the indicator the way ATAS does, outside ATAS. A constructor that throws is
// invisible in the platform -- the indicator never appears, or appears and never draws -- so it
// is caught here, where the exception is readable. Also prints every [Display] property and
// asserts the defaults that have cost real debugging rounds across the suite.
//
// Main must not touch an ATAS type itself: the JIT resolves a method's types on entry, before
// the resolver below is attached.
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
        OceansOrderBlocks.OceansOrderBlocks ob;

        try
        {
            ob = new OceansOrderBlocks.OceansOrderBlocks();
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
            var ind = (ATAS.Indicators.Indicator)ob;

            Console.WriteLine("EnableCustomDrawing: " + ind.EnableCustomDrawing);
            Console.WriteLine("DataSeries count: " + ind.DataSeries.Count);

            Check(ind.EnableCustomDrawing, "custom drawing is enabled (or nothing renders)");

            // 16, not 15. See ~/dev/CLAUDE.md -- shipped wrong three times.
            Check(ob.HaltHour == 16, "daily halt hour is 16, not the cash close at 15 (got " + ob.HaltHour + ")");
            Check(ob.TimeZoneId == "Central Standard Time", "the one time zone is Central (got '" + ob.TimeZoneId + "')");

            Check(ob.SessionStart == new TimeSpan(8, 30, 0), "session opens 08:30 Houston, not 09:30 ET");
            Check(ob.SessionEnd == new TimeSpan(15, 0, 0), "session ends 15:00 Houston");

            // The Pine defaults, carried over.
            Check(!ob.Show1H && ob.Show4H && ob.ShowDaily && ob.ShowWeekly && !ob.ShowSession,
                  "timeframe defaults match the Pine: 4H, D, W on; 1H and session off");
            Check(ob.MaxPerSide == 10, "10 active per side per TF");
            Check(ob.MinDisplacementAtr == 0m, "displacement filter off by default");
            Check(!ob.KeepMitigated, "mitigated blocks deleted by default");

            var strategy = false;
            for (var t = ob.GetType(); t != null; t = t.BaseType)
                if (t.Name.Contains("Strategy")) strategy = true;
            Check(!strategy, "this is an indicator and cannot place orders");

            Console.WriteLine();
            Console.WriteLine("Settings:");

            var group = string.Empty;
            var shown = 0;

            foreach (var p in ob.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var display = (DisplayAttribute)Attribute.GetCustomAttribute(p, typeof(DisplayAttribute));
                if (display == null) continue;

                var groupName = display.GroupName ?? "(ungrouped)";
                if (groupName != group)
                {
                    group = groupName;
                    Console.WriteLine("  " + group);
                }

                object value;
                try { value = p.GetValue(ob); }
                catch (Exception ex) { value = "<threw " + ex.GetType().Name + ">"; }

                Console.WriteLine("    " + (display.Name ?? p.Name).PadRight(30) + " = " + value);
                shown++;
            }

            Check(shown >= 30, "the settings pane is populated (" + shown + " properties)");

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
