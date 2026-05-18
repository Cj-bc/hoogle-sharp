using CSharpHoogle.Core.Indexing;
using CSharpHoogle.Core.Models;
using CSharpHoogle.Core.Parsing;
using CSharpHoogle.Core.Reflection;
using CSharpHoogle.Core.Storage;

namespace CSharpHoogle.Cli;

/// <summary>
/// Builds the CLI's flat <see cref="CachedMethod"/> list. Split into three
/// independently-callable passes so the cache can invalidate one bucket
/// (BCL / deps / source) without forcing a full rebuild of the others.
/// </summary>
public static class IndexBuilder
{
    /// <summary>
    /// Walks the BCL/reference-pack assemblies for <paramref name="ctx"/>, or the
    /// running runtime when no context is given. Returns one tagged
    /// <see cref="CachedMethod"/> per public method discovered. No dep or source
    /// passes — those live in <see cref="BuildDepMethods"/> /
    /// <see cref="BuildSourceMethods"/>.
    /// </summary>
    public static IReadOnlyList<CachedMethod> BuildBclMethods(
        ProjectContext? ctx,
        Action<string>? progress = null)
    {
        progress?.Invoke("Parsing BCL XML docs...");

        var docs = new InMemoryDocEntryRepository();
        var (docDirs, walkDirs, fromRefPacks) = ResolveBclDirs(ctx);
        if (ctx is not null
            && ProjectContextDetector.TryParseNetTfm(ctx.Tfm, out _, out _)
            && !fromRefPacks)
        {
            progress?.Invoke($"  no reference pack found for {ctx.Tfm} ({ctx.Sdk}); falling back to running runtime");
        }

        var docIndex = BclIndexBuilder.BuildFromRuntime(docDirs);
        docs.Store(docIndex.Values);
        progress?.Invoke($"  {docIndex.Count:N0} doc entries loaded from {string.Join(", ", docDirs)}");

        progress?.Invoke("Walking BCL assemblies...");
        using var loader = fromRefPacks
            ? new MetadataLoader(walkDirs, includeRuntimeDir: false)
            : new MetadataLoader(walkDirs);

        var all = new List<CachedMethod>();
        var assemblyCount = 0;

        foreach (var dir in walkDirs)
        {
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
            {
                if (!MetadataLoader.IsManagedAssembly(dll))
                {
                    continue;
                }

                try
                {
                    var asm = loader.LoadFromAssemblyPath(dll);
                    var asmName = asm.GetName().Name ?? Path.GetFileNameWithoutExtension(dll);
                    var source = new MethodSource("assembly", asmName);
                    var methods = MethodIndexBuilder.BuildFromAssembly(asm, docs);
                    foreach (var m in methods)
                    {
                        all.Add(Project(m, source));
                    }
                    assemblyCount++;
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
                {
                    // Skip assemblies the loader can't read (native mixed-mode, etc.).
                }
            }
        }

        progress?.Invoke($"  {all.Count:N0} methods from {assemblyCount} assemblies");
        return all;
    }

    /// <summary>
    /// Walks every dep assembly in <paramref name="deps"/> using a
    /// <see cref="MetadataLoader"/> seeded with both BCL search dirs and dep
    /// dirs (so cross-package references resolve). Returns the methods tagged
    /// with the dep's <see cref="MethodSource"/>.
    /// </summary>
    public static IReadOnlyList<CachedMethod> BuildDepMethods(
        ProjectContext? ctx,
        ProjectDependencies deps,
        Action<string>? progress = null)
    {
        if (deps.Entries.Count == 0)
        {
            return Array.Empty<CachedMethod>();
        }

        var docs = new InMemoryDocEntryRepository();
        foreach (var entry in deps.Entries)
        {
            try
            {
                var docMap = entry.XmlPath is not null
                    ? XmlDocParser.Parse(entry.XmlPath)
                    : XmlDocParser.ParseForAssembly(entry.AssemblyPath);
                docs.Store(docMap.Values);
            }
            catch (Exception ex) when (ex is FileNotFoundException
                                        or InvalidDataException
                                        or System.Xml.XmlException)
            {
                // Reflection can still produce signatures without the docs.
            }
        }

        var (_, walkDirs, fromRefPacks) = ResolveBclDirs(ctx);

        var depDirs = deps.Entries
            .Select(e => Path.GetDirectoryName(e.AssemblyPath))
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var loaderDirs = walkDirs.Concat(depDirs).ToList();

        progress?.Invoke($"Walking {deps.Entries.Count} dependency assemblies...");
        using var loader = fromRefPacks
            ? new MetadataLoader(loaderDirs, includeRuntimeDir: false)
            : new MetadataLoader(loaderDirs);

        var all = new List<CachedMethod>();
        var depMethodCount = 0;
        var depAssemblyCount = 0;

        foreach (var entry in deps.Entries)
        {
            if (!MetadataLoader.IsManagedAssembly(entry.AssemblyPath))
            {
                continue;
            }

            try
            {
                var asm = loader.LoadFromAssemblyPath(entry.AssemblyPath);
                var source = new MethodSource(entry.SourceKind, entry.SourceName);
                var methods = MethodIndexBuilder.BuildFromAssembly(asm, docs);
                foreach (var m in methods)
                {
                    all.Add(Project(m, source));
                    depMethodCount++;
                }
                depAssemblyCount++;
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
            {
                // Skip — same policy as BCL walk.
            }
        }

        progress?.Invoke($"  {depMethodCount:N0} methods from {depAssemblyCount} dependency assemblies");
        return all;
    }

    /// <summary>
    /// Walks .cs files for the projects belonging to <paramref name="ctx"/> and
    /// returns the methods discovered. Each entry is tagged with
    /// <see cref="MethodSource"/> kind <c>"source"</c> so
    /// <see cref="Dedupe"/> can shadow assembly entries with the same signature.
    /// </summary>
    public static IReadOnlyList<CachedMethod> BuildSourceMethods(
        ProjectContext ctx,
        Action<string>? progress = null)
    {
        progress?.Invoke("Walking source files...");
        var all = new List<CachedMethod>();
        foreach (var csproj in ProjectContextDetector.EnumerateCsprojs(ctx.OriginPath))
        {
            var asmName = ReadAssemblyName(csproj)
                ?? Path.GetFileNameWithoutExtension(csproj);
            var fromCsproj = SourceIndexBuilder.BuildFromCsproj(csproj, asmName, progress);
            all.AddRange(fromCsproj);
        }
        return all;
    }

    /// <summary>
    /// Merges assembly-side and source-side methods, dropping any assembly
    /// entry whose <c>(FullName, normalized params)</c> matches a source entry.
    /// The normalizer folds C# keyword aliases (<c>int</c> ↔ <c>Int32</c>) so
    /// the source string and metadata type name compare equal.
    /// </summary>
    public static IReadOnlyList<CachedMethod> Dedupe(
        IReadOnlyList<CachedMethod> assemblyMethods,
        IReadOnlyList<CachedMethod> sourceMethods,
        Action<string>? progress = null)
    {
        if (sourceMethods.Count == 0)
        {
            return assemblyMethods;
        }

        var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in sourceMethods)
        {
            sourceKeys.Add(MethodKey(m));
        }

        var deduped = new List<CachedMethod>(assemblyMethods.Count + sourceMethods.Count);
        var dropped = 0;
        foreach (var m in assemblyMethods)
        {
            if (sourceKeys.Contains(MethodKey(m)))
            {
                dropped++;
                continue;
            }
            deduped.Add(m);
        }
        deduped.AddRange(sourceMethods);

        progress?.Invoke($"  {sourceMethods.Count:N0} source methods indexed; {dropped:N0} assembly entries shadowed");
        return deduped;
    }

    /// <summary>
    /// Convenience wrapper that runs all three passes back-to-back. Used by
    /// tests and as the non-cached entry point; the cached-CLI flow calls the
    /// per-bucket methods directly.
    /// </summary>
    public static IReadOnlyList<CachedMethod> BuildIndex(
        ProjectContext? ctx,
        ProjectDependencies? deps,
        Action<string>? progress = null)
    {
        var bcl = BuildBclMethods(ctx, progress);
        var depMethods = deps is null
            ? Array.Empty<CachedMethod>()
            : BuildDepMethods(ctx, deps, progress);
        var sourceMethods = ctx is null
            ? Array.Empty<CachedMethod>()
            : BuildSourceMethods(ctx, progress);

        var assemblyMethods = new List<CachedMethod>(bcl.Count + depMethods.Count);
        assemblyMethods.AddRange(bcl);
        assemblyMethods.AddRange(depMethods);

        return Dedupe(assemblyMethods, sourceMethods, progress);
    }

    /// <summary>
    /// Returns the doc-search directories, the walk directories (where BCL
    /// DLLs live), and whether we resolved against an installed reference
    /// pack. Both <see cref="BuildBclMethods"/> and <see cref="BuildDepMethods"/>
    /// rely on this to seed their <see cref="MetadataLoader"/>.
    /// </summary>
    private static (IReadOnlyList<string> DocDirs, IReadOnlyList<string> WalkDirs, bool FromRefPacks)
        ResolveBclDirs(ProjectContext? ctx)
    {
        IReadOnlyList<string> refPackDirs = Array.Empty<string>();
        if (ctx is not null
            && ProjectContextDetector.TryParseNetTfm(ctx.Tfm, out var maj, out var min))
        {
            refPackDirs = BclIndexBuilder.ResolveDocDirs(maj, min, PacksFor(ctx.Sdk));
        }

        if (refPackDirs.Count > 0)
        {
            return (refPackDirs, refPackDirs, true);
        }

        var defaultDocDir = BclIndexBuilder.ResolveDefaultDocDir();
        var runtimeWalkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        return (new[] { defaultDocDir }, new[] { runtimeWalkDir }, false);
    }

    private static string MethodKey(CachedMethod m)
    {
        var sb = new System.Text.StringBuilder(m.FullName.Length + 32);
        sb.Append(m.FullName);
        sb.Append('(');
        for (var i = 0; i < m.ParameterTypes.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(NormalizeTypeAlias(m.ParameterTypes[i]));
        }
        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// Folds C# keyword aliases (<c>int</c>, <c>string</c>, ...) into their
    /// canonical BCL names and drops nullable-annotation <c>?</c> markers so a
    /// source-side TypeSyntax string and a metadata-side <see cref="Type.Name"/>
    /// compare equal during dedupe. Operates token-by-token so generic argument
    /// lists like <c>IEnumerable&lt;int?&gt;</c> normalize too.
    /// </summary>
    private static string NormalizeTypeAlias(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return raw;
        }

        var sb = new System.Text.StringBuilder(raw.Length);
        var token = new System.Text.StringBuilder();

        void FlushToken()
        {
            if (token.Length == 0) return;
            var t = token.ToString();
            sb.Append(CSharpKeywordAliases.TryGetCanonical(t, out var canonical) ? canonical : t);
            token.Clear();
        }

        foreach (var ch in raw)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                token.Append(ch);
            }
            else if (ch == '?')
            {
                // Nullable annotations don't change CLR type identity. Reflection
                // never surfaces them on reference types and renders Nullable<T>
                // as a generic, so the source-side `?` has no peer to compare
                // against — drop it for dedupe purposes.
                FlushToken();
            }
            else
            {
                FlushToken();
                sb.Append(ch);
            }
        }
        FlushToken();
        return sb.ToString();
    }

    /// <summary>
    /// Returns every .cs file the source pass would index for the given
    /// context. Program.cs feeds these into <see cref="CacheStore.TryLoadSource"/>'s
    /// invalidation list so an edited file invalidates only the source bucket
    /// — the CLI doesn't have to be re-invoked with <c>--rebuild</c>.
    /// </summary>
    public static IReadOnlyList<string> EnumerateSourceFiles(ProjectContext ctx)
    {
        var files = new List<string>();
        foreach (var csproj in ProjectContextDetector.EnumerateCsprojs(ctx.OriginPath))
        {
            files.AddRange(CompileItemEnumerator.Enumerate(csproj));
        }
        return files;
    }

    private static string? ReadAssemblyName(string csprojPath)
    {
        if (!File.Exists(csprojPath))
        {
            return null;
        }
        try
        {
            var doc = System.Xml.Linq.XDocument.Load(csprojPath);
            return doc.Descendants("AssemblyName")
                .Select(e => e.Value.Trim())
                .FirstOrDefault(s => !string.IsNullOrEmpty(s));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> PacksFor(SdkKind sdk) => sdk switch
    {
        SdkKind.Web => new[] { "Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref" },
        SdkKind.Razor => new[] { "Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref" },
        SdkKind.Worker => new[] { "Microsoft.NETCore.App.Ref", "Microsoft.AspNetCore.App.Ref" },
        _ => new[] { "Microsoft.NETCore.App.Ref" },
    };

    private static CachedMethod Project(MethodEntry m, MethodSource source) => new(
        FullName: m.FullName,
        ReturnType: TypeNameFormatter.Format(m.ReturnType),
        ParameterTypes: Array.ConvertAll(m.ParameterTypes, TypeNameFormatter.Format),
        GenericParams: m.GenericParams,
        IsExtensionMethod: m.IsExtensionMethod,
        DocUrl: m.DocUrl,
        Summary: m.Doc?.Summary,
        Source: source,
        RequiredParameterCount: m.RequiredParameterCount,
        DeclaringType: m.DeclaringType is null ? null : TypeNameFormatter.Format(m.DeclaringType),
        TypeGenericParams: m.TypeGenericParams ?? Array.Empty<string>());
}
