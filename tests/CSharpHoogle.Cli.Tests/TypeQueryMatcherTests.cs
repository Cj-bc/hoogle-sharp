using CSharpHoogle.Cli;

namespace CSharpHoogle.Cli.Tests;

public class TypeQueryMatcherTests
{
    private static readonly MethodSource Source = new("assembly", "Test");

    private static CachedMethod Method(
        string returnType,
        string[] parameterTypes,
        int requiredCount,
        string[]? generics = null,
        string? declaringType = null,
        string[]? typeGenericParams = null)
        => new(
            FullName: "Test.M",
            ReturnType: returnType,
            ParameterTypes: parameterTypes,
            GenericParams: generics ?? Array.Empty<string>(),
            IsExtensionMethod: false,
            DocUrl: "",
            Summary: null,
            Source: Source,
            RequiredParameterCount: requiredCount,
            DeclaringType: declaringType,
            TypeGenericParams: typeGenericParams ?? Array.Empty<string>());

    // bool Dictionary<TKey,TValue>.ContainsKey(TKey key)
    private static CachedMethod ContainsKey() => Method(
        returnType: "bool",
        parameterTypes: new[] { "TKey" },
        requiredCount: 1,
        declaringType: "Dictionary<TKey, TValue>",
        typeGenericParams: new[] { "TKey", "TValue" });

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

    [Fact]
    public void Receiver_InQuery_MatchesInstanceMethod()
    {
        // Dictionary<TKey,TValue> -> TKey -> bool finds bool ContainsKey(TKey)
        // declared on Dictionary<TKey,TValue>.
        var query = TypeQuery.ParseSignature("Dictionary<TKey, TValue> -> TKey -> bool");
        Assert.True(TypeQueryMatcher.Matches(query, ContainsKey()));
    }

    [Fact]
    public void Receiver_OmittedFromQuery_StillMatchesInstanceMethod()
    {
        // The synthetic receiver slot is optional — TKey -> bool still matches.
        var query = TypeQuery.ParseSignature("TKey -> bool");
        Assert.True(TypeQueryMatcher.Matches(query, ContainsKey()));
    }

    [Fact]
    public void Receiver_QueryHasNoMatchingMethod_Rejected()
    {
        // A static method (DeclaringType: null) must not gain a receiver slot,
        // so a query that mentions Dictionary cannot be absorbed by a static
        // helper with mismatched parameter types.
        var staticMethod = Method("bool", new[] { "int" }, requiredCount: 1);
        var query = TypeQuery.ParseSignature("Dictionary<TKey, TValue> -> TKey -> bool");
        Assert.False(TypeQueryMatcher.Matches(query, staticMethod));
    }

    [Fact]
    public void Receiver_StaticMethod_DoesNotInflateArity()
    {
        // int -> int continues to match a static (DeclaringType: null) method
        // with one int parameter and an int return — the receiver pool change
        // must not regress static methods.
        var staticAbs = Method("int", new[] { "int" }, requiredCount: 1);
        var query = TypeQuery.ParseSignature("int -> int");
        Assert.True(TypeQueryMatcher.Matches(query, staticAbs));
    }

    [Fact]
    public void Receiver_TypeGenericsUnifyAcrossReceiverAndParameters()
    {
        // The method has receiver Dictionary<TKey, TValue> and parameter TKey.
        // With a query that supplies a CONCRETE Dictionary, the type generic
        // TKey must bind to the same concrete type that fills the param slot.
        var query = TypeQuery.ParseSignature("Dictionary<int, string> -> int -> bool");
        Assert.True(TypeQueryMatcher.Matches(query, ContainsKey()));

        // A query whose parameter type can't unify with TKey (because TKey
        // is bound to int by the receiver) must fail.
        var mismatch = TypeQuery.ParseSignature("Dictionary<int, string> -> string -> bool");
        Assert.False(TypeQueryMatcher.Matches(mismatch, ContainsKey()));
    }

    [Fact]
    public void Receiver_ConcreteTypeMatchesAgainstReceiver()
    {
        // Receiver as a non-generic concrete type, no type-level generics.
        var instanceMethod = Method(
            returnType: "string",
            parameterTypes: Array.Empty<string>(),
            requiredCount: 0,
            declaringType: "MyClass");

        Assert.True(TypeQueryMatcher.Matches(
            TypeQuery.ParseSignature("MyClass -> string"),
            instanceMethod));

        // Without the receiver, the zero-param method still matches by return.
        Assert.True(TypeQueryMatcher.Matches(
            TypeQuery.ParseSignature("string"),
            instanceMethod));
    }
}
