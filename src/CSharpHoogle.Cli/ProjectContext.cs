using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CSharpHoogle.Cli;

/// <summary>
/// Which .NET SDK a project declares at its root (<c>&lt;Project Sdk="..."&gt;</c>).
/// Determines which reference packs feed the index — Web/Worker/Razor pull in
/// <c>Microsoft.AspNetCore.App.Ref</c> in addition to <c>Microsoft.NETCore.App.Ref</c>.
/// </summary>
public enum SdkKind
{
    Default,
    Web,
    Worker,
    Razor,
}

/// <summary>
/// Resolved project context driving cache keying and reference-pack selection.
/// <see cref="OriginPath"/> is the file the context was derived from (sln/slnx/csproj)
/// and is shown to the user so they can see why a particular cache was picked.
/// </summary>
public sealed record ProjectContext(string Tfm, SdkKind Sdk, string OriginPath);

public static class ProjectContextDetector
{
    /// <summary>
    /// Walk-up detection per spec: solution at cwd → solution in any parent →
    /// csproj at cwd only. Returns null when nothing matches; callers should
    /// then fall back to the running runtime.
    ///
    /// Overrides win in this order: explicit <paramref name="projectOverride"/> is
    /// parsed directly; <paramref name="tfmOverride"/> can pin the TFM independent
    /// of detection (handy in CI). When both overrides are absent we walk the disk.
    /// </summary>
    public static ProjectContext? Detect(string cwd, string? projectOverride, string? tfmOverride)
    {
        if (!string.IsNullOrEmpty(projectOverride))
        {
            var ctx = LoadFromPath(projectOverride!);
            return ApplyTfmOverride(ctx, tfmOverride);
        }

        var detected = WalkUp(cwd);
        return ApplyTfmOverride(detected, tfmOverride);
    }

    /// <summary>
    /// Returns the set of csproj paths the given origin would aggregate over —
    /// the single csproj for a .csproj origin, every member project for a .sln
    /// or .slnx. Used by dependency resolvers to iterate the same projects that
    /// fed TFM aggregation. Returns empty for non-file origins (e.g. the
    /// "&lt;--target-framework&gt;" sentinel).
    /// </summary>
    public static IReadOnlyList<string> EnumerateCsprojs(string originPath)
    {
        if (string.IsNullOrEmpty(originPath) || !File.Exists(originPath))
        {
            return Array.Empty<string>();
        }

        var ext = Path.GetExtension(originPath).ToLowerInvariant();
        return ext switch
        {
            ".sln" => CollectCsprojsFromSln(originPath),
            ".slnx" => CollectCsprojsFromSlnx(originPath),
            ".csproj" => new[] { Path.GetFullPath(originPath) },
            _ => Array.Empty<string>(),
        };
    }

    /// <summary>
    /// When --project receives a directory, find the project file inside it.
    /// Priority slnx &gt; sln &gt; csproj. Multiple files at the winning priority
    /// are ambiguous — caller must report and exit. Returns null with error set
    /// when ambiguous or when nothing matches; the caller writes error to stderr.
    /// Top-level only (not recursive), mirroring WalkUp's per-directory probe.
    /// </summary>
    public static string? ResolveProjectFromDirectory(string dir, out string? error)
    {
        error = null;

        var slnx = Directory.GetFiles(dir, "*.slnx");
        if (slnx.Length > 1)
        {
            error = $"Multiple .slnx files found in '{dir}': {string.Join(", ", slnx.Select(Path.GetFileName))}. Specify one explicitly with --project.";
            return null;
        }
        if (slnx.Length == 1) return slnx[0];

        var sln = Directory.GetFiles(dir, "*.sln");
        if (sln.Length > 1)
        {
            error = $"Multiple .sln files found in '{dir}': {string.Join(", ", sln.Select(Path.GetFileName))}. Specify one explicitly with --project.";
            return null;
        }
        if (sln.Length == 1) return sln[0];

        var csproj = Directory.GetFiles(dir, "*.csproj");
        if (csproj.Length > 1)
        {
            error = $"Multiple .csproj files found in '{dir}': {string.Join(", ", csproj.Select(Path.GetFileName))}. Specify one explicitly with --project.";
            return null;
        }
        if (csproj.Length == 1) return csproj[0];

        error = $"No project files (.slnx, .sln, .csproj) found in directory '{dir}'.";
        return null;
    }

