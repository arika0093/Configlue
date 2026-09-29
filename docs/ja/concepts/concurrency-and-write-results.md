---
title: 競合制御と書き込み結果
description: リビジョン検査、edit session の競合、複数ソースへの保存、StateWriteReceipt の意味を整理する。
---

Configlue は、複数の論理ソースと複数の物理リソースを1つの有効値へ合成できます。そのため、保存結果では「どの論理ソースを更新したか」と「物理的に何回書き込んだか」を区別します。

## アプリケーションの保存結果は StateWriteReceipt

`SaveAsync`、特定ソースへの保存、経路指定された Patch、edit session の commit は `StateWriteReceipt` を返します。

```csharp
var receipt = await state.SaveAsync(patch =>
{
    patch.Server.Port = 9000;
});

foreach (var source in receipt.Sources)
{
    Console.WriteLine($"{source.SourceId}: {source.Revision}");
}

Console.WriteLine($"Physical writes: {receipt.PhysicalWriteCount}");
```

`Sources` には、書き込んだ論理ソースごとの結果が入ります。`PhysicalWriteCount` は、背後のリソースを実際に更新した回数です。同じファイル内の重ならない section のように、複数の論理更新を1回の物理書き込みへまとめられる場合があります。

`Revision` は、論理ソースを1つだけ書き込んだ receipt のための簡便なプロパティです。変更がなければ、ソース数0・物理書き込み0の receipt が返ります。

## 楽観的同時実行制御

Provider は `RevisionCondition` で書き込み条件を表します。`Match(revision)` は読み取ったリビジョンとの一致を要求し、`MustNotExist` は対象が存在しないことを要求します。`None` はリビジョンを検査せずに書き込みます。

通常のアプリケーションコードでは、この条件を直接組み立てるより上位の API を使います。edit session は開始時の値を保持し、commit 前に最新状態を読み直します。同じメンバーが同時に変更されていれば、既定では `StateConflictException` が発生します。

同時に変更されたメンバーについて edit session 側の値を採用する設計であれば、モデル登録に `WriteConflictResolution = WriteConflictResolution.LastWriteWins` を設定できます。ただし、最終的な保存時のリビジョン検査まで無効になるわけではありません。

## 複数リソースへの保存はトランザクションではない

1回の論理保存が複数の物理リソースへ書き込む場合があります。途中で失敗したときは成功済み・失敗したソースを診断できますが、独立したリソース間でトランザクションの原子性を保証するものではありません。

複数の外部システムをまたいで all-or-nothing が必要な場合は、そのトランザクション境界を Configlue の外側に設けるか、対象の論理ソースを1回の物理更新で保存できるリソースへまとめます。

edit session は [読み書き・編集セッション・Patch](../basic-usage/reading-and-writing.md)、複数ソースへの保存先指定は [書き込み経路指定](../layering/write-routing.md) を参照してください。
