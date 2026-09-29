---
title: Rx.NET / R3 連携
description: Configlue の値、プロファイル変更、再読み込み失敗を Observable として合成する。
---

Configlue Core の変更通知はコールバックを基本にしています。すでに Rx.NET または R3 を使っているアプリケーションでは、任意のアダプターパッケージを追加すると、Configlue Core 側へ Reactive 依存を持ち込まずに Observable として扱えます。

Rx.NET では `Configlue.Extensions.Reactive`、R3 では `Configlue.Extensions.R3` を追加します。

## 利用できるアダプター

| API | 対象 | 流れる値 |
| --- | --- | --- |
| `ObserveChanges()` | `IReadOnlyOptions<T>` | 購読後に変化した有効値。初期読み取りなし |
| `ObserveValues()` | `IReadOnlyOptions<T>` | 現在の有効値と、その後の変更 |
| `ObserveReloadFailures()` | `IConfiglueDiagnostics<T>` | バックグラウンド再読み込みの失敗 |
| `ObserveActiveValues()` | `IConfiglueProfiledOptions<T>` | 現在の active profile の値と、その後の切り替え・値変更 |
| `ObserveActiveProfileNames()` | `IConfiglueProfiledOptions<T>` | 現在の active profile 名と、その後の切り替え |

これらは cold stream です。購読ごとに Configlue の listener を登録し、初期読み取りがある場合はそのキャンセル状態も購読側が所有します。購読を破棄すると listener も解除されます。

## Rx.NET で複数の設定を合成する

```csharp
using Configlue.Extensions.Reactive;
using System.Reactive.Linq;

using var subscription = app.ObserveValues()
    .Select(value => value.Theme)
    .DistinctUntilChanged()
    .CombineLatest(
        network.ObserveValues(),
        (theme, connection) => new { Theme = theme, connection.Endpoint })
    .Throttle(TimeSpan.FromMilliseconds(100))
    .Subscribe(ApplyView, ReportReadFailure);
```

Configlue は UI dispatcher を自動では選びません。特定の scheduler や synchronization context で処理する必要がある場合は、Rx.NET または R3 側の scheduling operator を使います。

## 再読み込みの失敗は別の stream にする

`ObserveReloadFailures()` は、watcher がバックグラウンドで再読み込みした際の失敗を例外値として流します。後の再読み込みで復旧できるため、この通知だけで値の stream を終了させることはありません。

listener の登録時や `ObserveValues()` の初期読み取りで失敗した場合は、その購読自体が終了します。Rx.NET は `OnError`、R3 は `Result.Failure` で通知します。再試行が必要な場合は、それぞれのライブラリが提供する operator を使います。

## active profile の切り替え

`ObserveActiveValues()` は active profile を追跡します。active profile が変わると、それまでの profile の購読を解除し、新しい profile の値 stream へ切り替えます。

profile 名そのものを別の Observable と組み合わせたい場合は `ObserveActiveProfileNames()` を使います。

Options、profile manager、それらを所有する context の寿命はアプリケーション側が管理します。購読中はこれらを破棄しないでください。
