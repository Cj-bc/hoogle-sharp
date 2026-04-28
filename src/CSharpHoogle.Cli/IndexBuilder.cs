using CSharpHoogle.Core.Indexing;
using CSharpHoogle.Core.Models;
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
    /// Builds the full BCL index. With no <see cref="ProjectContext"/>, walks
    /// the running runtime's directory (legacy behavior). With a context, picks
    /// reference packs that match the project's TFM and SDK kind so the index
    /// matches what the user is actually compiling against.
    /// </summary>
    public static IReadOnlyList<CachedMethod> BuildBclIndex(
        ProjectContext? ctx = null,
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

        progress?.Invoke("Walking BCL assemblies...");
        using var loader = fromRefPacks
            ? new MetadataLoader(walkDirs, includeRuntimeDir: false)
            : new MetadataLoader();
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
