using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

class Program
{
    static void Main(string[] args)
    {
        var atas = @"C:\Program Files (x86)\ATAS Platform";
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);

        var wpfRef = @"C:\Program Files\dotnet\packs\Microsoft.WindowsDesktop.App.Ref\10.0.0\ref\net10.0";

        var paths = new List<string>();
        paths.AddRange(Directory.GetFiles(atas, "*.dll"));
        paths.AddRange(Directory.GetFiles(runtime, "*.dll"));
        if (Directory.Exists(wpfRef))
        {
            // runtime dir wins on name collisions; only add what's missing
            var have = new HashSet<string>(paths.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            paths.AddRange(Directory.GetFiles(wpfRef, "*.dll").Where(f => !have.Contains(Path.GetFileName(f))));
        }

        var resolver = new PathAssemblyResolver(paths);
        using var mlc = new MetadataLoadContext(resolver);

        var asm = mlc.LoadFromAssemblyPath(Path.Combine(atas, "ATAS.Indicators.dll"));

        if (args.Length > 0)
        {
            foreach (var want in args)
            {
                foreach (var t in asm.GetTypes().Where(t => t.IsPublic && t.Name == want))
                {
                    Console.WriteLine("--- " + t.FullName + (t.IsEnum ? " (enum)" : "") + " ---");
                    if (t.IsEnum) { foreach (var f in t.GetFields().Where(f => f.IsStatic)) Console.WriteLine("    E  " + f.Name); }
                    else Dump(t);
                }
            }
            return;
        }

        // 1. Walk the Indicator base chain
        var ind = asm.GetTypes().FirstOrDefault(t => t.FullName == "ATAS.Indicators.Indicator");
        Console.WriteLine("###### BASE CHAIN ######");
        for (var t = ind; t != null && t.FullName != "System.Object"; t = t.BaseType)
            Console.WriteLine("  " + t.FullName + "   [asm: " + t.Assembly.GetName().Name + "]");

        // 2. Members of the chain that matter
        Console.WriteLine();
        Console.WriteLine("###### Indicator chain members ######");
        for (var t = ind; t != null && t.FullName != "System.Object"; t = t.BaseType)
        {
            Console.WriteLine("--- " + t.FullName + " ---");
            Dump(t);
        }

        // 3. Candle type
        Console.WriteLine();
        Console.WriteLine("###### Candle types ######");
        foreach (var t in asm.GetTypes().Where(t => t.IsPublic && t.Name.Contains("Candle")))
        {
            Console.WriteLine("--- " + t.FullName + " ---");
            Dump(t);
        }

        // 4. Anything session / instrument related
        Console.WriteLine();
        Console.WriteLine("###### Session/Instrument types ######");
        foreach (var t in asm.GetTypes().Where(t => t.IsPublic &&
                 (t.Name.Contains("Session") || t.Name.Contains("InstrumentInfo"))))
        {
            Console.WriteLine("--- " + t.FullName + " ---");
            Dump(t);
        }
    }

    static IEnumerable<T> Safe<T>(Func<T[]> f)
    {
        try { return f() ?? Array.Empty<T>(); }
        catch { return Array.Empty<T>(); }
    }

    static void Dump(Type t)
    {
        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic |
                               BindingFlags.Instance | BindingFlags.Static |
                               BindingFlags.DeclaredOnly;

        foreach (var p in Safe(() => t.GetProperties(F)))
        {
            try
            {
                var g = p.GetGetMethod(true);
                if (g != null && (g.IsPublic || g.IsFamily))
                    Console.WriteLine($"    P  {p.PropertyType.Name} {p.Name}");
            }
            catch { }
        }
        foreach (var m in Safe(() => t.GetMethods(F)))
        {
            try
            {
                if (m.IsSpecialName) continue;
                if (!(m.IsPublic || m.IsFamily)) continue;
                var ps = string.Join(", ", m.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name));
                var mod = m.IsVirtual ? (m.IsAbstract ? "abstract " : "virtual ") : "";
                Console.WriteLine($"    M  {mod}{m.ReturnType.Name} {m.Name}({ps})");
            }
            catch { }
        }
        foreach (var c in Safe(() => t.GetConstructors(F)))
        {
            try
            {
                if (!(c.IsPublic || c.IsFamily)) continue;
                var ps = string.Join(", ", c.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name));
                Console.WriteLine($"    C  .ctor({ps})");
            }
            catch { }
        }
    }
}
