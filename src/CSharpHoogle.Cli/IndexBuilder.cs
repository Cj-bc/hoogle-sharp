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

        if (ctx is not null)
        {
            progress?.Invoke("Walking source files...");
            var sourceMethods = new List<CachedMethod>();
            foreach (var csproj in ProjectContextDetector.EnumerateCsprojs(ctx.OriginPath))
            {
                var asmName = ReadAssemblyName(csproj)
                    ?? Path.GetFileNameWithoutExtension(csproj);
                var fromCsproj = SourceIndexBuilder.BuildFromCsproj(csproj, asmName, progress);
                sourceMethods.AddRange(fromCsproj);
            }

            // Source entries are authoritative for live edits — when their
            // (FullName, normalized params) matches an already-indexed assembly
            // entry, drop the assembly one. The normalizer folds C# keyword
            // aliases (int ↔ Int32) so the source string and the metadata
            // type name compare equal.
            if (sourceMethods.Count > 0)
            {
                var sourceKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var m in sourceMethods)
                {
                    sourceKeys.Add(MethodKey(m));
                }
                var deduped = new List<CachedMethod>(all.Count);
                var dropped = 0;
                foreach (var m in all)
                {
                    if (sourceKeys.Contains(MethodKey(m)))
                    {
                        dropped++;
                        continue;
                    }
                    deduped.Add(m);
                }
                all = deduped;
                all.AddRange(sourceMethods);
                progress?.Invoke($"  {sourceMethods.Count:N0} source methods indexed; {dropped:N0} assembly entries shadowed");
            }
        }

        return all;
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
            sb.Append(KeywordAlias(t) ?? t);
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

    private static string? KeywordAlias(string token) => token switch
    {
        "bool" => "Boolean",
        "byte" => "Byte",
        "sbyte" => "SByte",
        "char" => "Char",
        "decimal" => "Decimal",
        "double" => "Double",
        "float" => "Single",
        "int" => "Int32",
        "uint" => "UInt32",
        "long" => "Int64",
        "ulong" => "UInt64",
        "short" => "Int16",
        "ushort" => "UInt16",
        "object" => "Object",
        "string" => "String",
        "void" => "Void",
        "nint" => "IntPtr",
        "nuint" => "UIntPtr",
        _ => null,
    };

    /// <summary>
    /// Returns every .cs file that the source pass would index for the given
    /// context. Program.cs feeds these into <see cref="CacheStore.TryLoad"/>'s
    /// manifest list so an edited file invalidates the cache automatically —
    /// the CLI doesn't have to be re-invoked with <c>--rebuild</c>.
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
