using CSharpHoogle.Cli;

namespace CSharpHoogle.Cli.Tests;

public class CompileItemEnumeratorTests : IDisposable
{
    private readonly string _tempDir;

    public CompileItemEnumeratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hoogle-compileenum-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private string WriteCsproj(string projDir, string body)
    {
        Directory.CreateDirectory(projDir);
        var csproj = Path.Combine(projDir, "MyApp.csproj");
        File.WriteAllText(csproj, body);
        return csproj;
    }

    private static void WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// stub\n");
    }

    [Fact]
    public void RecursiveGlobNoSeparator_PicksUpAllCsFiles()
    {
        var projDir = Path.Combine(_tempDir, "proj");
        var csproj = WriteCsproj(projDir, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <Compile Include="**/*.cs" />
              </ItemGroup>
            </Project>
            """);
        WriteFile(Path.Combine(projDir, "A.cs"));
        WriteFile(Path.Combine(projDir, "sub", "B.cs"));
        WriteFile(Path.Combine(projDir, "sub", "deeper", "C.cs"));

        var result = CompileItemEnumerator.Enumerate(csproj);

        Assert.Equal(3, result.Count);
        Assert.Contains(Path.GetFullPath(Path.Combine(projDir, "A.cs")), result);
        Assert.Contains(Path.GetFullPath(Path.Combine(projDir, "sub", "B.cs")), result);
        Assert.Contains(Path.GetFullPath(Path.Combine(projDir, "sub", "deeper", "C.cs")), result);
    }

    [Fact]
    public void RecursiveGlobWithLiteralPrefix_FiltersByDirectory()
    {
        var projDir = Path.Combine(_tempDir, "proj");
        var csproj = WriteCsproj(projDir, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <Compile Include="Generated/**/*.cs" />
              </ItemGroup>
            </Project>
            """);
        WriteFile(Path.Combine(projDir, "Other.cs"));
        WriteFile(Path.Combine(projDir, "Generated", "X.cs"));
        WriteFile(Path.Combine(projDir, "Generated", "deeper", "Y.cs"));

        var result = CompileItemEnumerator.Enumerate(csproj);

        Assert.Equal(2, result.Count);
        Assert.Contains(Path.GetFullPath(Path.Combine(projDir, "Generated", "X.cs")), result);
        Assert.Contains(Path.GetFullPath(Path.Combine(projDir, "Generated", "deeper", "Y.cs")), result);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(projDir, "Other.cs")), result);
    }

    [Fact]
    public void RecursiveGlobWithNestedDirectoryAfterStarStar_MatchesOnlyMatchingPath()
    {
        // This is the case that used to be broken: "**/Foo/*.cs" should
        // match .cs files inside ANY directory named "Foo" at any depth,
        // and nothing else.
        var projDir = Path.Combine(_tempDir, "proj");
        var csproj = WriteCsproj(projDir, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <Compile Include="**/Foo/*.cs" />
              </ItemGroup>
            </Project>
            """);
        // Should match:
        WriteFile(Path.Combine(projDir, "Foo", "a.cs"));
        WriteFile(Path.Combine(projDir, "src", "a", "b", "Foo", "x.cs"));
        // Should NOT match:
        WriteFile(Path.Combine(projDir, "Foo.cs"));
        WriteFile(Path.Combine(projDir, "src", "Bar", "y.cs"));
        WriteFile(Path.Combine(projDir, "Foo", "nested", "deep.cs"));

        var result = CompileItemEnumerator.Enumerate(csproj);

        Assert.Contains(Path.GetFullPath(Path.Combine(projDir, "Foo", "a.cs")), result);
        Assert.Contains(Path.GetFullPath(Path.Combine(projDir, "src", "a", "b", "Foo", "x.cs")), result);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(projDir, "Foo.cs")), result);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(projDir, "src", "Bar", "y.cs")), result);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(projDir, "Foo", "nested", "deep.cs")), result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void DefaultGlob_ExcludesUnityManagedTrees()
    {
        var projDir = Path.Combine(_tempDir, "proj");
        // No explicit Compile items — exercises the SDK default glob.
        var csproj = WriteCsproj(projDir, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        // Files that should be kept:
        WriteFile(Path.Combine(projDir, "Real.cs"));
        WriteFile(Path.Combine(projDir, "Assets", "Scripts", "Player.cs"));

        // Files that must be excluded:
        WriteFile(Path.Combine(projDir, "obj", "Debug.cs"));
        WriteFile(Path.Combine(projDir, "bin", "Out.cs"));
        WriteFile(Path.Combine(projDir, "Library", "PackageCache", "thing", "Generated.cs"));
        WriteFile(Path.Combine(projDir, "Temp", "Bee", "scratch.cs"));
        WriteFile(Path.Combine(projDir, "Packages", "com.unity.foo", "Runtime", "Foo.cs"));
        WriteFile(Path.Combine(projDir, "ProjectSettings", "Generated.cs"));

        var result = CompileItemEnumerator.Enumerate(csproj);

        Assert.Contains(Path.GetFullPath(Path.Combine(projDir, "Real.cs")), result);
        Assert.Contains(Path.GetFullPath(Path.Combine(projDir, "Assets", "Scripts", "Player.cs")), result);

        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(projDir, "obj", "Debug.cs")), result);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(projDir, "bin", "Out.cs")), result);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(projDir, "Library", "PackageCache", "thing", "Generated.cs")), result);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(projDir, "Temp", "Bee", "scratch.cs")), result);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(projDir, "Packages", "com.unity.foo", "Runtime", "Foo.cs")), result);
        Assert.DoesNotContain(Path.GetFullPath(Path.Combine(projDir, "ProjectSettings", "Generated.cs")), result);
    }
}
