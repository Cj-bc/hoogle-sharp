using System.Reflection;
using CSharpHoogle.Core.Models;
using CSharpHoogle.Core.Reflection;
using CSharpHoogle.Core.Storage;
using LoxSmoke.DocXml;

namespace CSharpHoogle.Core.Indexing;

/// <summary>
/// Walks an assembly via <see cref="MetadataLoader"/> and produces
/// <see cref="MethodEntry"/> records for every public method on every
/// public type. Each entry has its <see cref="DocEntry"/> attached via
/// <see cref="LoxSmoke.DocXml"/> member-key lookup against a provided
/// <see cref="IDocEntryRepository"/>.
/// </summary>
/// <remarks>
/// The <see cref="Type"/> instances stored in returned <see cref="MethodEntry"/>
/// records belong to the <see cref="MetadataLoadContext"/> that produced them.
/// Callers must keep the <see cref="MetadataLoader"/> alive for as long as they
/// intend to inspect those types; disposing it invalidates the Type instances.
/// </remarks>
public static class MethodIndexBuilder
{
    private const string ExtensionAttributeFullName =
        "System.Runtime.CompilerServices.ExtensionAttribute";

    /// <summary>
    /// Loads <paramref name="dllPath"/> and builds a MethodEntry for every
    /// public method on every public type. If <paramref name="loader"/> is null,
    /// creates a throwaway loader internally (disposed before return — the
    /// returned entries' Type instances will become unusable after this call).
    /// </summary>
    public static IReadOnlyList<MethodEntry> BuildFromAssembly(
        string dllPath,
        IDocEntryRepository docs,
        MetadataLoader? loader = null)
    {
        var ownsLoader = loader is null;
        loader ??= new MetadataLoader();
        try
        {
            var assembly = loader.LoadFromAssemblyPath(dllPath);
            return Build(assembly, docs);
        }
        finally
        {
            if (ownsLoader)
            {
                loader.Dispose();
            }
        }
    }

    /// <summary>
    /// Builds MethodEntry records from an already-loaded assembly. Use this
    /// overload when you want to reuse a single <see cref="MetadataLoader"/>
    /// across many assemblies.
    /// </summary>
    public static IReadOnlyList<MethodEntry> BuildFromAssembly(
        Assembly assembly,
        IDocEntryRepository docs)
    {
        return Build(assembly, docs);
    }

    private static IReadOnlyList<MethodEntry> Build(Assembly assembly, IDocEntryRepository docs)
    {
        var entries = new List<MethodEntry>();

        foreach (var type in SafeGetTypes(assembly))
        {
            if (!IsVisiblePublicType(type))
            {
                continue;
            }

            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName)
                {
                    // Skip property/event accessors, operators — we only want plain methods.
                    continue;
                }

                entries.Add(BuildEntry(method, docs));
            }
        }

        return entries;
    }

    private static MethodEntry BuildEntry(MethodInfo method, IDocEntryRepository docs)
    {
        var memberKey = XmlDocId.MethodId(method);
        var fullName = BuildFullName(method);
        var rawParams = method.GetParameters();
        var paramTypes = Array.ConvertAll(rawParams, p => p.ParameterType);
        // Trailing-optional scan: any IsOptional run at the tail is omittable.
        // Stops at the first non-optional, so the rare IL-level non-trailing
        // case is treated as required — safe default.
        var requiredCount = rawParams.Length;
        while (requiredCount > 0 && rawParams[requiredCount - 1].IsOptional)
        {
            requiredCount--;
        }
        var genericParams = method.IsGenericMethodDefinition
            ? Array.ConvertAll(method.GetGenericArguments(), g => g.Name)
            : Array.Empty<string>();

        // Receiver only makes sense for instance methods. Static methods —
        // including extension methods, whose first parameter already plays
        // the role of receiver — get null so the matcher's synthetic-receiver
        // slot is suppressed for them.
        var isExtension = IsExtensionMethod(method);
        Type? declaringType = null;
        string[] typeGenericParams = Array.Empty<string>();
        if (!method.IsStatic && !isExtension && method.DeclaringType is { } declaring)
        {
            declaringType = declaring;
            if (declaring.IsGenericType)
            {
                typeGenericParams = Array.ConvertAll(
                    declaring.GetGenericArguments(),
                    g => g.Name);
            }
        }

        return new MethodEntry(
            FullName: fullName,
            ParameterTypes: paramTypes,
            ReturnType: method.ReturnType,
            GenericParams: genericParams,
            IsExtensionMethod: isExtension,
            DocUrl: DocUrlResolver.Resolve(memberKey),
            Doc: docs.Get(memberKey),
            RequiredParameterCount: requiredCount,
            DeclaringType: declaringType,
            TypeGenericParams: typeGenericParams);
    }

    private static string BuildFullName(MethodInfo method)
    {
        var declaring = method.DeclaringType;
        if (declaring is null)
        {
            return method.Name;
        }

        var typeName = declaring.FullName ?? declaring.Name;

        // Strip the generic-arity backtick suffix (`List`1.Add` → `List.Add`) so the
        // human-readable FullName matches what HANDOFF.md's example shows.
        var tick = typeName.IndexOf('`');
        if (tick >= 0)
        {
            typeName = typeName[..tick];
        }

        return typeName + "." + method.Name;
    }

    private static bool IsExtensionMethod(MethodInfo method)
    {
        if (method.DeclaringType is null)
        {
            return false;
        }

        return method.IsStatic
            && HasAttribute(method.GetCustomAttributesData(), ExtensionAttributeFullName)
            && HasAttribute(method.DeclaringType.GetCustomAttributesData(), ExtensionAttributeFullName);
    }

    private static bool HasAttribute(IList<CustomAttributeData> data, string fullName)
    {
        for (var i = 0; i < data.Count; i++)
        {
            if (data[i].AttributeType.FullName == fullName)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsVisiblePublicType(Type type)
    {
        // For nested types, IsPublic is false even when public — use IsVisible instead,
        // which reports true for public types whose enclosing types are all public.
        return type.IsVisible;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Some types failed to load (common with MetadataLoadContext against
            // assemblies whose references are outside the resolver's search paths).
            // Return the ones that did load.
            return ex.Types.Where(t => t is not null)!;
        }
    }
}
