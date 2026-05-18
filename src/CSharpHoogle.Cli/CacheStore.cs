using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CSharpHoogle.Cli;

/// <summary>
/// Which sub-cache the call refers to. The CLI splits its index into three
/// independently-invalidated buckets so a source edit doesn't force a BCL or
/// dependency rebuild and a <c>dotnet restore</c> doesn't force a source
/// rebuild.
/// </summary>
public enum CacheBucket
{
    /// <summary>BCL/reference-pack assemblies — keyed by TFM/SDK only.</summary>
    Bcl,
    /// <summary>NuGet/project-reference dlls — invalidated by manifest mtimes.</summary>
    Deps,
    /// <summary>Methods parsed from .cs files — invalidated by source mtimes.</summary>
    Source,
}

/// <summary>
/// Reads and writes the CLI's on-disk index cache. With a <see cref="ProjectContext"/>
/// the cache is keyed by (TFM, SDK kind, origin-hash) so each project keeps its own
/// index even when several projects share a TFM. With no context, falls back to a
/// runtime-version-keyed file (the pre-project-detection behavior).
/// </summary>
public static class CacheStore
{
    private const string AppFolderName = "csharp-hoogle";
    private const int SchemaVersion = 7;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string GetCachePath(CacheBucket bucket) => GetCachePath(null, bucket);

    public static string GetCachePath(ProjectContext? ctx, CacheBucket bucket)
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
        return Path.Combine(dir, $"index-{key}-{BucketTag(bucket)}.v{SchemaVersion}.json");
    }

    public static bool TryLoadBcl(ProjectContext? ctx, out IReadOnlyList<CachedMethod> methods)
        => TryLoadInternal(ctx, CacheBucket.Bcl, Array.Empty<string>(), out methods);

    public static bool TryLoadDeps(
        ProjectContext? ctx,
        IReadOnlyList<string> manifestPaths,
        out IReadOnlyList<CachedMethod> methods)
        => TryLoadInternal(ctx, CacheBucket.Deps, manifestPaths, out methods);

    public static bool TryLoadSource(
        ProjectContext? ctx,
        IReadOnlyList<string> sourceFiles,
        out IReadOnlyList<CachedMethod> methods)
        => TryLoadInternal(ctx, CacheBucket.Source, sourceFiles, out methods);

    public static void SaveBcl(ProjectContext? ctx, IReadOnlyList<CachedMethod> methods)
        => SaveInternal(ctx, CacheBucket.Bcl, methods);

    public static void SaveDeps(ProjectContext? ctx, IReadOnlyList<CachedMethod> methods)
        => SaveInternal(ctx, CacheBucket.Deps, methods);

    public static void SaveSource(ProjectContext? ctx, IReadOnlyList<CachedMethod> methods)
        => SaveInternal(ctx, CacheBucket.Source, methods);

    /// <summary>
    /// Per-bucket "try load, otherwise build-and-save" orchestration. When
    /// <paramref name="forceRebuild"/> is set, or the on-disk cache for
    /// <paramref name="bucket"/> is missing/invalidated/corrupt, calls
    /// <paramref name="build"/> and persists the result. Otherwise returns the
    /// loaded methods untouched. The returned <c>Rebuilt</c> flag tells the
    /// caller whether the bucket was actually rebuilt — used to log which
    /// buckets contributed to a slow startup.
    /// </summary>
    public static (IReadOnlyList<CachedMethod> Methods, bool Rebuilt) LoadOrBuild(
        ProjectContext? ctx,
        CacheBucket bucket,
        IReadOnlyList<string> invalidationPaths,
        bool forceRebuild,
        Func<IReadOnlyList<CachedMethod>> build)
    {
        if (!forceRebuild && TryLoadInternal(ctx, bucket, invalidationPaths, out var loaded))
        {
            return (loaded, false);
        }

        var built = build();
        SaveInternal(ctx, bucket, built);
        return (built, true);
    }

    /// <summary>
    /// Treats the cache as a miss when any path in <paramref name="invalidationPaths"/>
    /// has a newer mtime than the cache file. An empty cache file (legitimately
    /// no entries — e.g. a project with zero deps) is a hit.
    /// </summary>
    private static bool TryLoadInternal(
        ProjectContext? ctx,
        CacheBucket bucket,
        IReadOnlyList<string> invalidationPaths,
        out IReadOnlyList<CachedMethod> methods)
    {
        var path = GetCachePath(ctx, bucket);
        if (!File.Exists(path))
        {
            methods = Array.Empty<CachedMethod>();
            return false;
        }

        var cacheMtime = File.GetLastWriteTimeUtc(path);
        foreach (var p in invalidationPaths)
        {
            if (File.Exists(p) && File.GetLastWriteTimeUtc(p) > cacheMtime)
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
            return true;
        }
        catch (JsonException)
        {
            // Corrupt cache — treat as a miss so the caller rebuilds.
            methods = Array.Empty<CachedMethod>();
            return false;
        }
    }

    private static void SaveInternal(
        ProjectContext? ctx,
        CacheBucket bucket,
        IReadOnlyList<CachedMethod> methods)
    {
        var path = GetCachePath(ctx, bucket);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, methods, Json);
    }

    private static string BucketTag(CacheBucket bucket) => bucket switch
    {
        CacheBucket.Bcl => "bcl",
        CacheBucket.Deps => "deps",
        CacheBucket.Source => "source",
        _ => throw new ArgumentOutOfRangeException(nameof(bucket), bucket, null),
    };

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

    private static string SanitizeFilenameTag(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' ? ch : '-');
        }
        return sb.ToString().ToLowerInvariant().Trim('-');
    }

    private static string SanitizeTag(string raw) => SanitizeFilenameTag(raw);

    private static string GetRuntimeTag() =>
        SanitizeFilenameTag(RuntimeInformation.FrameworkDescription);
}
