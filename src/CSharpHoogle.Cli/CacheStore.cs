using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CSharpHoogle.Cli;

/// <summary>
/// Reads and writes the CLI's on-disk index cache.
/// The cache path is keyed by the running .NET runtime version so SDK
/// upgrades naturally invalidate the cache.
/// </summary>
public static class CacheStore
{
    private const string AppFolderName = "csharp-hoogle";
    private const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static string GetCachePath()
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
        return Path.Combine(dir, $"index-{GetRuntimeTag()}.v{SchemaVersion}.json");
    }

    public static bool TryLoad(out IReadOnlyList<CachedMethod> methods)
    {
        var path = GetCachePath();
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

    public static void Save(IReadOnlyList<CachedMethod> methods)
    {
        var path = GetCachePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, methods, Json);
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
