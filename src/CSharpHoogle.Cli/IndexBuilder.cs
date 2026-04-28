using CSharpHoogle.Core.Indexing;
using CSharpHoogle.Core.Models;
using CSharpHoogle.Core.Parsing;
using CSharpHoogle.Core.Reflection;
using CSharpHoogle.Core.Storage;

namespace CSharpHoogle.Cli;

/// <summary>
/// Composes Phase 1 (DocEntry index) and Phase 2 (MethodEntry walk) and
/// projects the result into the CLI's flat <see cref="CachedMethod"/> shape.
/// </summary>
public static class IndexBuilder
{
    /// <summary>
    /// Builds the BCL-only index. Kept for callers and tests that don't supply
    /// a dependency set; routes through <see cref="BuildIndex"/>.
    /// </summary>
    public static IReadOnlyList<CachedMethod> BuildBclIndex(
        ProjectContext? ctx = null,
        Action<string>? progress = null)
        => BuildIndex(ctx, deps: null, progress);

    /// <summary>
    /// Builds the BCL index and, when <paramref name="deps"/> is non-null,
    /// walks each dependency assembly into the same index — tagged with
    /// the dep's <see cref="MethodSource"/>. The MetadataLoader is seeded
    /// with both BCL and dep directories so cross-package references resolve.
    /// </summary>
    public static IReadOnlyList<CachedMethod> BuildIndex(
        ProjectContext? ctx,
        ProjectDependencies? deps,
        Action<string>? progress = null)
    {
        progress?.Invoke("Parsing BCL XML docs...");

        var docs = new InMemoryDocEntryRepository();
        IReadOnlyList<string> refPackDirs = Array.Empty<string>();

        if (ctx is not null
            && ProjectContextDetector.TryParseNetTfm(ctx.Tfm, out var maj, out var min))
        {
            refPackDirs = BclIndexBuilder.ResolveDocDirs(maj, min, PacksFor(ctx.Sdk));
            if (refPackDirs.Count == 0)
            {
                progress?.Invoke($"  no reference pack found for {ctx.Tfm} ({ctx.Sdk}); falling back to running runtime");
            }
        }

        // Two distinct modes:
        //   ref-pack mode: walk reference DLLs in resolved pack dirs; loader is seeded
        //     ONLY with those dirs (runtime dir would duplicate mscorlib and crash
        //     MetadataLoadContext). XML docs come from the same dirs.
        //   legacy mode: walk the running runtime dir (where mscorlib actually lives);
        //     loader auto-seeds the runtime; XML docs come from ResolveDefaultDocDir()
        //     (typically the matching ref pack on a normal .NET install).
        IReadOnlyList<string> docDirs;
        IReadOnlyList<string> walkDirs;
        bool fromRefPacks = refPackDirs.Count > 0;

        if (fromRefPacks)
        {
            docDirs = refPackDirs;
            walkDirs = refPackDirs;
        }
        else
        {
            var defaultDocDir = BclIndexBuilder.ResolveDefaultDocDir();
            docDirs = new[] { defaultDocDir };
            walkDirs = new[] { Path.GetDirectoryName(typeof(object).Assembly.Location)! };
        }

        var docIndex = BclIndexBuilder.BuildFromRuntime(docDirs);
        docs.Store(docIndex.Values);
        progress?.Invoke($"  {docIndex.Count:N0} doc entries loaded from {string.Join(", ", docDirs)}");

        // Add dep doc entries to the shared repo so MethodIndexBuilder can resolve
        // them while reflecting dep assemblies.
        if (deps is not null && deps.Entries.Count > 0)
        {
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
        }

        // Loader covers BCL search dirs + every distinct directory that contains a
        // dep dll. This lets references between packages (and from packages back
        // into BCL) resolve while we reflect.
        var depDirs = (deps?.Entries ?? Array.Empty<DependencyEntry>())
            .Select(e => Path.GetDirectoryName(e.AssemblyPath))
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var loaderDirs = walkDirs.Concat(depDirs).ToList();

        progress?.Invoke("Walking BCL assemblies...");
        using var loader = fromRefPacks
            ? new MetadataLoader(loaderDirs, includeRuntimeDir: false)
            : new MetadataLoader(loaderDirs);
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

        if (deps is not null && deps.Entries.Count > 0)
        {
            progress?.Invoke($"Walking {deps.Entries.Count} dependency assemblies...");
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
        }

        return all;
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
        Source: source);
}
