using System.Diagnostics;
using System.Text.Json;

namespace CSharpHoogle.Cli;

/// <summary>
/// Entry point for the <c>csharp-hoogle</c> CLI.
/// Usage:
///   csharp-hoogle &lt;query&gt;             substring match on FullName, or
///                                       signature match if the query contains "->"
///   csharp-hoogle --rebuild &lt;query&gt;   force a cache rebuild, then search
///   csharp-hoogle --rebuild             rebuild cache, print nothing else
///   csharp-hoogle --list-assemblies     list assemblies in the cache with method counts
///   csharp-hoogle --json                emit results as JSON for piping into other tools
///   csharp-hoogle --project &lt;path&gt;   pin context to a specific sln/slnx/csproj
///   csharp-hoogle --target-framework &lt;tfm&gt; / -f &lt;tfm&gt;
///                                       pin TFM (overrides on-disk detection)
///   csharp-hoogle --help                show usage
/// </summary>
public static class Program
{
    private const int MaxResults = 50;

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Honor NO_COLOR (https://no-color.org) and skip ANSI when stdout is piped.
    private static readonly bool UseColor =
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
        && !Console.IsOutputRedirected;

    private const string AnsiReset = "\x1b[0m";
    private const string AnsiBold = "\x1b[1m";
    private const string AnsiDim = "\x1b[2m";
    private const string AnsiCyan = "\x1b[36m";
    private const string AnsiYellow = "\x1b[33m";
    private const string AnsiMagenta = "\x1b[35m";

    private static string Color(string text, string ansi) =>
        UseColor ? $"{ansi}{text}{AnsiReset}" : text;

    public static int Main(string[] args)
    {
        var rebuild = false;
        var showHelp = false;
        var listAssemblies = false;
        var json = false;
        string? projectOverride = null;
        string? tfmOverride = null;
        var queryParts = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                    showHelp = true;
                    break;
                case "--rebuild":
                    rebuild = true;
                    break;
                case "--list-assemblies":
                    listAssemblies = true;
                    break;
                case "--json":
                    json = true;
                    break;
                case "--project":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("--project requires a path argument.");
                        return 2;
                    }
                    projectOverride = args[i];
                    break;
                case "--target-framework":
                case "-f":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine($"{arg} requires a TFM argument (e.g. net8.0).");
                        return 2;
                    }
                    tfmOverride = args[i];
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"Unknown option: {arg}");
                        return 2;
                    }
                    queryParts.Add(arg);
                    break;
            }
        }

        if (showHelp)
        {
            PrintUsage();
            return 0;
        }

        var ctx = ProjectContextDetector.Detect(Environment.CurrentDirectory, projectOverride, tfmOverride);

        // Resolve deps once up-front so manifest mtimes can drive cache invalidation
        // and the same dep set feeds RebuildAndSave on a miss. Capture progress into
        // a buffer so cache hits stay quiet; flush only on a rebuild.
        var depMessages = new List<string>();
        ProjectDependencies? deps = ctx is null
            ? null
            : ProjectDependencyResolver.Resolve(ctx, msg => depMessages.Add(msg));
        var manifestPaths = deps?.ManifestPaths ?? (IReadOnlyList<string>)Array.Empty<string>();

        IReadOnlyList<CachedMethod> methods;
        if (rebuild || !CacheStore.TryLoad(ctx, manifestPaths, out methods))
        {
            foreach (var m in depMessages)
            {
                Console.Error.WriteLine(m);
            }
            methods = RebuildAndSave(ctx, deps);
        }

        if (listAssemblies)
        {
            if (json)
            {
                PrintAssemblyListJson(methods);
            }
            else
            {
                PrintAssemblyList(methods);
            }
            return 0;
        }

        if (queryParts.Count == 0)
        {
            if (!rebuild)
            {
                PrintUsage();
                return 1;
            }
            return 0;
        }

        // Join multi-token queries so unquoted input like `A -> B` works.
        var query = string.Join(' ', queryParts);
        var matches = TypeQuery.LooksLikeSignatureQuery(query)
            ? RunSignatureSearch(query, methods)
            : RunSubstringSearch(query, methods);

        if (matches is null)
        {
            return 2;
        }

        if (matches.Count == 0)
        {
            Console.Error.WriteLine($"No matches for \"{query}\".");
            if (json)
            {
                PrintMatchesJson(matches);
            }
            return 1;
        }

        if (json)
        {
            PrintMatchesJson(matches);
        }
        else
        {
            foreach (var m in matches)
            {
                PrintMatch(m);
            }
        }

        return 0;
    }

    private static IReadOnlyList<CachedMethod> RunSubstringSearch(string query, IReadOnlyList<CachedMethod> methods)
        => methods
            .Where(m => m.FullName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(MaxResults)
            .ToList();

    private static IReadOnlyList<CachedMethod>? RunSignatureSearch(string query, IReadOnlyList<CachedMethod> methods)
    {
        SignatureQuery sig;
        try
        {
            sig = TypeQuery.ParseSignature(query);
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"Invalid signature query: {ex.Message}");
            return null;
        }

        return methods
            .Where(m => TypeQueryMatcher.Matches(sig, m))
            .Take(MaxResults)
            .ToList();
    }

    private static IReadOnlyList<CachedMethod> RebuildAndSave(ProjectContext? ctx, ProjectDependencies? deps)
    {
        if (ctx is not null)
        {
            Console.Error.WriteLine($"Detected project: {ctx.Tfm} ({ctx.Sdk}) from {ctx.OriginPath}");
        }
        var sw = Stopwatch.StartNew();
        var methods = IndexBuilder.BuildIndex(ctx, deps, msg => Console.Error.WriteLine(msg));
        CacheStore.Save(ctx, methods);
        sw.Stop();
        Console.Error.WriteLine($"Cached {methods.Count:N0} methods in {sw.Elapsed.TotalSeconds:F1}s → {CacheStore.GetCachePath(ctx)}");
        return methods;
    }

    private static void PrintMatch(CachedMethod m)
    {
        var paramList = string.Join(", ", m.ParameterTypes);
        var generics = m.GenericParams.Length > 0 ? $"<{string.Join(", ", m.GenericParams)}>" : "";
        var prefix = m.IsExtensionMethod ? Color("(ext) ", AnsiMagenta) : "";
        var name = Color($"{m.FullName}{generics}", AnsiBold + AnsiCyan);
        var sig = Color($"({paramList}) : {m.ReturnType}", AnsiYellow);
        Console.WriteLine($"{prefix}{name}{sig}");
        if (!string.IsNullOrWhiteSpace(m.Summary))
        {
            Console.WriteLine(Color($"    {m.Summary}", AnsiDim));
        }
        if (!string.IsNullOrEmpty(m.DocUrl))
        {
            Console.WriteLine(Color($"    {m.DocUrl}", AnsiDim));
        }
    }

    private static void PrintAssemblyList(IReadOnlyList<CachedMethod> methods)
    {
        // Group by the full Source (Kind + Name) so a future source-file
        // indexer shows up in the same listing without extra plumbing.
        var groups = methods
            .GroupBy(m => m.Source)
            .OrderBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Pad names so counts line up, but clamp to avoid pathological widths.
        var nameWidth = Math.Min(60, groups.Max(g => g.Key.Name.Length));

        foreach (var g in groups)
        {
            Console.WriteLine($"{g.Key.Name.PadRight(nameWidth)}  {g.Count(),7:N0} methods");
        }
        Console.WriteLine();
        Console.WriteLine($"Total: {groups.Count:N0} sources, {methods.Count:N0} methods");
    }

    private static void PrintMatchesJson(IReadOnlyList<CachedMethod> matches)
    {
        var projected = matches.Select(m => new
        {
            fullName = m.FullName,
            returnType = m.ReturnType,
            parameterTypes = m.ParameterTypes,
            genericParams = m.GenericParams,
            isExtensionMethod = m.IsExtensionMethod,
            summary = m.Summary,
            docUrl = m.DocUrl,
            source = m.Source.Name,
        });
        Console.WriteLine(JsonSerializer.Serialize(projected, JsonOut));
    }

    private static void PrintAssemblyListJson(IReadOnlyList<CachedMethod> methods)
    {
        var groups = methods
            .GroupBy(m => m.Source)
            .OrderBy(g => g.Key.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                name = g.Key.Name,
                kind = g.Key.Kind,
                methodCount = g.Count(),
            });
        Console.WriteLine(JsonSerializer.Serialize(groups, JsonOut));
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: csharp-hoogle [options] <query>");
        Console.WriteLine();
        Console.WriteLine("  <query>                       If it contains \"->\", matched as a type signature");
        Console.WriteLine("                                (e.g. `IEnumerable<T> -> Func<T,bool> -> IEnumerable<T>`).");
        Console.WriteLine("                                Otherwise, case-insensitive substring on the full name.");
        Console.WriteLine("  --rebuild                     Rebuild the on-disk cache before searching.");
        Console.WriteLine("  --list-assemblies             List assemblies in the cache with method counts.");
        Console.WriteLine("  --json                        Emit results as JSON on stdout (for piping into tools).");
        Console.WriteLine("  --project <path>              Pin context to a specific .sln/.slnx/.csproj file.");
        Console.WriteLine("  --target-framework <tfm>, -f  Pin TFM (e.g. net8.0); overrides on-disk detection.");
        Console.WriteLine("  --help                        Show this message.");
        Console.WriteLine();
        Console.WriteLine("Without overrides, csharp-hoogle walks up from the current directory looking for");
        Console.WriteLine("a .sln/.slnx; if none, falls back to a .csproj at the current directory; if none,");
        Console.WriteLine("uses the running .NET runtime.");
    }
}
