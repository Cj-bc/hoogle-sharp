using CSharpHoogle.Core.Indexing;

namespace CSharpHoogle.Core.Tests.Indexing;

public class DocUrlResolverTests
{
    [Fact]
    public void Resolve_BclMethodKey_ReturnsLearnUrl()
    {
        var url = DocUrlResolver.Resolve("M:System.String.IndexOf(System.Char)");
        Assert.Equal("https://learn.microsoft.com/dotnet/api/system.string.indexof", url);
    }

    [Fact]
    public void Resolve_GenericTypeKey_ReplacesBacktickWithDash()
    {
        var url = DocUrlResolver.Resolve("T:System.Collections.Generic.List`1");
        Assert.Equal("https://learn.microsoft.com/dotnet/api/system.collections.generic.list-1", url);
    }

    [Fact]
    public void Resolve_GenericMethodKey_ReplacesDoubleBacktickWithDash()
    {
        var url = DocUrlResolver.Resolve(
            "M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{``0},System.Func{``0,``1})");
        Assert.Equal("https://learn.microsoft.com/dotnet/api/system.linq.enumerable.select-2", url);
    }

    [Fact]
    public void Resolve_MicrosoftExtensionsKey_ReturnsLearnUrl()
    {
        var url = DocUrlResolver.Resolve(
            "M:Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(...)");
        Assert.StartsWith("https://learn.microsoft.com/dotnet/api/microsoft.extensions", url);
    }

    [Fact]
    public void Resolve_NonBclKey_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DocUrlResolver.Resolve("T:UnityEngine.GameObject"));
        Assert.Equal(string.Empty, DocUrlResolver.Resolve("T:MyApp.Service"));
    }

    [Fact]
    public void Resolve_EmptyOrMalformed_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DocUrlResolver.Resolve(""));
        Assert.Equal(string.Empty, DocUrlResolver.Resolve("X"));
        Assert.Equal(string.Empty, DocUrlResolver.Resolve("NoColon"));
    }
}
