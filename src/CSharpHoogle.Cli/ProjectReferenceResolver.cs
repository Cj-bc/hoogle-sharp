using System.Xml.Linq;

namespace CSharpHoogle.Cli;

/// <summary>
/// Discovers the built bin output of each <c>&lt;ProjectReference&gt;</c>
/// declared by the projects belonging to the detected context. Best-effort:
/// when neither bin/Debug nor bin/Release contains the expected dll, the
/// reference is skipped with a warning rather than failing the index.
/// </summary>
internal static class ProjectReferenceResolver
{
    public static IReadOnlyList<DependencyEntry> Resolve(ProjectContext ctx, Action<string>? progress)
    {
        var entries = new List<DependencyEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var csproj in ProjectContextDetector.EnumerateCsprojs(ctx.OriginPath))
        {
            XDocument doc;
            try
            {
                doc = XDocument.Load(csproj);
            }
            catch (System.Xml.XmlException)
            {
                continue;
            }

            var csprojDir = Path.GetDirectoryName(csproj);
            if (string.IsNullOrEmpty(csprojDir))
            {
                continue;
            }

            foreach (var pr in doc.Descendants("ProjectReference"))
            {
                var include = (string?)pr.Attribute("Include");
                if (string.IsNullOrEmpty(include))
                {
                    continue;
                }

                var refCsproj = Path.GetFullPath(Path.Combine(
                    csprojDir,
                    include!.Replace('\\', Path.DirectorySeparatorChar)));
                var refDir = Path.GetDirectoryName(refCsproj);
                if (string.IsNullOrEmpty(refDir))
                {
                    continue;
                }

                var asmName = ReadAssemblyName(refCsproj)
                              ?? Path.GetFileNameWithoutExtension(refCsproj);

                var debugDll = Path.Combine(refDir, "bin", "Debug", ctx.Tfm, asmName + ".dll");
                var releaseDll = Path.Combine(refDir, "bin", "Release", ctx.Tfm, asmName + ".dll");
                string? dll =
                    File.Exists(debugDll) ? debugDll
                    : File.Exists(releaseDll) ? releaseDll
                    : null;

                if (dll is null)
                {
                    progress?.Invoke(
                        $"Skipping ProjectReference {asmName} (no built output at bin/Debug/{ctx.Tfm} or bin/Release/{ctx.Tfm})");
                    continue;
                }

                if (!seen.Add(dll))
                {
                    continue;
                }

                var xml = Path.ChangeExtension(dll, ".xml");
                entries.Add(new DependencyEntry(
                    dll,
                    File.Exists(xml) ? xml : null,
                    "project",
                    asmName));
            }
        }

        return entries;
    }

    private static string? ReadAssemblyName(string csprojPath)
    {
        if (!File.Exists(csprojPath))
        {
            return null;
        }
        try
        {
            var doc = XDocument.Load(csprojPath);
            return doc.Descendants("AssemblyName")
                .Select(e => e.Value.Trim())
                .FirstOrDefault(s => !string.IsNullOrEmpty(s));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }
}
