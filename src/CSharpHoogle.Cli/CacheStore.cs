using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CSharpHoogle.Cli;

/// <summary>
/// Reads and writes the CLI's on-disk index cache. With a <see cref="ProjectContext"/>
/// the cache is keyed by (TFM, SDK kind, origin-hash) so each project keeps its own
/// index even when several projects share a TFM. With no context, falls back to a
/// runtime-version-keyed file (the pre-project-detection behavior).
/// </summary>
public static class CacheStore
{
    private const string AppFolderName = "csharp-hoogle";
    private const int SchemaVersion = 5;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string GetCachePath() => GetCachePath(null);

    public static string GetCachePath(ProjectContext? ctx)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            // Fallback for environments where LocalApplicationData is empty.
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache");
        }

        var dir = Path.Combine(root, AppFolderName);
        var key = ctx is null ? GetRuntimeTag() : GetContextTag(ctx);
        return Path.Combine(dir, $"index-{key}.v{SchemaVersion}.json");
    }

    public static bool TryLoad(out IReadOnlyList<CachedMethod> methods) =>
        TryLoad(null, Array.Empty<string>(), out methods);

    public static bool TryLoad(ProjectContext? ctx, out IReadOnlyList<CachedMethod> methods) =>
        TryLoad(ctx, Array.Empty<string>(), out methods);

    /// <summary>
    /// Loads the cached index, treating it as a miss when any path in
    /// <paramref name="manifestPaths"/> (typically project.assets.json /
    /// packages.config) has a newer mtime than the cache file. This lets a
    /// fresh <c>dotnet restore</c> auto-invalidate the cache without the user
    /// having to pass <c>--rebuild</c>.
    /// </summary>
    public static bool TryLoad(
        ProjectContext? ctx,
        IReadOnlyList<string> manifestPaths,
        out IReadOnlyList<CachedMethod> methods)
    {
        var path = GetCachePath(ctx);
        if (!File.Exists(path))
        {
            methods = Array.Empty<CachedMethod>();
            return false;
        }

        var cacheMtime = File.GetLastWriteTimeUtc(path);
        foreach (var manifest in manifestPaths)
        {
            if (File.Exists(manifest) && File.GetLastWriteTimeUtc(manifest) > cacheMtime)
            {
                methods = Array.Empty<CachedMethod>();
                return false;
            }
        }

        try
        {
            using var stream = File.OpenRead(path);
            var loaded = JsonSerializer.Deserialize<List<CachedMethod>>(stream, Json);
            methods = loaded ?? new List<CachedMethod>();
            return methods.Count > 0;
        }
        catch (JsonException)
        {
            // Corrupt cache — treat as a miss so the caller rebuilds.
            methods = Array.Empty<CachedMethod>();
            return false;
        }
    }

    public static void Save(IReadOnlyList<CachedMethod> methods) => Save(null, methods);

    public static void Save(ProjectContext? ctx, IReadOnlyList<CachedMethod> methods)
    {
        var path = GetCachePath(ctx);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, methods, Json);
    }

    private static string GetContextTag(ProjectContext ctx)
    {
        var tfm = SanitizeTag(ctx.Tfm);
        var sdkPart = ctx.Sdk == SdkKind.Default ? "" : $"-{ctx.Sdk.ToString().ToLowerInvariant()}";
        return $"{tfm}{sdkPart}-{OriginHash(ctx.OriginPath)}";
    }

    private static string OriginHash(string originPath)
    {
        var bytes = Encoding.UTF8.GetBytes(originPath);
        var hash = SHA1.HashData(bytes);
        return Convert.ToHexString(hash).Substring(0, 8).ToLowerInvariant();
    }

    private static string SanitizeTag(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' ? ch : '-');
        }
        return sb.ToString().ToLowerInvariant().Trim('-');
    }

    private static string GetRuntimeTag()
    {
        // "NET 8.0.26" etc. → "net-8.0.26" safe for a filename.
        var desc = RuntimeInformation.FrameworkDescription;
        var sb = new StringBuilder(desc.Length);
        foreach (var ch in desc)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' ? ch : '-');
        }
        return sb.ToString().ToLowerInvariant().Trim('-');
    }
}
