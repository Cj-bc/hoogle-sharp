using System.Xml.Linq;

namespace CSharpHoogle.Cli;

/// <summary>
/// Reads <c>Assets/packages.config</c> as written by NuGetForUnity and resolves
/// each entry's lib dlls under <c>Assets/Packages/{id}.{version}/lib/</c>. Unity
/// projects often declare TFMs that the standard .NET-Core gate rejects, so the
/// closest TFM lib subdir is picked from a Unity-friendly priority list when
/// the project's TFM doesn't match exactly.
/// </summary>
internal static class NuGetForUnityReader
{
    internal sealed record Result(
        IReadOnlyList<DependencyEntry> Entries,
        IReadOnlyList<string> ManifestPaths);

    public static Result Read(ProjectContext ctx, Action<string>? progress)
    {
        var entries = new List<DependencyEntry>();
        var manifests = new List<string>();
        var seenManifest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var start in CollectStartDirs(ctx))
        {
            var packagesConfig = WalkUpFor(start, "Assets", "packages.config");
            if (packagesConfig is null)
            {
                continue;
            }
            if (!seenManifest.Add(packagesConfig))
            {
                continue;
            }
            manifests.Add(packagesConfig);

            var assetsDir = Path.GetDirectoryName(packagesConfig)!; // .../Assets
            var packagesDir = Path.Combine(assetsDir, "Packages");
            if (!Directory.Exists(packagesDir))
            {
                continue;
            }

            var read = ReadPackagesConfig(packagesConfig, packagesDir, ctx.Tfm);
            entries.AddRange(read);

            if (read.Count > 0)
            {
                var pkgCount = read.Select(e => e.SourceName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                progress?.Invoke(
                    $"Indexing {pkgCount} NuGetForUnity packages from {GetFriendlyPath(packagesConfig)}");
            }
        }

        return new Result(entries, manifests);
    }

    private static IEnumerable<string> CollectStartDirs(ProjectContext ctx)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cs in ProjectContextDetector.EnumerateCsprojs(ctx.OriginPath))
        {
            var d = Path.GetDirectoryName(cs);
            if (!string.IsNullOrEmpty(d) && seen.Add(d!))
            {
                yield return d!;
            }
        }

        if (seen.Add(Environment.CurrentDirectory))
        {
            yield return Environment.CurrentDirectory;
        }
    }

    private static IReadOnlyList<DependencyEntry> ReadPackagesConfig(
        string packagesConfig, string packagesDir, string tfm)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(packagesConfig);
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<DependencyEntry>();
        }

        var entries = new List<DependencyEntry>();
        foreach (var pkg in doc.Descendants("package"))
        {
            var id = (string?)pkg.Attribute("id");
            var version = (string?)pkg.Attribute("version");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(version))
            {
                continue;
            }

            var libRoot = Path.Combine(packagesDir, $"{id}.{version}", "lib");
            if (!Directory.Exists(libRoot))
            {
                continue;
            }

            var tfmDir = PickTfmDir(libRoot, tfm);
            if (tfmDir is null)
            {
                continue;
            }

            foreach (var dll in Directory.EnumerateFiles(tfmDir, "*.dll"))
            {
                var xml = Path.ChangeExtension(dll, ".xml");
                entries.Add(new DependencyEntry(
                    dll,
                    File.Exists(xml) ? xml : null,
                    "package",
                    id!));
            }
        }
        return entries;
    }

    private static string? PickTfmDir(string libRoot, string tfm)
    {
        var exact = Path.Combine(libRoot, tfm);
        if (Directory.Exists(exact))
        {
            return exact;
        }

        foreach (var fb in UnityFallbacks.Tfms)
        {
            var candidate = Path.Combine(libRoot, fb);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? WalkUpFor(string startDir, params string[] relParts)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        while (dir is not null)
        {
            var parts = new List<string> { dir.FullName };
            parts.AddRange(relParts);
            var candidate = Path.Combine(parts.ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static string GetFriendlyPath(string fullPath)
    {
        var cwd = Environment.CurrentDirectory;
        if (fullPath.StartsWith(cwd, StringComparison.OrdinalIgnoreCase))
        {
            var rel = fullPath.Substring(cwd.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return rel.Length > 0 ? rel : fullPath;
        }
        return fullPath;
    }
}
