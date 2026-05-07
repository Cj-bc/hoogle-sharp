using System.Reflection;
using System.Reflection.PortableExecutable;

namespace CSharpHoogle.Core.Reflection;

/// <summary>
/// Read-only assembly loader built on <see cref="MetadataLoadContext"/>.
/// Works across target frameworks — can inspect netstandard2.1 Unity DLLs or
/// user project assemblies from our net8.0 process without executing their code.
/// Seeds the resolver with the running runtime's shared directory so that
/// System.Private.CoreLib and the BCL are always resolvable.
/// </summary>
public sealed class MetadataLoader : IDisposable
{
    private readonly MetadataLoadContext _context;
    private readonly HashSet<string> _searchPaths;

    /// <summary>
    /// Creates a loader whose resolver covers the runtime directory plus any
    /// additional <paramref name="extraSearchDirs"/> (e.g. Unity's Managed/ folder,
    /// the user's project output directory). Every <c>*.dll</c> found in those
    /// directories is added to the resolver's path list.
    /// </summary>
    public MetadataLoader(IEnumerable<string>? extraSearchDirs = null)
        : this(extraSearchDirs, includeRuntimeDir: true)
    {
    }

    /// <summary>
    /// Creates a loader from an explicit set of search directories. When
    /// <paramref name="includeRuntimeDir"/> is false, the running runtime is NOT
    /// auto-added — required when the search dirs already contain a complete BCL
    /// (e.g. a reference pack), since two copies of <c>mscorlib</c> would collide
    /// inside <see cref="MetadataLoadContext"/>.
    /// </summary>
    public MetadataLoader(IEnumerable<string>? searchDirs, bool includeRuntimeDir)
    {
        _searchPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Dedup by filename (first wins) so that two copies of mscorlib.dll
        // — runtime/ref-pack vs. a Unity facade in a NuGet/HintPath dir —
        // don't both end up in the resolver. PathAssemblyResolver would try
        // to load both during MetadataLoadContext's core-assembly probe and
        // throw "already been loaded" on the second one. Runtime/ref-pack is
        // walked first so its copy wins.
        var seenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDll(string dll)
        {
            if (seenFileNames.Add(Path.GetFileName(dll)))
            {
                _searchPaths.Add(dll);
            }
        }

        if (includeRuntimeDir)
        {
            var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)
                ?? throw new InvalidOperationException(
                    "Could not determine runtime directory from typeof(object).Assembly.Location.");

            foreach (var dll in Directory.EnumerateFiles(runtimeDir, "*.dll"))
            {
                AddDll(dll);
            }
        }

        if (searchDirs is not null)
        {
            foreach (var dir in searchDirs)
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
                {
                    AddDll(dll);
                }
            }
        }

        _context = new MetadataLoadContext(new PathAssemblyResolver(_searchPaths));
    }

    /// <summary>
    /// Loads the assembly at <paramref name="path"/>. The path is also added to
    /// the resolver so subsequent reference lookups can find it.
    /// Returns a <see cref="MetadataLoadContext"/>-owned <see cref="Assembly"/>;
    /// the caller must not use runtime-type comparisons against its types.
    /// </summary>
    public Assembly LoadFromAssemblyPath(string path)
    {
        _searchPaths.Add(path);
        return _context.LoadFromAssemblyPath(path);
    }

    /// <summary>
    /// Returns true if <paramref name="path"/> is a valid managed PE (skips
    /// native DLLs so enumeration over a mixed directory doesn't blow up).
    /// </summary>
    public static bool IsManagedAssembly(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new PEReader(stream);
            return reader.HasMetadata;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _context.Dispose();
}
