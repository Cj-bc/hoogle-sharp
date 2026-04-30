using CSharpHoogle.Cli;

namespace CSharpHoogle.Cli.Tests;

public class TypeQueryMatcherTests
{
    private static readonly MethodSource Source = new("assembly", "Test");

    private static CachedMethod Method(
        string returnType,
        string[] parameterTypes,
        int requiredCount,
        string[]? generics = null)
        => new(
            FullName: "Test.M",
            ReturnType: returnType,
            ParameterTypes: parameterTypes,
            GenericParams: generics ?? Array.Empty<string>(),
            IsExtensionMethod: false,
            DocUrl: "",
            Summary: null,
            Source: Source,
            RequiredParameterCount: requiredCount);

    // FirstAsync<T>(this IObservable<T>, CancellationToken = default) -> Task<T>
    private static CachedMethod FirstAsync() => Method(
        returnType: "Task<T>",
        parameterTypes: new[] { "Observable<T>", "CancellationToken" },
        requiredCount: 1,
        generics: new[] { "T" });

    [Fact]
    public void OptionalCancellationToken_MayBeOmitted()
    {
        var query = TypeQuery.ParseSignature("Observable<T> -> Task<T>");
        Assert.True(TypeQueryMatcher.Matches(query, FirstAsync()));
    }

    [Fact]
    public void OptionalCancellationToken_MayBePresent()
    {
        var query = TypeQuery.ParseSignature("Observable<T> -> CancellationToken -> Task<T>");
        Assert.True(TypeQueryMatcher.Matches(query, FirstAsync()));
    }

    [Fact]
    public void RequiredParameter_CannotBeDropped()
    {
        // The Observable source is required; a query without it must not match.
        var query = TypeQuery.ParseSignature("CancellationToken -> Task<T>");
        Assert.False(TypeQueryMatcher.Matches(query, FirstAsync()));
    }

    [Fact]
    public void EmptyParameterQuery_RequiresZeroRequired()
    {
        // Just `Task<T>` parses as zero parameters with that return.
        var query = TypeQuery.ParseSignature("Task<T>");
        Assert.False(TypeQueryMatcher.Matches(query, FirstAsync()));
    }

    [Fact]
    public void NoOptionals_StrictArityStillEnforced()
    {
        // Two required ints, no optionals: a single-arg query must fail even though
        // the loosened arity check is in play for other methods.
        var addExact = Method("int", new[] { "int", "int" }, requiredCount: 2);
        var underSized = TypeQuery.ParseSignature("int -> int");
        Assert.False(TypeQueryMatcher.Matches(underSized, addExact));

        var exact = TypeQuery.ParseSignature("int -> int -> int");
        Assert.True(TypeQueryMatcher.Matches(exact, addExact));
    }

    [Fact]
    public void MultipleOptionals_AnySubsetAccepted()
    {
        // Method (Alpha, Beta, Gamma) with only Alpha required. Concrete
        // multi-letter names so the wildcard heuristic doesn't kick in and
        // accidentally let unbound query params absorb method slots.
        var m = Method("Result", new[] { "Alpha", "Beta", "Gamma" }, requiredCount: 1);

        Assert.True(TypeQueryMatcher.Matches(TypeQuery.ParseSignature("Alpha -> Result"), m));
        Assert.True(TypeQueryMatcher.Matches(TypeQuery.ParseSignature("Alpha -> Beta -> Result"), m));
        Assert.True(TypeQueryMatcher.Matches(TypeQuery.ParseSignature("Alpha -> Gamma -> Result"), m));
        Assert.True(TypeQueryMatcher.Matches(TypeQuery.ParseSignature("Alpha -> Beta -> Gamma -> Result"), m));

        // Order at the top level is irrelevant — backtracking should still find
        // the assignment.
        Assert.True(TypeQueryMatcher.Matches(TypeQuery.ParseSignature("Gamma -> Alpha -> Result"), m));

        // Dropping the required Alpha must fail.
        Assert.False(TypeQueryMatcher.Matches(TypeQuery.ParseSignature("Beta -> Gamma -> Result"), m));
    }

    [Fact]
    public void AllOptionalParameters_ZeroArgQueryMatches()
    {
        var m = Method("Result", new[] { "Alpha", "Beta" }, requiredCount: 0);
        var query = TypeQuery.ParseSignature("Result");
        Assert.True(TypeQueryMatcher.Matches(query, m));
    }

    [Fact]
    public void OverSizedQuery_StillRejected()
    {
        // Query has more params than the method total — the upper bound on
        // arity stays strict.
        var m = Method("Result", new[] { "Alpha" }, requiredCount: 1);
        var query = TypeQuery.ParseSignature("Alpha -> Beta -> Result");
        Assert.False(TypeQueryMatcher.Matches(query, m));
    }
}
