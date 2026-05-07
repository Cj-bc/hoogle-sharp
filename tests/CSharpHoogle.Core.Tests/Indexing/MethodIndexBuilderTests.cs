using CSharpHoogle.Core.Indexing;
using CSharpHoogle.Core.Models;
using CSharpHoogle.Core.Reflection;
using CSharpHoogle.Core.Storage;

namespace CSharpHoogle.Core.Tests.Indexing;

public class MethodIndexBuilderTests
{
    private static string SystemLinqDll =>
        Path.Combine(
            Path.GetDirectoryName(typeof(object).Assembly.Location)!,
            "System.Linq.dll");

    [Fact]
    public void BuildFromAssembly_SystemLinq_FindsEnumerableSelect()
    {
        using var loader = new MetadataLoader();
        var asm = loader.LoadFromAssemblyPath(SystemLinqDll);
        var docs = new InMemoryDocEntryRepository();

        var entries = MethodIndexBuilder.BuildFromAssembly(asm, docs);

        var selects = entries
            .Where(e => e.FullName == "System.Linq.Enumerable.Select")
            .ToList();

        Assert.NotEmpty(selects);
    }

    [Fact]
    public void BuildFromAssembly_EnumerableSelect_IsExtensionMethod()
    {
        using var loader = new MetadataLoader();
        var asm = loader.LoadFromAssemblyPath(SystemLinqDll);
        var docs = new InMemoryDocEntryRepository();

        var entries = MethodIndexBuilder.BuildFromAssembly(asm, docs);

        var select = entries.FirstOrDefault(e =>
            e.FullName == "System.Linq.Enumerable.Select" &&
            e.GenericParams.Length == 2 &&
            e.ParameterTypes.Length == 2);

        Assert.NotNull(select);
        Assert.True(select!.IsExtensionMethod);
    }

    [Fact]
    public void BuildFromAssembly_GenericMethod_ExposesGenericParams()
    {
        using var loader = new MetadataLoader();
        var asm = loader.LoadFromAssemblyPath(SystemLinqDll);
        var docs = new InMemoryDocEntryRepository();

        var entries = MethodIndexBuilder.BuildFromAssembly(asm, docs);

        var select = entries.First(e =>
            e.FullName == "System.Linq.Enumerable.Select" &&
            e.GenericParams.Length == 2);

        // Enumerable.Select<TSource, TResult>(...) — the names come from the metadata,
        // which preserves the original TSource / TResult identifiers.
        Assert.Contains("TSource", select.GenericParams);
        Assert.Contains("TResult", select.GenericParams);
    }

    [Fact]
    public void BuildFromAssembly_WithDocRepo_AttachesDocEntry()
    {
        using var loader = new MetadataLoader();
        var asm = loader.LoadFromAssemblyPath(SystemLinqDll);

        // Seed the repo with a hand-crafted DocEntry whose key matches what
        // LoxSmoke.DocXml will generate for Enumerable.Select<TSource,TResult>.
        var docs = new InMemoryDocEntryRepository();
        const string selectKey =
            "M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})";
        docs.Store(new[]
        {
            new DocEntry(
                MemberKey: selectKey,
                Summary: "SENTINEL SUMMARY",
                Returns: null,
                Params: new Dictionary<string, string>(),
                Remarks: null,
                Example: null),
        });

        var entries = MethodIndexBuilder.BuildFromAssembly(asm, docs);

        var attached = entries.FirstOrDefault(e => e.Doc?.Summary == "SENTINEL SUMMARY");
        Assert.NotNull(attached);
        Assert.Equal("System.Linq.Enumerable.Select", attached!.FullName);
    }

    [Fact]
    public void BuildFromAssembly_DocUrl_PopulatedForBclMember()
    {
        using var loader = new MetadataLoader();
        var asm = loader.LoadFromAssemblyPath(SystemLinqDll);
        var docs = new InMemoryDocEntryRepository();

        var entries = MethodIndexBuilder.BuildFromAssembly(asm, docs);

        var select = entries.First(e => e.FullName == "System.Linq.Enumerable.Select");

        Assert.StartsWith("https://learn.microsoft.com/dotnet/api/system.linq.enumerable.select", select.DocUrl);
    }

    [Fact]
    public void BuildFromAssembly_Path_CreatesThrowawayLoaderWhenNoneProvided()
    {
        var docs = new InMemoryDocEntryRepository();
        var entries = MethodIndexBuilder.BuildFromAssembly(SystemLinqDll, docs);

        Assert.NotEmpty(entries);
        Assert.Contains(entries, e => e.FullName == "System.Linq.Enumerable.Select");
    }

