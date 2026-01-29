
---

## 1) “thoughts（思考出力）” を含むレスポンスを受け取っている可能性が高い（最重要）

あなたの `ExtractJsonText` は **candidates[0].content.parts[0].text しか見ません**。
Gemini / Vertex AI では、モデルが **thoughts（思考過程）を Part として返す**ことがあり、`parts` が複数になったり、`thought=true` のパートが混ざることがあります（ThinkingConfig / includeThoughts が仕様として存在）。([Google Cloud Documentation][1])

その場合に起きる典型的な事故はこうです：

* `parts[0].text` = **thoughts（人間向けでない中間推論）**
* `parts[1].text` 以降 = **最終のJSON（translations）**
* しかし実装は `parts[0]` 固定で拾うため、`ParseTranslations` がJSONとして解釈できず **0件** になる（あなたのログ “translations parsed: 0” と一致）
* 一方でHTTP bodyは thoughts + メタデータ込みで **肥大化**する（body lengthが大きい）

### 対策A：thoughtsを返さないよう明示する（推奨）

`generationConfig` に `thinkingConfig.includeThoughts=false` を明示し、可能なら thinking 自体も抑制します。Vertex AI の GenerationConfig には ThinkingConfig（includeThoughts / thinkingBudget / thinkingLevel）が定義されています。([Google Cloud Documentation][1])
（モデル系列により “完全にoff不可” など制約もありますが、少なくとも **thoughtsを返さない**のは有効です。）([Google AI for Developers][2])

例（JSONイメージ）：

```json
"generationConfig": {
  "temperature": 0.2,
  "maxOutputTokens": 1024,
  "responseMimeType": "application/json",
  "thinkingConfig": { "includeThoughts": false, "thinkingBudget": 0 }
}
```

### 対策B：ExtractJsonText を「thought=false の text」を選ぶ実装にする（必須）

thoughtsが返る可能性をゼロにできないので、**parts を走査して thought でない text を選ぶ**のが堅いです。

C#の修正例（要点）：

```csharp
private static string? ExtractJsonText(string rawResponse)
{
    try
    {
        using var doc = JsonDocument.Parse(rawResponse);
        var candidate = doc.RootElement.GetProperty("candidates")[0];
        var parts = candidate.GetProperty("content").GetProperty("parts");

        foreach (var p in parts.EnumerateArray())
        {
            if (!p.TryGetProperty("text", out var textEl)) continue;

            var isThought = p.TryGetProperty("thought", out var thoughtEl) && thoughtEl.GetBoolean();
            if (!isThought)
                return textEl.GetString();
        }

        // fallback
        return parts[0].GetProperty("text").GetString();
    }
    catch { return null; }
}
```

---

## 2) safetySettings のキー名が誤っており、設定が効いていない

リクエストで `safety_settings`（アンダースコア）を送っていますが、API仕様は `safetySettings`（キャメルケース）です。 ([Google AI for Developers][3])
現状だと **安全設定が無視**され、結果としてブロック挙動や候補生成の形が想定と変わるリスクがあります（OCRテキストのマッピングを壊したくない意図とも矛盾）。

修正は単純で、匿名型のプロパティ名を `safetySettings` に変えるだけです。

---


