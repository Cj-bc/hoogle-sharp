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
    /// Builds the full BCL index: parses every XML doc file in the reference
    /// pack, walks every managed DLL in the shared runtime directory, and
    /// returns a projected list suitable for caching and substring search.
    /// </summary>
    public static IReadOnlyList<CachedMethod> BuildBclIndex(Action<string>? progress = null)
    {
        progress?.Invoke("Parsing BCL XML docs...");
        var docs = new InMemoryDocEntryRepository();
        var docDir = BclIndexBuilder.ResolveDefaultDocDir();
        var docIndex = BclIndexBuilder.BuildFromRuntime(docDir);
        docs.Store(docIndex.Values);
        progress?.Invoke($"  {docIndex.Count:N0} doc entries loaded from {docDir}");

        progress?.Invoke("Walking BCL assemblies...");
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;

        using var loader = new MetadataLoader();
        var all = new List<CachedMethod>();
        var assemblyCount = 0;

        foreach (var dll in Directory.EnumerateFiles(runtimeDir, "*.dll"))
        {
            if (!MetadataLoader.IsManagedAssembly(dll))
            {
                continue;
            }

            try
            {
                var asm = loader.LoadFromAssemblyPath(dll);
                var methods = MethodIndexBuilder.BuildFromAssembly(asm, docs);
                foreach (var m in methods)
                {
                    all.Add(Project(m));
                }
                assemblyCount++;
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException)
            {
                // Skip assemblies the loader can't read (native mixed-mode, etc.).
            }
        }

        progress?.Invoke($"  {all.Count:N0} methods from {assemblyCount} assemblies");
        return all;
    }

    private static CachedMethod Project(MethodEntry m) => new(
        FullName: m.FullName,
        ReturnType: TypeNameFormatter.Format(m.ReturnType),
        ParameterTypes: Array.ConvertAll(m.ParameterTypes, TypeNameFormatter.Format),
        GenericParams: m.GenericParams,
        IsExtensionMethod: m.IsExtensionMethod,
        DocUrl: m.DocUrl,
        Summary: m.Doc?.Summary);
}