    [Fact]
    public void BuildFromAssembly_InstanceMethod_HasDeclaringTypeAndTypeGenerics()
    {
        // Pick a stable, well-known instance method on a generic BCL type.
        // List<T>.Contains lives in System.Private.CoreLib alongside
        // typeof(object), so we don't have to guess at ref-assembly forwarding.
        var coreLib = typeof(object).Assembly.Location;
        using var loader = new MetadataLoader();
        var asm = loader.LoadFromAssemblyPath(coreLib);
        var docs = new InMemoryDocEntryRepository();

        var entries = MethodIndexBuilder.BuildFromAssembly(asm, docs);

        var contains = entries.FirstOrDefault(e =>
            e.FullName == "System.Collections.Generic.List.Contains");
        Assert.NotNull(contains);
        Assert.NotNull(contains!.DeclaringType);
        Assert.Equal("List`1", contains.DeclaringType!.Name);
        Assert.Contains("T", contains.TypeGenericParams!);
    }

    [Fact]
    public void BuildFromAssembly_ExtensionMethod_DeclaringTypeNull()
    {
        // Enumerable.Select is a static extension method; the receiver is
        // already parameter[0], so DeclaringType must stay null to keep the
        // matcher from doubling up.
        using var loader = new MetadataLoader();
        var asm = loader.LoadFromAssemblyPath(SystemLinqDll);
        var docs = new InMemoryDocEntryRepository();

        var entries = MethodIndexBuilder.BuildFromAssembly(asm, docs);

        var select = entries.First(e =>
            e.FullName == "System.Linq.Enumerable.Select" && e.IsExtensionMethod);
        Assert.Null(select.DeclaringType);
        Assert.Empty(select.TypeGenericParams!);
    }

    [Fact]
    public void BuildFromAssembly_SkipsMethodsWithUnresolvableSignatureReferences()
    {
        // Pre-fix regression: when a dep DLL exposes a method whose signature
        // references an assembly missing from the resolver's search paths,
        // signature decoding inside XmlDocId.MethodId / GetParameters threw
        // FileNotFoundException and crashed the entire dep walk. The fix
        // wraps BuildEntry per-method so just that method is dropped.
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "hoogle-mib-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var libDir = Path.Combine(tempDir, "Lib");
            Directory.CreateDirectory(libDir);
            File.WriteAllText(Path.Combine(libDir, "Lib.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName>Lib</AssemblyName>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(libDir, "LibType.cs"), """
                namespace Lib;
                public class LibType { }
                """);

            var consumerDir = Path.Combine(tempDir, "Consumer");
            Directory.CreateDirectory(consumerDir);
            File.WriteAllText(Path.Combine(consumerDir, "Consumer.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                    <AssemblyName>Consumer</AssemblyName>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\Lib\Lib.csproj" />
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(consumerDir, "Bar.cs"), """
                using Lib;
                namespace Consumer;
                public class Bar
                {
                    public void Use(LibType t) { }
                    public int Plain() => 42;
                }
                """);

            if (!RunDotnetBuild(consumerDir))
            {
                // dotnet build is environment-dependent; skip the assertion
                // when it can't run rather than failing in CI without dotnet.
                return;
            }

            // Copy ONLY Consumer.dll to an isolated dir so the resolver can't
            // find Lib.dll alongside it. Mirrors the real-world failure mode:
            // a NuGet package's DLL references an assembly we don't index.
            var isolatedDir = Path.Combine(tempDir, "isolated");
            Directory.CreateDirectory(isolatedDir);
            var consumerDllSrc = Path.Combine(
                consumerDir, "bin", "Debug", "net8.0", "Consumer.dll");
            var isolatedDll = Path.Combine(isolatedDir, "Consumer.dll");
            File.Copy(consumerDllSrc, isolatedDll);

            using var loader = new MetadataLoader();
            var asm = loader.LoadFromAssemblyPath(isolatedDll);
            var docs = new InMemoryDocEntryRepository();

            // Pre-fix: this throws FileNotFoundException for Bar.Use(LibType).
            var entries = MethodIndexBuilder.BuildFromAssembly(asm, docs);

            // Plain() doesn't reference Lib, so it survives.
            // Use(LibType) is dropped because its signature can't be decoded.
            Assert.Contains(entries, e => e.FullName == "Consumer.Bar.Plain");
            Assert.DoesNotContain(entries, e => e.FullName == "Consumer.Bar.Use");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static bool RunDotnetBuild(string projectDir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", "build -c Debug")
        {
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode == 0;
    }
}
