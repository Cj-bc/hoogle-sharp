# C# Hoogle — BCL DocEntry ビルダー 実装資料

## プロジェクト概要

Haskell の Hoogle に相当する、**C# の型シグネチャで関数を検索するシステム**を構築する。
本タスクはその第一フェーズ：**BCL の XML doc comment を読み込み、`DocEntry` を構築してDBに保存するまで**。

---

## 本タスクのスコープ

```
Assembly (BCL dll)
    +
XML doc file (BCL xml)
    ↓
[インデックスビルダー]
    ↓
DocEntry の辞書 / DB
```

検索機能・UIは後フェーズ。今回は「データを作る」パイプラインのみ。

---

## 主要な型定義

```csharp
/// <summary>
/// XML doc comment から抽出したドキュメント情報。
/// 検索対象ではなく、検索結果の表示用。
/// </summary>
public record DocEntry(
    string MemberKey,       // XML name属性の値: "M:System.Linq.Enumerable.Select``2(...)"
    string? Summary,
    string? Returns,
    IReadOnlyDictionary<string, string> Params,  // param name → description
    string? Remarks,
    string? Example
);

/// <summary>
/// Reflection で取得したメソッド情報（後フェーズで拡張する骨格のみ定義）
/// </summary>
public record MethodEntry(
    string FullName,            // "System.Linq.Enumerable.Select"
    Type[] ParameterTypes,
    Type ReturnType,
    string[] GenericParams,
    bool IsExtensionMethod,
    string DocUrl,
    DocEntry? Doc               // ← 本タスクで埋める
);
```

---

## XML doc comment の形式

BCL の `.xml` ファイルは以下の構造：

```xml
<doc>
  <members>
    <member name="M:System.Linq.Enumerable.Select``2(...)">
      <summary>Projects each element...</summary>
      <param name="source">A sequence of values...</param>
      <param name="selector">A transform function...</param>
      <returns>An IEnumerable&lt;T&gt;...</returns>
      <remarks>...</remarks>
    </member>
  </members>
</doc>
```

**メンバーキーの形式:**
- `T:` → 型
- `M:` → メソッド
- `P:` → プロパティ
- `F:` → フィールド
- `` ``2 `` → ジェネリクス型引数が2個
- `` ``0 ``, `` ``1 `` → 型変数のインデックス参照（パラメータ部分）

---

## XMLファイルの取得場所

### BCL (標準ライブラリ)

```
%DOTNET_ROOT%/shared/Microsoft.NETCore.App/{version}/
  例: C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.x\
  ├── System.Linq.dll
  ├── System.Linq.xml
  ├── System.Runtime.dll
  ├── System.Runtime.xml
  └── ...
```

環境依存のパスは `RuntimeEnvironment.GetRuntimeDirectory()` または
`typeof(object).Assembly.Location` の親ディレクトリから取得できる。

```csharp
var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
// → /usr/share/dotnet/shared/Microsoft.NETCore.App/8.0.x  (Linux)
// → C:\Program Files\dotnet\shared\...                     (Windows)
```

---

## 実装すべきコンポーネント

### 1. `XmlDocParser`

`DocEntry` の辞書を構築する。

```csharp
public class XmlDocParser
{
    // XMLファイルを読み込み、メンバーキー → DocEntry の辞書を返す
    public static IReadOnlyDictionary<string, DocEntry> Parse(string xmlFilePath);

    // dllと同じディレクトリにある .xml を自動検索して Parse
    public static IReadOnlyDictionary<string, DocEntry> ParseForAssembly(string dllPath);
}
```

**実装上の注意点:**

- `<summary>` などのテキストはインデント・改行が含まれるので `.Trim()` が必要
- `<see cref="..."/>` タグが含まれる場合がある → 初期実装はテキストノードだけ結合でOK
- XMLが存在しないアセンブリも多い（例: サードパーティ） → ファイルが無い場合は空辞書を返す

```csharp
// <see cref="T:System.String"/> → 展開せずプレーンテキストで取得する場合
var summary = member.Element("summary")?
    .Nodes()
    .OfType<XText>()
    .Select(t => t.Value)
    .Aggregate(string.Concat)?
    .Trim();

// または全テキストを結合（<see/>タグは無視される）
var summary = member.Element("summary")?.Value.Trim();
```

