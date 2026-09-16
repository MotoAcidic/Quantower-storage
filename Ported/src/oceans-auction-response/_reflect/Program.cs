using System;
using System.Linq;
using System.Reflection;

static class Probe
{
    static void Dump(Type t, bool fields = true)
    {
        if (t == null) { Console.WriteLine("  <TYPE NOT FOUND>"); return; }
        Console.WriteLine("=== " + t.FullName + "  [" + t.Assembly.GetName().Name + "]");
        if (t.IsEnum)
        {
            foreach (var n in Enum.GetNames(t)) Console.WriteLine("   enum " + n + " = " + Convert.ToInt64(Enum.Parse(t, n)));
            return;
        }
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(x => x.Name))
            Console.WriteLine("   prop " + p.PropertyType.Name + " " + p.Name + (p.CanWrite ? " {g;s}" : " {g}"));
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                           .Where(x => !x.IsSpecialName).OrderBy(x => x.Name))
            Console.WriteLine("   " + (m.IsFamily ? "prot " : m.IsPublic ? "pub  " : "priv ") + (m.IsVirtual ? "virt " : "     ")
                + m.ReturnType.Name + " " + m.Name + "(" + string.Join(", ", m.GetParameters().Select(q => q.ParameterType.Name + " " + q.Name)) + ")");
        foreach (var e in t.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Console.WriteLine("   event " + e.EventHandlerType.Name + " " + e.Name);
    }

    const string AtasDir = @"C:\Program Files (x86)\ATAS Platform";

    static Type F(string name)
    {
        foreach (var an in new[] { "ATAS.Indicators", "ATAS.DataFeedsCore", "ATAS.Types", "OFT.Core", "OFT.Rendering", "Utils.Common" })
        {
            try
            {
                var asm = Assembly.LoadFrom(System.IO.Path.Combine(AtasDir, an + ".dll"));
                var t = asm.GetTypes().FirstOrDefault(x => x.FullName == name || x.Name == name);
                if (t != null) return t;
            }
            catch (ReflectionTypeLoadException ex) { var t = ex.Types.Where(x => x != null).FirstOrDefault(x => x.FullName == name || x.Name == name); if (t != null) return t; }
            catch { }
        }
        return null;
    }

    static void Main()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var simple = new AssemblyName(e.Name).Name;
            var path = System.IO.Path.Combine(AtasDir, simple + ".dll");
            return System.IO.File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        foreach (var n in new[] { "IDrawingObjectsListInfo", "IChartDrawingObject", "DrawingObject", "LineTillTouch", "HorizontalLine", "IChart" })
            Dump(F(n));
        if (true) return;

        var ind = F("ATAS.Indicators.Indicator");
        Console.WriteLine();
        Console.WriteLine("########## Indicator hierarchy");
        for (var t = ind; t != null && t != typeof(object); t = t.BaseType) Console.WriteLine("   " + t.FullName);
        Console.WriteLine();
        foreach (var t in new[] { ind, ind?.BaseType, ind?.BaseType?.BaseType, ind?.BaseType?.BaseType?.BaseType })
            if (t != null && t != typeof(object)) Dump(t);
    }
}
