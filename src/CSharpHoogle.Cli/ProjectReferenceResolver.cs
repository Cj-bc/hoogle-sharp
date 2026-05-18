using System.Xml.Linq;

namespace CSharpHoogle.Cli;

/// <summary>
/// Discovers DLLs declared as direct csproj references for the projects in the
/// detected context. Two forms are handled:
/// <list type="bullet">
///   <item><c>&lt;ProjectReference&gt;</c> — sibling project; resolved against
///   <c>bin/Debug|Release/{tfm}</c>. Skipped with a warning when no built output
///   exists.</item>
///   <item><c>&lt;Reference&gt;</c> with a child <c>&lt;HintPath&gt;</c> — direct
///   binary reference (vendor DLLs, hand-placed assemblies). <c>&lt;Reference&gt;</c>
///   without HintPath (GAC/framework) is left to the BCL pass.</item>
/// </list>
/// Both forms share one <c>seen</c> set so the same absolute DLL path is never
/// emitted twice. Cross-resolver dedup (NuGet vs. these) lives in
/// <see cref="ProjectDependencyResolver"/>.
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

                var asmName = ProjectContextDetector.ReadAssemblyName(refCsproj)
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
                    new MethodSource("project", asmName)));
            }

            entries.AddRange(ResolveReferenceEntries(doc, csprojDir, seen, progress));
        }

        return entries;
    }

    private static IEnumerable<DependencyEntry> ResolveReferenceEntries(
        XDocument doc, string csprojDir, HashSet<string> seen, Action<string>? progress)
    {
        foreach (var r in doc.Descendants("Reference"))
        {
            var hintPath = r.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "HintPath")
                ?.Value
                ?.Trim();
            if (string.IsNullOrEmpty(hintPath))
            {
                continue;
            }

            var include = (string?)r.Attribute("Include");
            var asmName = ParseAssemblyName(include);

            var dllAbs = Path.GetFullPath(Path.Combine(
                csprojDir,
                hintPath!.Replace('\\', Path.DirectorySeparatorChar)));

            if (string.IsNullOrEmpty(asmName))
            {
                asmName = Path.GetFileNameWithoutExtension(dllAbs);
            }

            if (!File.Exists(dllAbs))
            {
                progress?.Invoke(
                    $"Skipping Reference {asmName} (HintPath does not exist: {dllAbs})");
                continue;
            }

            if (!seen.Add(dllAbs))
            {
                continue;
            }

            var xml = Path.ChangeExtension(dllAbs, ".xml");
            yield return new DependencyEntry(
                dllAbs,
                File.Exists(xml) ? xml : null,
                new MethodSource("reference", asmName!));
        }
    }

    /// <summary>
    /// Pulls the assembly simple name out of a <c>&lt;Reference Include="..."&gt;</c>
    /// value. Strong-named references look like
    /// <c>"Newtonsoft.Json, Version=13.0.3.0, Culture=neutral, PublicKeyToken=..."</c>
    /// — only the part before the first comma is the simple name.
    /// </summary>
    private static string? ParseAssemblyName(string? include)
    {
        if (string.IsNullOrWhiteSpace(include))
        {
            return null;
        }
        var commaIdx = include!.IndexOf(',');
        var name = commaIdx >= 0 ? include.Substring(0, commaIdx) : include;
        name = name.Trim();
        return name.Length == 0 ? null : name;
    }

}
