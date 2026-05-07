using CSharpHoogle.Core.Reflection;

namespace CSharpHoogle.Core.Tests.Reflection;

public class MetadataLoaderTests
{
    private static string RuntimeDir =>
        Path.GetDirectoryName(typeof(object).Assembly.Location)!;

    [Fact]
    public void LoadFromAssemblyPath_BclAssembly_Succeeds()
    {
        using var loader = new MetadataLoader();
        var asm = loader.LoadFromAssemblyPath(Path.Combine(RuntimeDir, "System.Linq.dll"));

        Assert.NotNull(asm);
        Assert.Contains(asm.GetTypes(), t => t.FullName == "System.Linq.Enumerable");
    }

    [Fact]
    public void Dispose_AfterLoad_DoesNotThrow()
    {
        var loader = new MetadataLoader();
        loader.LoadFromAssemblyPath(Path.Combine(RuntimeDir, "System.Linq.dll"));
        loader.Dispose();
        // Second Dispose should also be safe.
        loader.Dispose();
    }

    [Fact]
    public void IsManagedAssembly_RealBclDll_ReturnsTrue()
    {
        Assert.True(MetadataLoader.IsManagedAssembly(
            Path.Combine(RuntimeDir, "System.Linq.dll")));
    }

    [Fact]
    public void IsManagedAssembly_MissingFile_ReturnsFalse()
    {
        Assert.False(MetadataLoader.IsManagedAssembly(
            Path.Combine(RuntimeDir, "does-not-exist-" + Guid.NewGuid().ToString("N") + ".dll")));
    }

    [Fact]
    public void Constructor_DedupesSearchPathsByFileName_FirstWins()
    {
        // Pre-fix regression: when two search dirs each contained a DLL with
        // the same filename (e.g. runtime's mscorlib.dll and a Unity-shipped
        // mscorlib facade pulled in via <Reference HintPath>), both paths
        // ended up in the resolver. PathAssemblyResolver then loaded both
        // during MetadataLoadContext's core-assembly probe, and the second
        // raised FileLoadException("already been loaded") because two
        // assemblies cannot share a simple name in one context.
        //
        // The fix dedupes _searchPaths by filename (first wins). This test
        // pins that property directly: a duplicate filename in a later dir
        // is rejected. We can't easily reproduce the original crash because
        // it requires two same-named-but-different-MVID assemblies, but the
        // dedup property is what guards the resolver from ever seeing them.
        var dirA = Path.Combine(
            Path.GetTempPath(),
            "hoogle-loader-tests-" + Guid.NewGuid().ToString("N") + "-a");
        var dirB = Path.Combine(
            Path.GetTempPath(),
            "hoogle-loader-tests-" + Guid.NewGuid().ToString("N") + "-b");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);
        try
        {
            // Use real managed DLLs (need .HasMetadata-valid files because
            // the resolver may probe them later). Different sources so the
            // two paths' contents differ; same filename to force a collision.
            var srcA = Path.Combine(RuntimeDir, "System.Linq.dll");
            var srcB = Path.Combine(RuntimeDir, "System.Console.dll");
            var pathA = Path.Combine(dirA, "Shared.dll");
            var pathB = Path.Combine(dirB, "Shared.dll");
            File.Copy(srcA, pathA);
            File.Copy(srcB, pathB);

            // includeRuntimeDir=true so the context can find a core assembly;
            // the dedup we want to verify is between dirA and dirB.
            using var loader = new MetadataLoader(new[] { dirA, dirB });

            // First-wins: only dirA's Shared.dll path is kept; dirB's is dropped.
            Assert.Contains(pathA, loader.SearchPaths);
            Assert.DoesNotContain(pathB, loader.SearchPaths);
        }
        finally
        {
            Directory.Delete(dirA, recursive: true);
            Directory.Delete(dirB, recursive: true);
        }
    }

    [Fact]
    public void Constructor_RuntimeDirWinsOverExtraDirs_OnFileNameCollision()
    {
        // The runtime dir is walked first so its DLLs always survive
        // collisions with extra search dirs. Pins ordering: even if a Unity
        // package ships its own copy of a runtime-named DLL, the runtime's
        // copy wins.
        var coreSrc = Path.Combine(RuntimeDir, "System.Linq.dll");
        var extraDir = Path.Combine(
            Path.GetTempPath(),
            "hoogle-loader-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extraDir);
        try
        {
            var extraPath = Path.Combine(extraDir, "System.Linq.dll");
            File.Copy(coreSrc, extraPath);

            using var loader = new MetadataLoader(new[] { extraDir });

            Assert.Contains(coreSrc, loader.SearchPaths);
            Assert.DoesNotContain(extraPath, loader.SearchPaths);
        }
        finally
        {
            Directory.Delete(extraDir, recursive: true);
        }
    }
}
