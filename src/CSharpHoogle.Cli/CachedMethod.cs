namespace CSharpHoogle.Cli;

/// <summary>
/// Flattened, JSON-serializable projection of a MethodEntry for CLI display
/// and caching. Avoids carrying live <see cref="Type"/> references so the
/// cache outlives the MetadataLoadContext that produced the source data.
/// </summary>
public sealed record CachedMethod(
    string FullName,
    string ReturnType,
    string[] ParameterTypes,
    string[] GenericParams,
    bool IsExtensionMethod,
    string DocUrl,
    string? Summary);
