using CSharpHoogle.Cli;

namespace CSharpHoogle.Cli.Tests;

public class ProjectReferenceResolverTests : IDisposable
{
    private readonly string _tempDir;

    public ProjectReferenceResolverTests()
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
    public void Resolve_PrefersDebugOverRelease_AndReadsAssemblyName()
    {
        var hostDir = Path.Combine(_tempDir, "Host");
        var libDir = Path.Combine(_tempDir, "Lib");
        Directory.CreateDirectory(hostDir);
        Directory.CreateDirectory(libDir);

        var hostCsproj = Path.Combine(hostDir, "Host.csproj");
        File.WriteAllText(hostCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);

        var libCsproj = Path.Combine(libDir, "Lib.csproj");
        File.WriteAllText(libCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <AssemblyName>MyLib</AssemblyName>
              </PropertyGroup>
            </Project>
            """);

        var debugDll = Path.Combine(libDir, "bin", "Debug", "net8.0", "MyLib.dll");
        var releaseDll = Path.Combine(libDir, "bin", "Release", "net8.0", "MyLib.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(debugDll)!);
        Directory.CreateDirectory(Path.GetDirectoryName(releaseDll)!);
        File.WriteAllBytes(debugDll, Array.Empty<byte>());
        File.WriteAllBytes(releaseDll, Array.Empty<byte>());
        File.WriteAllText(Path.ChangeExtension(debugDll, ".xml"), "<doc/>");

        var ctx = new ProjectContext("net8.0", SdkKind.Default, hostCsproj);
        var messages = new List<string>();

        var entries = ProjectReferenceResolver.Resolve(ctx, messages.Add);

        var entry = Assert.Single(entries);
        Assert.Equal(debugDll, entry.AssemblyPath);
        Assert.Equal("project", entry.SourceKind);
        Assert.Equal("MyLib", entry.SourceName);
        Assert.NotNull(entry.XmlPath);
        Assert.Empty(messages);
    }

    [Fact]
    public void Resolve_FallsBackToRelease_WhenDebugMissing()
    {
        var hostDir = Path.Combine(_tempDir, "Host");
        var libDir = Path.Combine(_tempDir, "Lib");
        Directory.CreateDirectory(hostDir);
        Directory.CreateDirectory(libDir);

        File.WriteAllText(Path.Combine(hostDir, "Host.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var releaseDll = Path.Combine(libDir, "bin", "Release", "net8.0", "Lib.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(releaseDll)!);
        File.WriteAllBytes(releaseDll, Array.Empty<byte>());

        var ctx = new ProjectContext("net8.0", SdkKind.Default, Path.Combine(hostDir, "Host.csproj"));

        var entries = ProjectReferenceResolver.Resolve(ctx, _ => { });

        var entry = Assert.Single(entries);
        Assert.Equal(releaseDll, entry.AssemblyPath);
        Assert.Equal("Lib", entry.SourceName); // falls back to filename when AssemblyName missing
    }

    [Fact]
    public void Resolve_WarnsAndSkips_WhenNoBuiltOutput()
    {
        var hostDir = Path.Combine(_tempDir, "Host");
        var libDir = Path.Combine(_tempDir, "Lib");
        Directory.CreateDirectory(hostDir);
        Directory.CreateDirectory(libDir);

        File.WriteAllText(Path.Combine(hostDir, "Host.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var ctx = new ProjectContext("net8.0", SdkKind.Default, Path.Combine(hostDir, "Host.csproj"));
        var messages = new List<string>();

        var entries = ProjectReferenceResolver.Resolve(ctx, messages.Add);

        Assert.Empty(entries);
        Assert.Contains(messages, m => m.Contains("Skipping ProjectReference Lib"));
    }
}
