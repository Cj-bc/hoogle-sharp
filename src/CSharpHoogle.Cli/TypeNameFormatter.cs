using System.Text;

namespace CSharpHoogle.Cli;

/// <summary>
/// Formats a <see cref="Type"/> into a readable string for CLI display
/// (e.g. <c>IEnumerable&lt;TSource&gt;</c> instead of <c>IEnumerable`1</c>).
/// Operates on metadata-only Types (MetadataLoadContext-owned) without
/// requiring runtime-type comparisons.
/// </summary>
public static class TypeNameFormatter
{
    public static string Format(Type type)
    {
        if (type.IsByRef)
        {
            return "ref " + Format(type.GetElementType()!);
        }

        if (type.IsPointer)
        {
            return Format(type.GetElementType()!) + "*";
        }

        if (type.IsArray)
        {
            var rank = type.GetArrayRank();
            var commas = rank > 1 ? new string(',', rank - 1) : string.Empty;
            return Format(type.GetElementType()!) + "[" + commas + "]";
        }

        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.IsConstructedGenericType || type.IsGenericTypeDefinition)
        {
            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0)
            {
                name = name[..tick];
            }

            var args = type.GetGenericArguments();
            var sb = new StringBuilder(name.Length + args.Length * 8);
            sb.Append(name);
            sb.Append('<');
            for (var i = 0; i < args.Length; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Format(args[i]));
            }
            sb.Append('>');
            return sb.ToString();
        }

        return type.Name;
    }
}
