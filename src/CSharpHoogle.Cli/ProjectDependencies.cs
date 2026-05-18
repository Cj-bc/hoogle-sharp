namespace CSharpHoogle.Cli;

/// <summary>
/// One indexable assembly discovered from a project's deps. <see cref="Source"/>
/// uses <see cref="MethodSource.Kind"/> <c>"package"</c> for NuGet packages
/// (assets.json or NuGetForUnity) and <c>"project"</c> for sibling project bin output.
/// </summary>
public sealed record DependencyEntry(
    string AssemblyPath,
    string? XmlPath,
    MethodSource Source);

/// <summary>
/// Resolved dep set for a <see cref="ProjectContext"/>. <see cref="ManifestPaths"/>
/// holds the lock/manifest files actually consulted (project.assets.json,
/// packages.config) so <see cref="CacheStore"/> can compare their mtimes against
/// the cache file and rebuild automatically when restore runs.
/// </summary>
public sealed record ProjectDependencies(
    IReadOnlyList<DependencyEntry> Entries,
    IReadOnlyList<string> ManifestPaths);

/// <summary>
/// Aggregates standard NuGet (obj/project.assets.json), NuGetForUnity
/// (Assets/packages.config), and project-reference build outputs for the
/// projects belonging to a detected <see cref="ProjectContext"/>.
/// </summary>
public static class ProjectDependencyResolver
{
    public static ProjectDependencies Resolve(ProjectContext ctx, Action<string>? progress = null)
    {
        var entries = new List<DependencyEntry>();
        var manifests = new List<string>();
        var seenAsm = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenManifest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddEntries(IEnumerable<DependencyEntry> source)
        {
            foreach (var e in source)
            {
                if (seenAsm.Add(e.AssemblyPath))
                {
                    entries.Add(e);
                }
            }
        }

        void AddManifests(IEnumerable<string> paths)
        {
            foreach (var p in paths)
            {
                if (seenManifest.Add(p))
                {
                    manifests.Add(p);
                }
            }
        }

        var nuget = NuGetAssetsReader.Read(ctx, progress);
        AddManifests(nuget.ManifestPaths);
        AddEntries(nuget.Entries);

        var unity = NuGetForUnityReader.Read(ctx, progress);
        AddManifests(unity.ManifestPaths);
        AddEntries(unity.Entries);

        var projRefs = ProjectReferenceResolver.Resolve(ctx, progress);
        AddEntries(projRefs);

        return new ProjectDependencies(entries, manifests);
    }
}
