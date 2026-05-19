using CSharpHoogle.Cli;

namespace CSharpHoogle.Cli.Tests;

public class TypeQueryTests
{
    // LooksLikeSignatureQuery decides whether to route a query through
    // ParseSignature; a mistaken "yes" turns a plain substring search into a
    // FormatException, so the predicate must reject stray arrows.

    [Fact]
    public void LooksLikeSignatureQuery_NormalSignature_True()
    {
        Assert.True(TypeQuery.LooksLikeSignatureQuery("int -> int"));
    }

    [Fact]
    public void LooksLikeSignatureQuery_PlainSubstring_False()
    {
        Assert.False(TypeQuery.LooksLikeSignatureQuery("Foo"));
    }

    [Fact]
    public void LooksLikeSignatureQuery_MissingLhs_False()
    {
        Assert.False(TypeQuery.LooksLikeSignatureQuery("-> Foo"));
    }

    [Fact]
    public void LooksLikeSignatureQuery_MissingRhs_False()
    {
        Assert.False(TypeQuery.LooksLikeSignatureQuery("Foo ->"));
    }

    [Fact]
    public void LooksLikeSignatureQuery_MultipleArrows_True()
    {
        Assert.True(TypeQuery.LooksLikeSignatureQuery("A -> B -> C"));
    }

    [Fact]
    public void LooksLikeSignatureQuery_BareArrowWithWhitespace_False()
    {
        Assert.False(TypeQuery.LooksLikeSignatureQuery("   ->   "));
    }
}
