namespace CSharpHoogle.Cli;

/// <summary>
/// Origin of a cached method. <see cref="Kind"/> is <c>"assembly"</c> today;
/// a future source-file indexer will use <c>"source"</c> with a file path in
/// <see cref="Name"/>. Modeled as a record so grouping and JSON round-tripping
/// stay straightforward.
/// </summary>
public sealed record MethodSource(string Kind, string Name);

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
    string? Summary,
    MethodSource Source);
