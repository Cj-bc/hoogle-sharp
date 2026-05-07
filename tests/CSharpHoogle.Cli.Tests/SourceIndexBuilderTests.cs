using CSharpHoogle.Cli;

namespace CSharpHoogle.Cli.Tests;

public class SourceIndexBuilderTests : IDisposable
{
    private readonly string _tempDir;

    public SourceIndexBuilderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "hoogle-source-tests-" + Guid.NewGuid().ToString("N"));
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
    public void BuildFromCsproj_PureCSharp_FindsPublicMethodsViaDefaultGlob()
    {
        var csproj = Path.Combine(_tempDir, "App.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(_tempDir, "Greeter.cs"), """
            namespace App;

            public class Greeter
            {
                public string Hello(string name) => $"Hi {name}";

                private string Internal_(int x) => x.ToString();
            }
            """);

        var methods = SourceIndexBuilder.BuildFromCsproj(csproj, "App");

        var hello = Assert.Single(methods, m => m.FullName == "App.Greeter.Hello");
        Assert.Equal("string", hello.ReturnType);
        Assert.Equal(new[] { "string" }, hello.ParameterTypes);
        Assert.Equal("source", hello.Source.Kind);
        Assert.Equal("App", hello.Source.Name);

        // Private method must be skipped.
        Assert.DoesNotContain(methods, m => m.FullName == "App.Greeter.Internal_");
    }

    [Fact]
    public void BuildFromCsproj_RespectsExplicitCompileItems()
    {
        // Mimic the Unity/Godot pattern where the csproj lists each .cs file
        // explicitly. Files outside the listed set must NOT be picked up.
        var included = Path.Combine(_tempDir, "Included.cs");
        var excluded = Path.Combine(_tempDir, "Excluded.cs");
        File.WriteAllText(included, """
            namespace Game;
            public class Included { public void Tick() {} }
            """);
        File.WriteAllText(excluded, """
            namespace Game;
            public class Excluded { public void NeverIndexed() {} }
            """);

        var csproj = Path.Combine(_tempDir, "Game.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <Compile Include="Included.cs" />
              </ItemGroup>
            </Project>
            """);

        var methods = SourceIndexBuilder.BuildFromCsproj(csproj, "Game");

        Assert.Contains(methods, m => m.FullName == "Game.Included.Tick");
        Assert.DoesNotContain(methods, m => m.FullName == "Game.Excluded.NeverIndexed");
    }

    [Fact]
    public void BuildFromCsproj_DefaultGlob_ExcludesObjAndBin()
    {
        var csproj = Path.Combine(_tempDir, "Excl.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);

        File.WriteAllText(Path.Combine(_tempDir, "Real.cs"), """
            namespace Excl;
            public class Real { public void Keep() {} }
            """);

        var objDir = Path.Combine(_tempDir, "obj");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "Generated.cs"), """
            namespace Excl;
            public class Generated { public void Drop() {} }
            """);

        var binDir = Path.Combine(_tempDir, "bin", "Debug");
        Directory.CreateDirectory(binDir);
        File.WriteAllText(Path.Combine(binDir, "Cached.cs"), """
            namespace Excl;
            public class Cached { public void Drop() {} }
            """);

        var methods = SourceIndexBuilder.BuildFromCsproj(csproj, "Excl");

        Assert.Contains(methods, m => m.FullName == "Excl.Real.Keep");
        Assert.DoesNotContain(methods, m => m.FullName == "Excl.Generated.Drop");
        Assert.DoesNotContain(methods, m => m.FullName == "Excl.Cached.Drop");
    }

    [Fact]
    public void BuildFromCsproj_ExtensionMethod_FlaggedAsExtension()
    {
        var csproj = Path.Combine(_tempDir, "Ext.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Ext.cs"), """
            namespace Ext;
            public static class StringExtensions
            {
                public static int Wordcount(this string s) => s.Split(' ').Length;
            }
            """);

        var methods = SourceIndexBuilder.BuildFromCsproj(csproj, "Ext");
        var word = Assert.Single(methods, m => m.FullName == "Ext.StringExtensions.Wordcount");
        Assert.True(word.IsExtensionMethod);
    }

    [Fact]
    public void BuildFromCsproj_GenericMethod_CapturesTypeParameters()
    {
        var csproj = Path.Combine(_tempDir, "Gen.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Box.cs"), """
            namespace Gen;
            public class Box
            {
                public T Identity<T>(T value) => value;
            }
            """);

        var methods = SourceIndexBuilder.BuildFromCsproj(csproj, "Gen");
        var id = Assert.Single(methods, m => m.FullName == "Gen.Box.Identity");
        Assert.Equal(new[] { "T" }, id.GenericParams);
        Assert.Equal("T", id.ReturnType);
        Assert.Equal(new[] { "T" }, id.ParameterTypes);
    }

    [Fact]
    public void BuildFromCsproj_OptionalParameters_TrackedAsRequiredCount()
    {
        var csproj = Path.Combine(_tempDir, "Opt.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Opt.cs"), """
            namespace Opt;
            public class Service
            {
                public void Send(string message, int retries = 3, bool urgent = false) {}
            }
            """);

        var methods = SourceIndexBuilder.BuildFromCsproj(csproj, "Opt");
        var send = Assert.Single(methods, m => m.FullName == "Opt.Service.Send");
        Assert.Equal(3, send.ParameterTypes.Length);
        Assert.Equal(1, send.RequiredParameterCount);
    }

    [Fact]
    public void BuildFromCsproj_SummaryDocComment_Captured()
    {
        var csproj = Path.Combine(_tempDir, "Doc.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Doc.cs"), """
            namespace Doc;
            public class Service
            {
                /// <summary>
                /// Sends a thing.
                /// </summary>
                public void Send() {}
            }
            """);

        var methods = SourceIndexBuilder.BuildFromCsproj(csproj, "Doc");
        var send = Assert.Single(methods, m => m.FullName == "Doc.Service.Send");
        Assert.Equal("Sends a thing.", send.Summary);
    }

    [Fact]
    public void BuildFromCsproj_InstanceMethod_RecordsDeclaringType()
    {
        var csproj = Path.Combine(_tempDir, "App.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Bag.cs"), """
            namespace App;
            public class Bag<TKey, TValue>
            {
                public bool ContainsKey(TKey key) => false;
                public static Bag<TKey, TValue> Empty() => new();
            }
            """);

        var methods = SourceIndexBuilder.BuildFromCsproj(csproj, "App");

        var contains = Assert.Single(methods, m => m.FullName == "App.Bag.ContainsKey");
        Assert.Equal("Bag<TKey, TValue>", contains.DeclaringType);
        Assert.Equal(new[] { "TKey", "TValue" }, contains.TypeGenericParams);

        // Static factory: receiver suppressed.
        var empty = Assert.Single(methods, m => m.FullName == "App.Bag.Empty");
        Assert.Null(empty.DeclaringType);
        Assert.Empty(empty.TypeGenericParams!);
    }

    [Fact]
    public void BuildFromCsproj_ExtensionMethod_LeavesDeclaringTypeNull()
    {
        // Extension methods already place the receiver in parameter[0]; the
        // matcher's synthetic-receiver slot must NOT also fire for them, so
        // DeclaringType stays null.
        var csproj = Path.Combine(_tempDir, "App.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_tempDir, "Ext.cs"), """
            namespace App;
            public static class StringExtensions
            {
                public static string Shout(this string s) => s.ToUpper();
            }
            """);

        var methods = SourceIndexBuilder.BuildFromCsproj(csproj, "App");
        var shout = Assert.Single(methods, m => m.FullName == "App.StringExtensions.Shout");
        Assert.True(shout.IsExtensionMethod);
        Assert.Null(shout.DeclaringType);
    }
}
