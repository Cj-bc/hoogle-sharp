namespace CSharpHoogle.Core.Models;

/// <summary>
/// XML doc comment から抽出したドキュメント情報。
/// 検索対象ではなく、検索結果の表示用。
/// </summary>
public record DocEntry(
    string MemberKey,
    string? Summary,
    string? Returns,
    IReadOnlyDictionary<string, string> Params,
    string? Remarks,
    string? Example);
