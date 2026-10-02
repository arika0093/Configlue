# 指定コミット以降のissue対応レビュー

対象: `d9b0350be1396821baae0886bc776640b9282ad6..3acdebc2254dadc1b8fc1d3540b005e3e275c07a`（origin/mainの9コミット）。

作業先: `C:/ark/program/oss/Configlue-issue-audit`、ブランチ `review/issue-requirements-20261002`。
元のworktreeの作業ファイルは変更していない。

| コミット / PR | 対応issue | 確認結果と修正 |
| --- | --- | --- |
| 724a08c / #133 | [#121](https://github.com/arika0093/Configlue/issues/121) | internalモデルの拡張クラスは適切。publicな現行モデルがinternalな旧モデルを公開シグネチャに出す漏れを修正。公開範囲の異なるモデルのコンパイルテストを追加。 |
| a697223 / #134 | [#111](https://github.com/arika0093/Configlue/issues/111) | 登録コードは要件に合致。同一scopedインスタンス、登録された通知インターフェース経由の認証変更、複数subject型でのlast-registration-wins、scope破棄後の通知停止をテスト。 |
| 036069a / #135 | [#122](https://github.com/arika0093/Configlue/issues/122) | file-local/ref-likeの診断とreadonly修飾の伝播を確認。init-onlyモデルで生成コンストラクターが暗黙の引数なしコンストラクターを消す問題、および空モデルの不正な生成コードを修正。class/struct/record/readonlyのコンパイルテストを追加。 |
| 08749b2 / #136 | [#112](https://github.com/arika0093/Configlue/issues/112) | readonly structにmutableフィールドがありCS8340でビルド不能だったため修正。zeroとDefaultの等価性・ハッシュを統一し、Default with { ModelId = ... }を保持。不完全なコンテキストは共通境界で拒否。mutation・source・resolver・writer・watcher・composite・fallbackをテスト。 |
| cfbc4a3 / #72 | [#72](https://github.com/arika0093/Configlue/issues/72) | READMEと英日ランディングの構成は要件に合致。Quick Startの型宣言がトップレベル文より前にあり実行できなかったため順序を修正。例のコンパイルとAstroビルドを確認。 |
| 166ade7 / #137 | [#123](https://github.com/arika0093/Configlue/issues/123) | sparse writeで片側だけ存在すると曖昧なwire nameを出力できる非対称性を修正。read/writeの開始時に全メンバーを検証し、空JSONでも曖昧な設定を拒否。explicit/default名およびstructural型の静的衝突も診断。命名ポリシー・case-insensitive・正常なexplicit名のテストを追加。 |
| c62c73d / #138 | [#100](https://github.com/arika0093/Configlue/issues/100) | .NET Standard非対応のEnum.IsDefined呼び出し、破棄とCTS.Token取得の競合、watcher作成失敗時のfallback漏れ、過大なpoll intervalを修正。poll intervalが長くてもrevision検証の期限を守る。通知を無効にしたlive watcherで欠落を再現し、watcher勝利・polling勝利・同一サイズ/時刻・作成/置換/削除・欠落directory・watcher error・再待機・並行待機・cancel/disposeをテスト。 |
| 614bda9 / #139 | [#119](https://github.com/arika0093/Configlue/issues/119) | 予約名一覧の漏れ、プロパティ以外のroot宣言、structural型、ObservableのSetChild、拡張コンテナーとの衝突を診断。共通fragment名はSparseNamingに集約。キーワード名から不正な補助識別子ができる経路も修正し、コンパイルテストを追加。生成内部の`__`接頭辞は予約対象。 |
| 3acdebc / #141 | [#101](https://github.com/arika0093/Configlue/issues/101) | FromResourceの直接修正は適切。ただしprojection adapterがcontextual identityを公開せず、既存の固定identityテストも失敗していた。読み取り専用・書き込み可能な投影の双方でcontextual identityを保持。2subjectのID、batch mutationとsourceのID一致、実際のbatch write、明示的fixed overrideをテスト。 |

## 検証

- Configlue.Tests / net10.0: **847成功、17スキップ、失敗0**（全864件）。
- Configlue.Tests.PublicApi: **73成功、失敗0**。生成コードテストとAPI承認を含む。
- SparseFragments.Tests: **81成功、失敗0**。
- Configlue.Generator.Compatibility.Tests: **66成功、失敗0**。
- Configlue.Core: netstandard2.0 / netstandard2.1 / net10.0をビルド。
- README Quick Start: package指示行を外し、同じコードを作業中のConfiglueへのProjectReferenceでコンパイル。NuGet公開済みパッケージの実行は検証していない。
- docs-site: `npm ci --no-audit --no-fund`、`npm run build`成功。
- API承認ファイル: 明示実装したcontextのEquals/GetHashCodeを反映。#138で手編集されていたCore承認の並び順も正規化。

.NETのビルド・実行には`-p:WarningsNotAsErrors=S2743`を指定した。指定範囲より前からある`CurrentSubjectState.cs`のstatic TimeSpanフィールド2件のSonar警告は未修正。GeneratorのPolyfillMemoryVersion警告も残る。外部Redis/PostgreSQL/S3の接続設定がない17件は既存の条件に従いスキップ。NativeAOT publishとホスト固有の全プラットフォーム実行は行っていない。

ログとREADMEコンパイル用の一時プロジェクトは、このworktreeの`artifacts/issue-audit/`に保存。
