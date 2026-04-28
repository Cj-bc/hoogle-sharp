using System.Text.Json;
using CSharpHoogle.Cli;

namespace CSharpHoogle.Cli.Tests;

public class NuGetAssetsReaderTests : IDisposable
{
    private readonly string _tempDir;

    public NuGetAssetsReaderTests()
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
    public void Read_ResolvesPackages_AndSkipsPlaceholdersAndUsesRuntimeFallback()
    {
        var projDir = Path.Combine(_tempDir, "proj");
        var objDir = Path.Combine(projDir, "obj");
        var nugetDir = Path.Combine(_tempDir, "nuget");
        Directory.CreateDirectory(objDir);

        var csproj = Path.Combine(projDir, "MyApp.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        // Newtonsoft.Json with compile path + adjacent xml
        var njDll = Path.Combine(nugetDir, "newtonsoft.json", "13.0.3", "lib", "net6.0", "Newtonsoft.Json.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(njDll)!);
        File.WriteAllBytes(njDll, Array.Empty<byte>());
        File.WriteAllText(Path.ChangeExtension(njDll, ".xml"), "<doc/>");

        // Runtime-only package — no compile section, falls back to runtime
        var orDll = Path.Combine(nugetDir, "onlyruntime", "1.0.0", "lib", "net8.0", "OnlyRuntime.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(orDll)!);
        File.WriteAllBytes(orDll, Array.Empty<byte>());

        var assets = new
        {
            version = 3,
            targets = new Dictionary<string, object>
            {
                ["net8.0"] = new Dictionary<string, object>
                {
                    ["Newtonsoft.Json/13.0.3"] = new
                    {
                        type = "package",
                        compile = new Dictionary<string, object>
                        {
                            ["lib/net6.0/Newtonsoft.Json.dll"] = new { },
                        },
                    },
                    ["PlaceholderPackage/1.0.0"] = new
                    {
                        type = "package",
                        compile = new Dictionary<string, object>
                        {
                            ["lib/net8.0/_._"] = new { },
                        },
                    },
                    ["OnlyRuntime/1.0.0"] = new
                    {
                        type = "package",
                        runtime = new Dictionary<string, object>
                        {
                            ["lib/net8.0/OnlyRuntime.dll"] = new { },
                        },
                    },
                },
            },
            libraries = new Dictionary<string, object>
            {
                ["Newtonsoft.Json/13.0.3"] = new { type = "package", path = "newtonsoft.json/13.0.3" },
                ["PlaceholderPackage/1.0.0"] = new { type = "package", path = "placeholderpackage/1.0.0" },
                ["OnlyRuntime/1.0.0"] = new { type = "package", path = "onlyruntime/1.0.0" },
            },
            packageFolders = new Dictionary<string, object>
            {
                [nugetDir + Path.DirectorySeparatorChar] = new { },
            },
        };
        File.WriteAllText(Path.Combine(objDir, "project.assets.json"), JsonSerializer.Serialize(assets));

        var ctx = new ProjectContext("net8.0", SdkKind.Default, csproj);
        var messages = new List<string>();

        var result = NuGetAssetsReader.Read(ctx, messages.Add);

        Assert.Single(result.ManifestPaths);
        var names = result.Entries.Select(e => e.SourceName).OrderBy(s => s).ToList();
        Assert.Contains("Newtonsoft.Json", names);
        Assert.Contains("OnlyRuntime", names);
        Assert.DoesNotContain("PlaceholderPackage", names);

        var nj = result.Entries.First(e => e.SourceName == "Newtonsoft.Json");
        Assert.Equal(njDll, nj.AssemblyPath);
        Assert.NotNull(nj.XmlPath);
        Assert.Equal("package", nj.SourceKind);

        var or = result.Entries.First(e => e.SourceName == "OnlyRuntime");
        Assert.Equal(orDll, or.AssemblyPath);
        Assert.Null(or.XmlPath);

        Assert.Contains(messages, m => m.Contains("Indexing 2 NuGet packages"));
    }

    [Fact]
    public void Read_WarnsAboutMissingRestore_WhenCsprojHasPackageReference()
    {
        var projDir = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(projDir);
        var csproj = Path.Combine(projDir, "MyApp.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
              </ItemGroup>
            </Project>
            """);

        var ctx = new ProjectContext("net8.0", SdkKind.Default, csproj);
        var messages = new List<string>();

        var result = NuGetAssetsReader.Read(ctx, messages.Add);

        Assert.Empty(result.Entries);
        Assert.Empty(result.ManifestPaths);
        Assert.Contains(messages, m => m.Contains("dotnet restore"));
    }

    [Fact]
    public void Read_NoWarning_WhenCsprojHasNoPackageReferenceAndNoAssetsJson()
    {
        var projDir = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(projDir);
        var csproj = Path.Combine(projDir, "MyApp.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        var ctx = new ProjectContext("net8.0", SdkKind.Default, csproj);
        var messages = new List<string>();

        var result = NuGetAssetsReader.Read(ctx, messages.Add);

        Assert.Empty(result.Entries);
        Assert.Empty(messages);
    }
}
