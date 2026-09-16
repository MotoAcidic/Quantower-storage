using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

// Constructs the indicator the way ATAS does, outside ATAS. A constructor that throws is
// invisible in the platform -- the indicator simply never appears or never draws -- so it is
// worth catching here, where the exception is actually readable.
//
// It also runs the math self-test through the constructor and prints every [Display] property,
// which is the cheapest way to catch a settings pane that will come up empty or mislabelled.
//
// Main must not touch an ATAS type itself: the JIT resolves a method's types when the method is
// entered, which would happen before the resolver below is attached.
static class Program
{
    private const string AtasDir = @"C:\Program Files (x86)\ATAS Platform";

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
        try
        {
            var decoder = new OceansPivotDecoder.PivotDecoder();
            Console.WriteLine("Constructed OK.");

            var ind = (ATAS.Indicators.Indicator)decoder;

            Console.WriteLine("Panel: '" + ind.Panel + "'");
            Console.WriteLine("DrawAbovePrice: " + ind.DrawAbovePrice);
            Console.WriteLine("DataSeries count: " + ind.DataSeries.Count);

            for (var i = 0; i < ind.DataSeries.Count; i++)
            {
                var v = ind.DataSeries[i] as ATAS.Indicators.ValueDataSeries;
                if (v == null) continue;

                Console.WriteLine("  [" + i + "] IsHidden=" + v.IsHidden +
                                  "  VisualType=" + v.VisualType +
                                  "  ShowZeroValue=" + v.ShowZeroValue);
            }

            // The constructor runs the math self-test. If it failed, the indicator would draw a
            // red banner instead of trustworthy levels -- fail the smoke test here instead.
            string report;
            var ok = OceansPivotDecoder.PivotMath.SelfTest(out report);
            Console.WriteLine();
            Console.WriteLine("Math self-test: " + (ok ? "PASS" : "FAIL"));
            Console.WriteLine(report);

            Console.WriteLine();
            Console.WriteLine("Settings:");

            var group = string.Empty;
            foreach (var p in decoder.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
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
                try { value = p.GetValue(decoder); }
                catch (Exception ex) { value = "<threw " + ex.GetType().Name + ">"; }

                Console.WriteLine("    " + (display.Name ?? p.Name).PadRight(30) + " = " + value);
            }

            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("CONSTRUCTOR THREW: " + ex.GetType().Name);
            Console.WriteLine(ex.Message);
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
    }
}
