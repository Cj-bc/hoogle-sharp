using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CSharpHoogle.Cli;

/// <summary>
/// Reads and writes the CLI's on-disk index cache. With a <see cref="ProjectContext"/>
/// the cache is keyed by (TFM, SDK kind) so different projects can each keep their
/// own index. With no context, falls back to a runtime-version-keyed file (the
/// pre-project-detection behavior).
/// </summary>
public static class CacheStore
{
    private const string AppFolderName = "csharp-hoogle";
    private const int SchemaVersion = 3;

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
        TryLoad(null, out methods);

    public static bool TryLoad(ProjectContext? ctx, out IReadOnlyList<CachedMethod> methods)
    {
        var path = GetCachePath(ctx);
        if (!File.Exists(path))
        {
            methods = Array.Empty<CachedMethod>();
            return false;
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
        return ctx.Sdk == SdkKind.Default
            ? tfm
            : $"{tfm}-{ctx.Sdk.ToString().ToLowerInvariant()}";
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
