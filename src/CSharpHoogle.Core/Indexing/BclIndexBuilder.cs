using System.Runtime.InteropServices;
using CSharpHoogle.Core.Models;
using CSharpHoogle.Core.Parsing;

namespace CSharpHoogle.Core.Indexing;

/// <summary>
/// Builds a <see cref="DocEntry"/> index from BCL assemblies and/or
/// arbitrary XML doc files (UnityEngine.xml, user-project .xml, etc.).
/// </summary>
public static class BclIndexBuilder
{
    /// <summary>
    /// Scans <paramref name="docDir"/> for *.dll files and parses each
    /// adjacent .xml file into the merged index. When <paramref name="docDir"/>
    /// is null, resolves it via <see cref="ResolveDefaultDocDir"/> — preferring
    /// the reference pack (which actually ships XML docs) over the shared runtime
    /// directory (which typically does not).
    /// </summary>
    public static IReadOnlyDictionary<string, DocEntry> BuildFromRuntime(string? docDir = null)
    {
        docDir ??= ResolveDefaultDocDir();

        var merged = new Dictionary<string, DocEntry>(StringComparer.Ordinal);
        foreach (var dll in Directory.EnumerateFiles(docDir, "*.dll"))
        {
            foreach (var kv in XmlDocParser.ParseForAssembly(dll))
            {
                merged[kv.Key] = kv.Value;
            }
        }

        return merged;
    }

    /// <summary>
    /// Finds the directory most likely to contain BCL XML doc files for the
    /// running runtime. On typical installs, .NET XML docs ship with the
    /// reference pack (dotnet/packs/Microsoft.NETCore.App.Ref/{ver}/ref/net{x.y})
    /// and not with the shared runtime. This method:
    ///   1. Locates the running runtime directory.
    ///   2. If it has any *.xml files, returns it.
    ///   3. Otherwise walks up to the dotnet root and picks the highest
    ///      reference pack whose TFM matches the running runtime.
    ///   4. Falls back to the runtime directory if no ref pack is found
    ///      (caller will get an empty index, which mirrors a trimmed runtime).
    /// </summary>
    public static string ResolveDefaultDocDir()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)
            ?? throw new InvalidOperationException(
                "Could not determine runtime directory from typeof(object).Assembly.Location.");

        if (Directory.GetFiles(runtimeDir, "*.xml").Length > 0)
        {
            return runtimeDir;
        }

        if (TryResolveReferencePackDir(runtimeDir, out var refPackDir))
        {
            return refPackDir;
        }

        return runtimeDir;
    }

    private static bool TryResolveReferencePackDir(string runtimeDir, out string refPackDir)
    {
        refPackDir = string.Empty;

        // runtimeDir = .../dotnet/shared/Microsoft.NETCore.App/{runtimeVer}
        // We want    .../dotnet/packs/Microsoft.NETCore.App.Ref/{refVer}/ref/net{major.minor}
        var sharedDir = Path.GetDirectoryName(runtimeDir);        // shared/Microsoft.NETCore.App
        var shared = Path.GetDirectoryName(sharedDir);            // shared
        var dotnetRoot = Path.GetDirectoryName(shared);           // dotnet
        if (dotnetRoot is null)
        {
            return false;
        }

        var packsRef = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
        if (!Directory.Exists(packsRef))
        {
            return false;
        }

        // Running runtime major.minor — used to pick the matching net{x.y} folder.
        var fxDesc = RuntimeInformation.FrameworkDescription; // e.g. ".NET 8.0.26"
        var versionToken = fxDesc.Split(' ').LastOrDefault();
        if (versionToken is null || !Version.TryParse(versionToken, out var fxVer))
        {
            return false;
        }

        var tfm = $"net{fxVer.Major}.{fxVer.Minor}";

        // Prefer the ref-pack version closest to the running runtime: same major.minor,
        // highest patch. Fall back to any ref pack with a matching ref/net{tfm} folder.
        var candidates = Directory.GetDirectories(packsRef)
            .Select(dir => (Dir: dir, Ver: TryParseVersion(Path.GetFileName(dir))))
            .Where(t => t.Ver is not null)
            .Where(t => t.Ver!.Major == fxVer.Major && t.Ver.Minor == fxVer.Minor)
            .OrderByDescending(t => t.Ver)
            .Select(t => Path.Combine(t.Dir, "ref", tfm))
            .Where(Directory.Exists)
            .ToList();

        if (candidates.Count == 0)
        {
            return false;
        }

        refPackDir = candidates[0];
        return true;
    }

    private static Version? TryParseVersion(string s) =>
        Version.TryParse(s, out var v) ? v : null;

    /// <summary>
    /// Parses each XML file in <paramref name="xmlPaths"/> and merges the results.
    /// Missing files throw (unlike <see cref="XmlDocParser.ParseForAssembly"/>) —
    /// the caller named a specific file, so it is expected to exist.
    /// Overlapping member keys across files: last wins.
    /// </summary>
    public static IReadOnlyDictionary<string, DocEntry> BuildFromFiles(IEnumerable<string> xmlPaths)
    {
        var merged = new Dictionary<string, DocEntry>(StringComparer.Ordinal);
        foreach (var xmlPath in xmlPaths)
        {
            foreach (var kv in XmlDocParser.Parse(xmlPath))
            {
                merged[kv.Key] = kv.Value;
            }
        }

        return merged;
    }
}
