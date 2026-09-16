using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

// Constructs the indicator and the strategy the way ATAS does, outside ATAS. A constructor that
// throws is invisible in the platform -- the thing simply never appears in the list -- so it is
// worth catching here, where the exception is actually readable.
//
// It also prints every setting with its group and default, because the settings dialog is the
// only other place to see them and that needs a restart of ATAS to look at.
//
// Main must not touch an ATAS type itself: the JIT resolves a method's types when the method is
// entered, which would happen before the resolver below is attached.
internal static class Program
{
    private const string AtasDir = @"C:\Program Files (x86)\ATAS Platform";

    private static int Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs e)
        {
            var path = Path.Combine(AtasDir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        return Run();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run()
    {
        var bad = 0;

        bad += Check("AsiaWickIndicator", delegate
        {
            var ind = new OceansAsiaWick.AsiaWickIndicator();
            var b = (ATAS.Indicators.Indicator)ind;

            Console.WriteLine("  DataSeries: " + b.DataSeries.Count);

            for (var i = 0; i < b.DataSeries.Count; i++)
            {
                var v = b.DataSeries[i] as ATAS.Indicators.ValueDataSeries;
                if (v != null)
                    Console.WriteLine("    [" + i + "] " + v.Name + "  " + v.VisualType);
            }

            // The five series the renderer indexes must all be there, or a signal marker would
            // write past the end of DataSeries at the worst possible moment.
            if (b.DataSeries.Count != 5)
                throw new Exception("expected 5 data series, found " + b.DataSeries.Count);

            return ind;
        }, ref bad);

        bad += Check("AsiaWickStrategy", delegate
        {
            var st = new OceansAsiaWick.AsiaWickStrategy();

            // The single most important default in the whole build.
            var so = st.GetType().GetProperty("SignalOnly");
            var on = (bool)so.GetValue(st);

            Console.WriteLine("  SignalOnly default: " + on);

            if (!on)
                throw new Exception("SignalOnly must ship ON -- the phase gate depends on it");

            return st;
        }, ref bad);

        Console.WriteLine();
        Console.WriteLine(bad == 0 ? "smoke OK" : "smoke FAILED (" + bad + ")");
        return bad == 0 ? 0 : 1;
    }

    private static int Check(string name, Func<object> build, ref int bad)
    {
        Console.WriteLine("=== " + name + " ===");

        try
        {
            var o = build();
            DumpSettings(o);
            Console.WriteLine("  constructed OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("  FAILED: " + ex.GetType().Name + ": " + ex.Message);
            return 1;
        }
    }

    private static void DumpSettings(object o)
    {
        var rows = new List<string>();

        foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var d = p.GetCustomAttribute<DisplayAttribute>();
            if (d == null) continue;

            object val;
            try { val = p.GetValue(o); }
            catch (Exception ex) { val = "<" + ex.GetType().Name + ">"; }

            rows.Add("    " + (d.GroupName ?? "?").PadRight(16) + " " +
                     (d.Name ?? p.Name).PadRight(32) + " = " + val);
        }

        rows.Sort();
        foreach (var r in rows) Console.WriteLine(r);
    }
}
