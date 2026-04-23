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
}
