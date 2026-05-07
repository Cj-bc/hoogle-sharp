namespace CSharpHoogle.Core.Models;

/// <summary>
/// Reflection で取得したメソッド情報の骨格。
/// 後フェーズで Reflection と XML ドキュメントを突合して構築する。
/// Phase 1 では型シェイプの定義のみで、構築側は未実装。
/// </summary>
public record MethodEntry(
    string FullName,
    Type[] ParameterTypes,
    Type ReturnType,
    string[] GenericParams,
    bool IsExtensionMethod,
    string DocUrl,
    DocEntry? Doc,
    int RequiredParameterCount,
    Type? DeclaringType = null,
    string[]? TypeGenericParams = null);
