using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;

// Constructs the indicator the way ATAS does, outside ATAS. A constructor that throws is
// invisible in the platform -- the indicator simply never appears or never draws -- so it is
// worth catching here, where the exception is actually readable.
//
// Main must not touch an ATAS type itself: the JIT resolves a method's types when the method
// is entered, which would happen before the resolver below is attached.
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
            var indicator = new OceansSma.OceansSmaIndicator();
            Console.WriteLine("Constructed OK.");

            var ind = (ATAS.Indicators.Indicator)indicator;
            var series = ind.DataSeries;

            Console.WriteLine("Panel: '" + ind.Panel + "'");
            foreach (var f in typeof(ATAS.Indicators.IndicatorDataProvider)
                              .GetFields(BindingFlags.Public | BindingFlags.Static))
                Console.WriteLine("  IndicatorDataProvider." + f.Name + " = '" + f.GetValue(null) + "'");

            Console.WriteLine("SourceDataSeries null? " + (ind.SourceDataSeries == null));
            Console.WriteLine("Indicator.DrawAbovePrice: " + ind.DrawAbovePrice);
            Console.WriteLine("DataSeries count: " + series.Count);

            for (var i = 0; i < series.Count; i++)
            {
                var v = series[i] as ATAS.Indicators.ValueDataSeries;
                Console.WriteLine("  [" + i + "] " + series[i].GetType().Name + "  name=" + series[i].Name);
                if (v == null) continue;

                Console.WriteLine("        IsVisible=" + v.IsVisible +
                                  "  IsHidden=" + v.IsHidden +
                                  "  VisualType=" + v.VisualType +
                                  "  ScaleIt=" + v.ScaleIt +
                                  "  ShowZeroValue=" + v.ShowZeroValue +
                                  "  DrawAbovePrice=" + v.DrawAbovePrice +
                                  "  Width=" + v.Width +
                                  "  Color=" + v.Color);
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
