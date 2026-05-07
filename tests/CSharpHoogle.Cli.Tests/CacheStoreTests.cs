using CSharpHoogle.Cli;

namespace CSharpHoogle.Cli.Tests;

public class CacheStoreTests
{
    [Fact]
    public void GetCachePath_BumpedToSchemaV7()
    {
        var ctx = new ProjectContext("net8.0", SdkKind.Default, "/some/path/Foo.csproj");
        var path = CacheStore.GetCachePath(ctx, CacheBucket.Bcl);
        Assert.EndsWith(".v7.json", path);
    }

    [Fact]
    public void GetCachePath_OriginHash_DistinguishesSameTfmContexts()
    {
        var a = new ProjectContext("net8.0", SdkKind.Default, "/projects/AppA/AppA.csproj");
        var b = new ProjectContext("net8.0", SdkKind.Default, "/projects/AppB/AppB.csproj");

        Assert.NotEqual(
            CacheStore.GetCachePath(a, CacheBucket.Bcl),
            CacheStore.GetCachePath(b, CacheBucket.Bcl));
    }

    [Fact]
    public void GetCachePath_IncludesSdk_WhenNonDefault()
    {
        var web = new ProjectContext("net8.0", SdkKind.Web, "/x/Web.csproj");
        var path = CacheStore.GetCachePath(web, CacheBucket.Bcl);
        Assert.Contains("net8.0-web-", path);
    }

    [Fact]
    public void GetCachePath_DistinguishesBuckets()
    {
        var ctx = new ProjectContext("net8.0", SdkKind.Default, "/x/App.csproj");
        var bcl = CacheStore.GetCachePath(ctx, CacheBucket.Bcl);
        var deps = CacheStore.GetCachePath(ctx, CacheBucket.Deps);
        var source = CacheStore.GetCachePath(ctx, CacheBucket.Source);

        Assert.Contains("-bcl.", bcl);
        Assert.Contains("-deps.", deps);
        Assert.Contains("-source.", source);
        Assert.NotEqual(bcl, deps);
        Assert.NotEqual(bcl, source);
        Assert.NotEqual(deps, source);
    }

    [Fact]
    public void TryLoadDeps_ReturnsFalse_WhenManifestNewerThanCache()
    {
        WithTempCtx((tempDir, ctx) =>
        {
            CacheStore.SaveDeps(ctx, new[] { Sample("Test.X.Y") });

            var cachePath = CacheStore.GetCachePath(ctx, CacheBucket.Deps);
            Assert.True(File.Exists(cachePath));

            var manifest = Path.Combine(tempDir, "project.assets.json");
            File.WriteAllText(manifest, "{}");
            File.SetLastWriteTimeUtc(manifest, File.GetLastWriteTimeUtc(cachePath).AddMinutes(1));

            Assert.False(CacheStore.TryLoadDeps(ctx, new[] { manifest }, out var stale));
            Assert.Empty(stale);

            Assert.True(CacheStore.TryLoadDeps(ctx, Array.Empty<string>(), out var hit));
            Assert.NotEmpty(hit);
        });
    }

    [Fact]
    public void TryLoadSource_ReturnsFalse_WhenSourceNewerThanCache()
    {
        WithTempCtx((tempDir, ctx) =>
        {
            CacheStore.SaveSource(ctx, new[] { Sample("App.Greeter.Hello") });

            var cachePath = CacheStore.GetCachePath(ctx, CacheBucket.Source);
            var src = Path.Combine(tempDir, "Greeter.cs");
            File.WriteAllText(src, "// stub");
            File.SetLastWriteTimeUtc(src, File.GetLastWriteTimeUtc(cachePath).AddMinutes(1));

            Assert.False(CacheStore.TryLoadSource(ctx, new[] { src }, out var stale));
            Assert.Empty(stale);
        });
    }

    [Fact]
    public void TryLoadBcl_IgnoresManifestAndSourceChanges()
    {
        WithTempCtx((tempDir, ctx) =>
        {
            CacheStore.SaveBcl(ctx, new[] { Sample("System.X.Y") });

            // Touch a "manifest" and a "source" — neither is passed to
            // TryLoadBcl, so both must be irrelevant. BCL only invalidates on
            // --rebuild (i.e. the caller skipping the load entirely).
            var cachePath = CacheStore.GetCachePath(ctx, CacheBucket.Bcl);
            var manifest = Path.Combine(tempDir, "project.assets.json");
            File.WriteAllText(manifest, "{}");
            File.SetLastWriteTimeUtc(manifest, File.GetLastWriteTimeUtc(cachePath).AddMinutes(1));

            Assert.True(CacheStore.TryLoadBcl(ctx, out var hit));
            Assert.NotEmpty(hit);
        });
    }

    [Fact]
    public void TryLoadDeps_HitsOnEmptyCache()
    {
        // A project with zero deps still saves an empty deps cache; that's a
        // legitimate "all known, nothing to walk" state and must read back as
        // a hit so the caller doesn't keep re-resolving deps.
        WithTempCtx((_, ctx) =>
        {
            CacheStore.SaveDeps(ctx, Array.Empty<CachedMethod>());

            Assert.True(CacheStore.TryLoadDeps(ctx, Array.Empty<string>(), out var hit));
            Assert.Empty(hit);
        });
    }

    private static CachedMethod Sample(string fullName) => new(
        fullName,
        "void",
        Array.Empty<string>(),
        Array.Empty<string>(),
        false,
        "",
        null,
        new MethodSource("assembly", "Test"),
        RequiredParameterCount: 0);

    private static void WithTempCtx(Action<string, ProjectContext> body)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "hoogle-cache-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var ctx = new ProjectContext("net8.0", SdkKind.Default, Path.Combine(tempDir, "App.csproj"));
        try
        {
            body(tempDir, ctx);
        }
        finally
        {
            foreach (var bucket in new[] { CacheBucket.Bcl, CacheBucket.Deps, CacheBucket.Source })
            {
                var p = CacheStore.GetCachePath(ctx, bucket);
                if (File.Exists(p))
                {
                    File.Delete(p);
                }
            }
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
