using CSharpHoogle.Cli;

namespace CSharpHoogle.Cli.Tests;

public class NuGetForUnityReaderTests : IDisposable
{
    private readonly string _tempDir;

    public NuGetForUnityReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hoogle-cli-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void Read_PicksClosestTfm_AndIncludesAdjacentXml()
    {
        // Unity-shaped tree:
        //   tempDir/UnityProj/
        //     Assembly-CSharp.csproj  (origin)
        //     Assets/packages.config
        //     Assets/Packages/Newtonsoft.Json.13.0.3/lib/net45/Newtonsoft.Json.dll  (should NOT be picked)
        //     Assets/Packages/Newtonsoft.Json.13.0.3/lib/netstandard2.1/Newtonsoft.Json.dll
        //     Assets/Packages/Newtonsoft.Json.13.0.3/lib/netstandard2.1/Newtonsoft.Json.xml
        //     Assets/Packages/Serilog.2.0.0/lib/netstandard2.0/Serilog.dll  (only ns2.0 — picked via fallback)
        var unityProj = Path.Combine(_tempDir, "UnityProj");
        var assetsDir = Path.Combine(unityProj, "Assets");
        var packagesDir = Path.Combine(assetsDir, "Packages");
        Directory.CreateDirectory(packagesDir);

        var csproj = Path.Combine(unityProj, "Assembly-CSharp.csproj");
        File.WriteAllText(csproj, """
            <Project>
              <PropertyGroup><TargetFramework>netstandard2.1</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(assetsDir, "packages.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <packages>
              <package id="Newtonsoft.Json" version="13.0.3" />
              <package id="Serilog" version="2.0.0" />
            </packages>
            """);

        var njNet45Dll = Path.Combine(packagesDir, "Newtonsoft.Json.13.0.3", "lib", "net45", "Newtonsoft.Json.dll");
        var njNs21Dll = Path.Combine(packagesDir, "Newtonsoft.Json.13.0.3", "lib", "netstandard2.1", "Newtonsoft.Json.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(njNet45Dll)!);
        Directory.CreateDirectory(Path.GetDirectoryName(njNs21Dll)!);
        File.WriteAllBytes(njNet45Dll, Array.Empty<byte>());
        File.WriteAllBytes(njNs21Dll, Array.Empty<byte>());
        File.WriteAllText(Path.ChangeExtension(njNs21Dll, ".xml"), "<doc/>");

        var serilogDll = Path.Combine(packagesDir, "Serilog.2.0.0", "lib", "netstandard2.0", "Serilog.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(serilogDll)!);
        File.WriteAllBytes(serilogDll, Array.Empty<byte>());

        var ctx = new ProjectContext("netstandard2.1", SdkKind.Default, csproj);
        var messages = new List<string>();

        var result = NuGetForUnityReader.Read(ctx, messages.Add);

        Assert.Single(result.ManifestPaths);

        var nj = Assert.Single(result.Entries, e => e.Source.Name == "Newtonsoft.Json");
        Assert.Equal(njNs21Dll, nj.AssemblyPath); // exact-TFM match wins over net45
        Assert.NotNull(nj.XmlPath);
        Assert.Equal("package", nj.Source.Kind);

        var s = Assert.Single(result.Entries, e => e.Source.Name == "Serilog");
        Assert.Equal(serilogDll, s.AssemblyPath); // ns2.0 fallback
        Assert.Null(s.XmlPath);

        Assert.Contains(messages, m => m.Contains("Indexing 2 NuGetForUnity packages"));
    }

    [Fact]
    public void Read_NoOp_WhenNoPackagesConfigFound()
    {
        var projDir = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(projDir);
        var csproj = Path.Combine(projDir, "MyApp.csproj");
        File.WriteAllText(csproj, "<Project/>");

        var ctx = new ProjectContext("net8.0", SdkKind.Default, csproj);
        var messages = new List<string>();

        var result = NuGetForUnityReader.Read(ctx, messages.Add);

        Assert.Empty(result.Entries);
        Assert.Empty(result.ManifestPaths);
        Assert.Empty(messages);
    }
}
