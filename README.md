# niptent

VRChat アバターの衣装に、素体の `Nipple_On` に合わせたシェイプキーを作る Claude Code の skill です。

素体の `Nipple_On` を入れると、ブラやビキニを突き抜けることがあります。この skill は、衣装に同じ名前のシェイプキーを作って素体と連動させ、突き抜けを防ぎます。

## 使い方

Claude Code に頼むだけです。

```
ヒエラルキー上の Avatar > Outfit > Bra に Nipple_On のシェイプキーを作って。
```

衣装に配布元の `Nipple_On` が元からある時は、作り直さずに素体との連動だけを足します。

## 必要なもの

- Unity 2022.3 のアバタープロジェクト
- [MCP for Unity](https://github.com/CoplayDev/unity-mcp)（Unity と Claude Code をつなぐ）
- Python 3
- Modular Avatar（任意。入っていれば素体と自動で連動させる）

## 入れ方

Unity プロジェクトのフォルダで、次のどちらかを実行します。

```
npx skills add hrpnx/niptent -a claude-code
```

```
gh skill install hrpnx/niptent niptent --agent claude-code
```

## ライセンス

MIT