    private static ProjectContext? ApplyTfmOverride(ProjectContext? ctx, string? tfmOverride)
    {
        if (string.IsNullOrEmpty(tfmOverride))
        {
            return ctx;
        }

        // --target-framework can be used standalone (no project on disk) — synthesize
        // a context with Default SDK; user can also override TFM on top of detection.
        var sdk = ctx?.Sdk ?? SdkKind.Default;
        var origin = ctx?.OriginPath ?? "<--target-framework>";
        return new ProjectContext(tfmOverride!, sdk, origin);
    }

    private static ProjectContext? WalkUp(string startDir)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(startDir));
        var firstDir = true;

        while (dir is not null)
        {
            var slnx = dir.GetFiles("*.slnx").FirstOrDefault();
            if (slnx is not null)
            {
                return LoadFromSlnx(slnx.FullName);
            }

            var sln = dir.GetFiles("*.sln").FirstOrDefault();
            if (sln is not null)
            {
                return LoadFromSln(sln.FullName);
            }

            // csproj only at the original cwd, not in any parent.
            if (firstDir)
            {
                var csproj = dir.GetFiles("*.csproj").FirstOrDefault();
                if (csproj is not null)
                {
                    return LoadFromCsproj(csproj.FullName);
                }
            }

            firstDir = false;
            dir = dir.Parent;
        }

        return null;
    }

    private static ProjectContext? LoadFromPath(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".slnx" => LoadFromSlnx(path),
            ".sln" => LoadFromSln(path),
            ".csproj" => LoadFromCsproj(path),
            _ => null,
        };
    }

    private static ProjectContext? LoadFromSln(string slnPath) =>
        Aggregate(CollectCsprojsFromSln(slnPath), slnPath);

    private static ProjectContext? LoadFromSlnx(string slnxPath) =>
        Aggregate(CollectCsprojsFromSlnx(slnxPath), slnxPath);

    private static IReadOnlyList<string> CollectCsprojsFromSln(string slnPath)
    {
        // sln line: Project("{type-guid}") = "Name", "relative\path\proj.csproj", "{guid}"
        var rx = new Regex(
            "^Project\\(\"\\{[^}]+\\}\"\\)\\s*=\\s*\"[^\"]*\",\\s*\"([^\"]+)\"",
            RegexOptions.Multiline);

        var slnDir = Path.GetDirectoryName(slnPath)!;
        var csprojs = new List<string>();
        foreach (Match m in rx.Matches(File.ReadAllText(slnPath)))
        {
            var rel = m.Groups[1].Value;
            if (!rel.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // sln paths use backslashes even on Linux; normalize.
            rel = rel.Replace('\\', Path.DirectorySeparatorChar);
            csprojs.Add(Path.GetFullPath(Path.Combine(slnDir, rel)));
        }
        return csprojs;
    }

    private static IReadOnlyList<string> CollectCsprojsFromSlnx(string slnxPath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(slnxPath);
        }
        catch (System.Xml.XmlException)
        {
            return Array.Empty<string>();
        }

        var slnDir = Path.GetDirectoryName(slnxPath)!;
        return doc.Descendants("Project")
            .Select(p => (string?)p.Attribute("Path"))
            .Where(p => !string.IsNullOrEmpty(p) && p!.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(slnDir, p!.Replace('\\', Path.DirectorySeparatorChar))))
            .ToList();
    }

    private static ProjectContext? Aggregate(IReadOnlyList<string> csprojPaths, string originPath)
    {
        var members = csprojPaths
            .Where(File.Exists)
            .Select(p => LoadFromCsproj(p))
            .OfType<ProjectContext>()
            .ToList();

        if (members.Count == 0)
        {
            return null;
        }

        // Highest TFM wins; SDK kind picks the most specialized (Web > Razor > Worker > Default).
        var tfm = members
            .Select(m => m.Tfm)
            .OrderByDescending(TfmKey)
            .First();
        var sdk = members.Select(m => m.Sdk).Aggregate(SdkKind.Default, MoreSpecialized);

        return new ProjectContext(tfm, sdk, originPath);
    }

    private static ProjectContext? LoadFromCsproj(string csprojPath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(csprojPath);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var root = doc.Root;
        if (root is null)
        {
            return null;
        }

        var sdkAttr = (string?)root.Attribute("Sdk") ?? "";
        var sdkKind = ClassifySdk(sdkAttr);

        // Single-TFM and multi-TFM forms; element name is unqualified in SDK-style csproj.
        var rawTfms = root.Descendants("TargetFramework")
            .Select(e => e.Value)
            .Concat(root.Descendants("TargetFrameworks").SelectMany(e => e.Value.Split(';')))
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        var supported = rawTfms.Where(IsSupportedNetTfm).ToList();
        if (supported.Count > 0)
        {
            var tfm = supported.OrderByDescending(TfmKey).First();
            return new ProjectContext(tfm, sdkKind, csprojPath);
        }

        // Unity fallback: a Unity-shaped tree (Assets/packages.config in any ancestor)
        // has csprojs declaring TFMs the .NET-Core gate rejects (netstandard2.1, net48).
        // Accept the highest-priority Unity TFM the project actually declares; SDK kind
        // is forced to Default (BCL ref-pack resolution gates on IsSupportedNetTfm so
        // ref packs still won't be used — the index falls through to runtime BCL).
        if (HasUnityPackagesConfig(csprojPath))
        {
            foreach (var preferred in UnityFallbacks.Tfms)
            {
                if (rawTfms.Contains(preferred, StringComparer.OrdinalIgnoreCase))
                {
                    return new ProjectContext(preferred, SdkKind.Default, csprojPath);
                }
            }
        }

        return null;
    }

    private static bool HasUnityPackagesConfig(string csprojPath)
    {
        var dir = new DirectoryInfo(Path.GetFullPath(Path.GetDirectoryName(csprojPath)!));
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Assets", "packages.config");
            if (File.Exists(candidate))
            {
                return true;
            }
            dir = dir.Parent;
        }
        return false;
    }

    private static SdkKind ClassifySdk(string sdk)
    {
        if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase)) return SdkKind.Web;
        if (sdk.Contains("Razor", StringComparison.OrdinalIgnoreCase)) return SdkKind.Razor;
        if (sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase)) return SdkKind.Worker;
        return SdkKind.Default;
    }

    private static SdkKind MoreSpecialized(SdkKind a, SdkKind b) =>
        Rank(a) >= Rank(b) ? a : b;

    private static int Rank(SdkKind k) => k switch
    {
        SdkKind.Web => 3,
        SdkKind.Razor => 2,
        SdkKind.Worker => 1,
        _ => 0,
    };

    /// <summary>
    /// Net TFM only — netstandard / netcoreapp / net48 fall through to runtime fallback.
    /// "net8.0" yes; "net8.0-windows" yes (we strip the platform); "netstandard2.0" no.
    /// </summary>
    private static bool IsSupportedNetTfm(string tfm)
    {
        return TryParseNetTfm(tfm, out _, out _);
    }

    public static bool TryParseNetTfm(string tfm, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (!tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rest = tfm.Substring(3);
        // Drop platform suffix like "-windows", "-android" — same ref pack for our purposes.
        var dash = rest.IndexOf('-');
        if (dash >= 0)
        {
            rest = rest.Substring(0, dash);
        }

        // Reject pre-Core "net48" style (no dot).
        if (!rest.Contains('.'))
        {
            return false;
        }

        var parts = rest.Split('.');
        return parts.Length == 2
            && int.TryParse(parts[0], out major)
            && int.TryParse(parts[1], out minor);
    }

    private static (int, int) TfmKey(string tfm) =>
        TryParseNetTfm(tfm, out var maj, out var min) ? (maj, min) : (0, 0);

    internal static string? ReadAssemblyName(string csprojPath)
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
