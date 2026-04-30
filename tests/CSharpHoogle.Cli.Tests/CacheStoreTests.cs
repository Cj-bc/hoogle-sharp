using CSharpHoogle.Cli;

namespace CSharpHoogle.Cli.Tests;

public class CacheStoreTests
{
    [Fact]
    public void GetCachePath_BumpedToSchemaV5()
    {
        var ctx = new ProjectContext("net8.0", SdkKind.Default, "/some/path/Foo.csproj");
        var path = CacheStore.GetCachePath(ctx);
        Assert.EndsWith(".v5.json", path);
    }

    [Fact]
    public void GetCachePath_OriginHash_DistinguishesSameTfmContexts()
    {
        var a = new ProjectContext("net8.0", SdkKind.Default, "/projects/AppA/AppA.csproj");
        var b = new ProjectContext("net8.0", SdkKind.Default, "/projects/AppB/AppB.csproj");

        Assert.NotEqual(CacheStore.GetCachePath(a), CacheStore.GetCachePath(b));
    }

    [Fact]
    public void GetCachePath_IncludesSdk_WhenNonDefault()
    {
        var web = new ProjectContext("net8.0", SdkKind.Web, "/x/Web.csproj");
        var path = CacheStore.GetCachePath(web);
        Assert.Contains("net8.0-web-", path);
    }

    [Fact]
    public void TryLoad_ReturnsFalse_WhenManifestNewerThanCache()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hoogle-cache-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var ctx = new ProjectContext("net8.0", SdkKind.Default, Path.Combine(tempDir, "App.csproj"));

            // Save a real cache so the path exists.
            CacheStore.Save(ctx, new[]
            {
                new CachedMethod(
                    "Test.X.Y",
                    "void",
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    false,
                    "",
                    null,
                    new MethodSource("assembly", "Test"),
                    RequiredParameterCount: 0)
            });

            var cachePath = CacheStore.GetCachePath(ctx);
            Assert.True(File.Exists(cachePath));

            // Manifest with a newer mtime than the cache file.
            var manifest = Path.Combine(tempDir, "project.assets.json");
            File.WriteAllText(manifest, "{}");
            File.SetLastWriteTimeUtc(manifest, File.GetLastWriteTimeUtc(cachePath).AddMinutes(1));

            var hit = CacheStore.TryLoad(ctx, new[] { manifest }, out var methods);
            Assert.False(hit);
            Assert.Empty(methods);

            // With no manifest list it should hit.
            var hitNoManifests = CacheStore.TryLoad(ctx, Array.Empty<string>(), out var methods2);
            Assert.True(hitNoManifests);
            Assert.NotEmpty(methods2);
        }
        finally
        {
            // Cleanup the cache file too.
            var ctx = new ProjectContext("net8.0", SdkKind.Default, Path.Combine(tempDir, "App.csproj"));
            var cachePath = CacheStore.GetCachePath(ctx);
            if (File.Exists(cachePath))
            {
                File.Delete(cachePath);
            }
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
