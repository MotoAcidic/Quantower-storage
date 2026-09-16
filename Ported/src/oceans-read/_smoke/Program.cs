using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

// Constructs the indicator the way ATAS does, outside ATAS. A constructor that throws is
// invisible in the platform -- the indicator simply never appears in the list -- so it is worth
// catching here, where the exception is actually readable.
//
// It also prints every setting with its group and default, because the settings dialog is the
// only other place to see them and looking there costs a restart of ATAS.
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
            var indicator = new OceansRead.OceansReadIndicator();
            Console.WriteLine("Constructed OK.");

            var ind = (ATAS.Indicators.Indicator)indicator;

            Console.WriteLine("Panel: '" + ind.Panel + "'");
            Console.WriteLine("DrawAbovePrice: " + ind.DrawAbovePrice);
            Console.WriteLine("EnableCustomDrawing: " + ind.EnableCustomDrawing);
            Console.WriteLine("DataSeries count: " + ind.DataSeries.Count);

            for (var i = 0; i < ind.DataSeries.Count; i++)
            {
                var v = ind.DataSeries[i] as ATAS.Indicators.ValueDataSeries;
                if (v == null) continue;

                // The inherited series must be invisible: this indicator plots nothing per bar,
                // and a stray zero line across the price panel would be its own bug report.
                Console.WriteLine("  [" + i + "] IsHidden=" + v.IsHidden +
                                  "  VisualType=" + v.VisualType +
                                  "  ScaleIt=" + v.ScaleIt);
            }

            Console.WriteLine();
            Console.WriteLine("Settings as ATAS will show them:");

            var group = "";
            foreach (var property in indicator.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var display = property.GetCustomAttribute<DisplayAttribute>();
                if (display == null) continue;
                if (property.DeclaringType != typeof(OceansRead.OceansReadIndicator)) continue;

                if (display.GroupName != group)
                {
                    group = display.GroupName;
                    Console.WriteLine("  " + group);
                }

                var value = property.CanRead ? property.GetValue(indicator) : null;
                Console.WriteLine("      " + display.Name.PadRight(38) + " = " + value);
            }

            return 0;
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
