using System.Text.Json;
using System.Xml.Linq;

namespace CSharpHoogle.Cli;

/// <summary>
/// Reads <c>obj/project.assets.json</c> — the lock file <c>dotnet restore</c>
/// writes — and resolves each library's compile/runtime dll relative paths to
/// absolute paths under the package folders the same file declares.
/// </summary>
internal static class NuGetAssetsReader
{
    internal sealed record Result(
        IReadOnlyList<DependencyEntry> Entries,
        IReadOnlyList<string> ManifestPaths);

    public static Result Read(ProjectContext ctx, Action<string>? progress)
    {
        var entries = new List<DependencyEntry>();
        var manifests = new List<string>();
        var anyMissingWithPackageRef = false;

        foreach (var csproj in ProjectContextDetector.EnumerateCsprojs(ctx.OriginPath))
        {
            var csprojDir = Path.GetDirectoryName(csproj);
            if (string.IsNullOrEmpty(csprojDir))
            {
                continue;
            }

            var assetsPath = Path.Combine(csprojDir, "obj", "project.assets.json");
            if (!File.Exists(assetsPath))
            {
                if (CsprojHasPackageReference(csproj))
                {
                    anyMissingWithPackageRef = true;
                }
                continue;
            }

            manifests.Add(assetsPath);

            int packageCount;
            IReadOnlyList<DependencyEntry> read;
            try
            {
                read = ReadAssets(assetsPath, ctx.Tfm, out packageCount);
            }
            catch (JsonException ex)
            {
                progress?.Invoke($"Could not parse {assetsPath}: {ex.Message}");
                continue;
            }

            entries.AddRange(read);
            if (packageCount > 0)
            {
                progress?.Invoke($"Indexing {packageCount} NuGet packages from {GetFriendlyPath(assetsPath)}");
            }
        }

        if (anyMissingWithPackageRef)
        {
            progress?.Invoke("No project.assets.json found; run 'dotnet restore' for package indexing.");
        }

        return new Result(entries, manifests);
    }

    private static IReadOnlyList<DependencyEntry> ReadAssets(string assetsPath, string tfm, out int packageCount)
    {
        packageCount = 0;
        using var doc = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var root = doc.RootElement;

        if (!root.TryGetProperty("targets", out var targets) ||
            !root.TryGetProperty("libraries", out var libraries) ||
            !root.TryGetProperty("packageFolders", out var packageFolders))
        {
            return Array.Empty<DependencyEntry>();
        }

        var chosenTarget = PickTarget(targets, tfm);
        if (chosenTarget.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<DependencyEntry>();
        }

        var roots = packageFolders.EnumerateObject().Select(p => p.Name).ToList();
        var entries = new List<DependencyEntry>();
        var packageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var libProp in chosenTarget.EnumerateObject())
        {
            var libKey = libProp.Name; // "Newtonsoft.Json/13.0.3"
            var libVal = libProp.Value;

            if (!libVal.TryGetProperty("type", out var typeEl)
                || typeEl.GetString() != "package")
            {
                continue;
            }

            var slashIdx = libKey.IndexOf('/');
            if (slashIdx <= 0)
            {
                continue;
            }
            var packageId = libKey.Substring(0, slashIdx);

            if (!libraries.TryGetProperty(libKey, out var libEntry)
                || !libEntry.TryGetProperty("path", out var pathEl))
            {
                continue;
            }
            var libPath = pathEl.GetString();
            if (string.IsNullOrEmpty(libPath))
            {
                continue;
            }

            // Compile (ref-style) takes precedence over runtime — that's the surface
            // referencing code sees and is what we want to reflect for signatures.
            IEnumerable<string> relPaths = Array.Empty<string>();
            if (libVal.TryGetProperty("compile", out var compileEl)
                && compileEl.ValueKind == JsonValueKind.Object)
            {
                relPaths = compileEl.EnumerateObject().Select(p => p.Name).ToList();
            }
            else if (libVal.TryGetProperty("runtime", out var runtimeEl)
                     && runtimeEl.ValueKind == JsonValueKind.Object)
            {
                relPaths = runtimeEl.EnumerateObject().Select(p => p.Name).ToList();
            }

            var added = false;
            foreach (var rel in relPaths)
            {
                // _._ is the empty-placeholder convention for "package compatible
                // but exposes no assemblies for this TFM".
                if (rel.EndsWith("_._", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!rel.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? dllPath = null;
                foreach (var folder in roots)
                {
                    var candidate = Path.GetFullPath(Path.Combine(
                        folder,
                        libPath.Replace('\\', Path.DirectorySeparatorChar),
                        rel.Replace('\\', Path.DirectorySeparatorChar)));
                    if (File.Exists(candidate))
                    {
                        dllPath = candidate;
                        break;
                    }
                }
                if (dllPath is null)
                {
                    continue;
                }

                var xmlPath = Path.ChangeExtension(dllPath, ".xml");
                entries.Add(new DependencyEntry(
                    dllPath,
                    File.Exists(xmlPath) ? xmlPath : null,
                    "package",
                    packageId));
                added = true;
            }

            if (added)
            {
                packageIds.Add(packageId);
            }
        }

        packageCount = packageIds.Count;
        return entries;
    }

    private static JsonElement PickTarget(JsonElement targets, string tfm)
    {
        if (targets.TryGetProperty(tfm, out var exact))
        {
            return exact;
        }

        // Longest matching key like "net8.0/win-x64".
        JsonElement best = default;
        int bestLen = -1;
        var prefix = tfm + "/";
        foreach (var prop in targets.EnumerateObject())
        {
            var k = prop.Name;
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && k.Length > bestLen)
            {
                best = prop.Value;
                bestLen = k.Length;
            }
        }
        return bestLen >= 0 ? best : default;
    }

    private static bool CsprojHasPackageReference(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            return doc.Descendants("PackageReference").Any();
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
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