### 2. `BclIndexBuilder`

ランタイムディレクトリを走査して `XmlDocParser` を呼び出す。

```csharp
public class BclIndexBuilder
{
    // ランタイムディレクトリ内の全 .xml を解析
    // dllと.xmlが同名で同一ディレクトリにあるものだけ対象
    public static IReadOnlyDictionary<string, DocEntry> BuildFromRuntime();

    // 特定の xml ファイルのみ対象
    public static IReadOnlyDictionary<string, DocEntry> BuildFromFiles(IEnumerable<string> xmlPaths);
}
```

### 3. `DocEntryRepository` (任意: 永続化が必要な場合)

初期実装はインメモリの辞書でよい。将来的にSQLiteなどに差し替える想定で
インターフェースだけ定義しておく。

```csharp
public interface IDocEntryRepository
{
    DocEntry? Get(string memberKey);
    void Store(IEnumerable<DocEntry> entries);
}

// 初期実装
public class InMemoryDocEntryRepository : IDocEntryRepository { ... }
```

---

## 使用ライブラリ

| 用途 | ライブラリ |
|---|---|
| XML解析 | `System.Xml.Linq` (標準) |
| XMLメンバーキー生成 | **`DocXml`** (NuGet) ← Reflectionとの突合に使用 |
| 永続化（後フェーズ） | `Microsoft.Data.Sqlite` |

### DocXml の使い方

```csharp
// Install: dotnet add package LoxSmoke.DocXml
using LoxSmoke.DocXml;

var reader = new DocXmlReader("System.Linq.xml");
var method = typeof(Enumerable).GetMethod("Select", ...);
var comments = reader.GetMethodComments(method);
// comments.Summary, comments.Returns, comments.Parameters[i].Text
```

> ※ `DocXml` は `XmlDocParser` の代替として全面採用してもよい。
> ただしメンバーキーの文字列辞書を直接扱いたい場合は自前パーサーの方が柔軟。

---

## 検証方法

```csharp
// テストとして System.Linq の Select を引いてみる
var docs = BclIndexBuilder.BuildFromRuntime();

// キーを直接確認
var key = "M:System.Linq.Enumerable.Select``2(...)";
var entry = docs[key];
Console.WriteLine(entry.Summary);
// → "Projects each element of a sequence into a new form."

// DocXml 経由で突合する場合
var method = typeof(Enumerable)
    .GetMethods()
    .First(m => m.Name == "Select" && m.GetParameters().Length == 2);
var reader = new DocXmlReader(xmlPath);
var comments = reader.GetMethodComments(method);
```

---

## ディレクトリ構成（推奨）

```
CSharpHoogle/
  src/
    CSharpHoogle.Core/
      Models/
        DocEntry.cs
        MethodEntry.cs
      Parsing/
        XmlDocParser.cs
      Indexing/
        BclIndexBuilder.cs
      Storage/
        IDocEntryRepository.cs
        InMemoryDocEntryRepository.cs
  tests/
    CSharpHoogle.Core.Tests/
      Parsing/
        XmlDocParserTests.cs
      Indexing/
        BclIndexBuilderTests.cs
```

---

## 完了条件

- [ ] BCL ランタイムディレクトリから `.xml` ファイルを列挙できる
- [ ] `XmlDocParser.Parse()` が `DocEntry` の辞書を返す
- [ ] `System.Linq.Enumerable.Select` の `Summary` が正しく取得できる
- [ ] `Params` 辞書に `source` / `selector` が含まれる
- [ ] XMLが存在しないアセンブリでも例外を投げずに空辞書を返す
- [ ] ユニットテストが通る

---

## 後フェーズへの接続（参考）

本タスクで作った `DocEntry` は、後フェーズで `MethodEntry.Doc` に紐付ける。
紐付けのキーは **Reflection の `MethodInfo` → XML メンバーキー文字列** の変換で行う。
この変換は `DocXml` ライブラリが提供する機能を使う。

```csharp
// 後フェーズのイメージ（今回は実装不要）
var entry = new MethodEntry(
    FullName: "System.Linq.Enumerable.Select",
    ...
    Doc: docEntryRepository.Get(DocXml.GetMemberKey(methodInfo))
);
```
