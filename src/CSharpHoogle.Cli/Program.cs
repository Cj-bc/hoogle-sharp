using System.Diagnostics;

namespace CSharpHoogle.Cli;

/// <summary>
/// Entry point for the <c>csharp-hoogle</c> CLI.
/// Usage:
///   csharp-hoogle &lt;query&gt;             substring match on FullName
///   csharp-hoogle --rebuild &lt;query&gt;   force a cache rebuild, then search
///   csharp-hoogle --rebuild             rebuild cache, print nothing else
///   csharp-hoogle --help                show usage
/// </summary>
public static class Program
{
    private const int MaxResults = 50;

    public static int Main(string[] args)
    {
        var rebuild = false;
        var showHelp = false;
        string? query = null;

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                case "--rebuild":
                    rebuild = true;
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        Console.Error.WriteLine($"Unknown option: {arg}");
                        return 2;
                    }
                    if (query is not null)
                    {
                        Console.Error.WriteLine("Only one query argument is supported.");
                        return 2;
                    }
                    query = arg;
                    break;
            }
        }

        if (showHelp)
        {
            PrintUsage();
            return 0;
        }

        IReadOnlyList<CachedMethod> methods;
        if (rebuild || !CacheStore.TryLoad(out methods))
        {
            methods = RebuildAndSave();
        }

        if (query is null)
        {
            if (!rebuild)
            {
                PrintUsage();
                return 1;
            }
            return 0;
        }

        var matches = methods
            .Where(m => m.FullName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(MaxResults)
            .ToList();

        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"No matches for \"{query}\".");
            return 1;
        }

        foreach (var m in matches)
        {
            PrintMatch(m);
        }

        return 0;
    }

    private static IReadOnlyList<CachedMethod> RebuildAndSave()
    {
        var sw = Stopwatch.StartNew();
        var methods = IndexBuilder.BuildBclIndex(msg => Console.Error.WriteLine(msg));
        CacheStore.Save(methods);
        sw.Stop();
        Console.Error.WriteLine($"Cached {methods.Count:N0} methods in {sw.Elapsed.TotalSeconds:F1}s → {CacheStore.GetCachePath()}");
        return methods;
    }

    private static void PrintMatch(CachedMethod m)
    {
        var paramList = string.Join(", ", m.ParameterTypes);
        var generics = m.GenericParams.Length > 0 ? $"<{string.Join(", ", m.GenericParams)}>" : "";
        var prefix = m.IsExtensionMethod ? "(ext) " : "";
        Console.WriteLine($"{prefix}{m.FullName}{generics}({paramList}) : {m.ReturnType}");
        if (!string.IsNullOrWhiteSpace(m.Summary))
        {
            Console.WriteLine($"    {m.Summary}");
        }
        if (!string.IsNullOrEmpty(m.DocUrl))
        {
            Console.WriteLine($"    {m.DocUrl}");
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: csharp-hoogle [--rebuild] <query>");
        Console.WriteLine();
        Console.WriteLine("  <query>     Substring matched against the method's full name");
        Console.WriteLine("              (case-insensitive).");
        Console.WriteLine("  --rebuild   Rebuild the on-disk cache before searching.");
        Console.WriteLine("  --help      Show this message.");
    }
}
