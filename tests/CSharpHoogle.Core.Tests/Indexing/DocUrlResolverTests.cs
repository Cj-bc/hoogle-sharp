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

    [Fact]
    public void Resolve_GenericMethodArity_RendersAsSingleDash()
    {
        // Pins the order of the two Replace calls in DocUrlResolver: the
        // double-backtick (ECMA-335 generic-method arity) MUST be normalized
        // BEFORE the single-backtick (generic-type arity). If a refactor
        // collapses these into one Replace, reverses them, or replaces the
        // single backtick first, the double backtick collapses to "--"
        // (e.g. "select--2") and the Learn URL 404s.
        var url = DocUrlResolver.Resolve(
            "M:System.Linq.Enumerable.Select``2(System.Collections.Generic.IEnumerable{TSource},System.Func{TSource,TResult})");
        Assert.Contains("enumerable.select-2", url);
        Assert.DoesNotContain("select--2", url);
    }

    [Fact]
    public void Resolve_GenericTypeArity_RendersAsSingleDash()
    {
        // Companion to the arity-ordering test: a single backtick on a generic
        // type must also render as exactly one dash. Together these two tests
        // pin both branches of the path.Replace("``", "-").Replace("`", "-")
        // expression in DocUrlResolver.
        var url = DocUrlResolver.Resolve("T:System.Collections.Generic.List`1");
        Assert.Contains("list-1", url);
        Assert.DoesNotContain("list--1", url);
    }

    [Fact]
    public void Resolve_GenericMethodOnGenericType_RendersBothAritiesCorrectly()
    {
        // Combined case: a generic method declared on a generic type. Pins
        // that both arity markers in the same key are normalized independently
        // — "dictionary-2.trygetvalue" must not become "dictionary--2..." or
        // similar if someone refactors the Replace chain.
        var url = DocUrlResolver.Resolve(
            "M:System.Collections.Generic.Dictionary`2.TryGetValue(`0,`1@)");
        Assert.Contains("dictionary-2.trygetvalue", url);
        Assert.DoesNotContain("dictionary--2", url);
    }
}
