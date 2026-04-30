using System.Xml.Linq;

namespace CSharpHoogle.Cli;

/// <summary>
/// Resolves the set of <c>.cs</c> files a csproj would compile. Unity- and
/// Godot-generated csprojs list their compile items explicitly with
/// <c>&lt;Compile Include="..."/&gt;</c>; pure SDK-style csprojs leave it to
/// the SDK's default glob (<c>**/*.cs</c> under the project dir, minus
/// <c>obj</c>/<c>bin</c>). One helper covers all three.
/// </summary>
internal static class CompileItemEnumerator
{
    /// <summary>
    /// Enumerates absolute paths of .cs files the csproj declares (or, when
    /// no explicit Compile items exist, the SDK-default glob would pick up).
    /// Honors <c>&lt;Compile Remove="..."/&gt;</c>.
    /// </summary>
    public static IReadOnlyList<string> Enumerate(string csprojPath)
    {
        if (!File.Exists(csprojPath))
        {
            return Array.Empty<string>();
        }

        XDocument doc;
        try
        {
            doc = XDocument.Load(csprojPath);
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<string>();
        }

        var csprojDir = Path.GetDirectoryName(csprojPath);
        if (string.IsNullOrEmpty(csprojDir))
        {
            return Array.Empty<string>();
        }

        var includes = doc.Descendants("Compile")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList();

        var removes = doc.Descendants("Compile")
            .Select(e => (string?)e.Attribute("Remove"))
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => s!)
            .ToList();

        var collected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (includes.Count > 0)
        {
            foreach (var item in includes)
            {
                foreach (var path in ExpandPattern(csprojDir, item))
                {
                    collected.Add(path);
                }
            }
        }
        else
        {
            // SDK default glob: every .cs under the project dir, minus obj/bin.
            foreach (var path in DefaultGlob(csprojDir))
            {
                collected.Add(path);
            }
        }

        if (removes.Count > 0)
        {
            foreach (var item in removes)
            {
                foreach (var path in ExpandPattern(csprojDir, item))
                {
                    collected.Remove(path);
                }
            }
        }

        return collected.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> ExpandPattern(string csprojDir, string raw)
    {
        // Normalize Windows-style separators that Unity/Godot tooling emits even
        // on Linux into the platform separator before handing to the FS layer.
        var pattern = raw.Replace('\\', Path.DirectorySeparatorChar);

        // Roslyn-style globs can use ** for "any depth" — translate to the
        // closest equivalent the BCL EnumerateFiles supports.
        if (pattern.Contains("**"))
        {
            // Strip the **/ prefix or *\* segments and use SearchOption.AllDirectories
            // on whatever literal prefix exists ahead of the glob marker.
            var globStart = pattern.IndexOf("**", StringComparison.Ordinal);
            var literalPrefix = globStart > 0
                ? pattern.Substring(0, globStart).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                : "";
            var afterGlob = pattern.Substring(globStart + 2)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Trailing pattern is what the matcher applies to each candidate
            // (e.g. "**/*.cs" → search "*.cs" recursively).
            var searchPattern = string.IsNullOrEmpty(afterGlob) ? "*.cs" : afterGlob;
            var searchRoot = Path.GetFullPath(Path.Combine(csprojDir, literalPrefix));

            if (!Directory.Exists(searchRoot))
            {
                yield break;
            }

            foreach (var file in Directory.EnumerateFiles(searchRoot, searchPattern, SearchOption.AllDirectories))
            {
                if (IsExcludedDir(file, csprojDir))
                {
                    continue;
                }
                yield return Path.GetFullPath(file);
            }
            yield break;
        }

        // Plain glob (no **) or a direct file reference.
        var combined = Path.GetFullPath(Path.Combine(csprojDir, pattern));

        if (File.Exists(combined))
        {
            yield return combined;
            yield break;
        }

        // If pattern contains wildcards in the leaf, search the parent dir.
        var dir = Path.GetDirectoryName(combined);
        var leaf = Path.GetFileName(combined);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || string.IsNullOrEmpty(leaf))
        {
            yield break;
        }
        if (leaf.Contains('*') || leaf.Contains('?'))
        {
            foreach (var file in Directory.EnumerateFiles(dir, leaf, SearchOption.TopDirectoryOnly))
            {
                yield return Path.GetFullPath(file);
            }
        }
    }

    private static IEnumerable<string> DefaultGlob(string csprojDir)
    {
        foreach (var file in Directory.EnumerateFiles(csprojDir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsExcludedDir(file, csprojDir))
            {
                continue;
            }
            yield return Path.GetFullPath(file);
        }
    }

    private static bool IsExcludedDir(string filePath, string csprojDir)
    {
        // Match obj/ and bin/ as path segments under the project dir — this is
        // what the SDK's default Compile glob excludes. Comparing with leading
        // separators avoids false positives for files literally named `objects`.
        var rel = Path.GetRelativePath(csprojDir, filePath);
        var sep = Path.DirectorySeparatorChar;
        return rel.StartsWith($"obj{sep}", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith($"bin{sep}", StringComparison.OrdinalIgnoreCase);
    }
}
