Read and write outcomes
=======================

Readers construct `StateReadResult<T>` through `Success`, `NotFound`,
`Unavailable`, or `Invalid`. `Status` and `Value` are read-only, so a `with`
expression cannot turn a missing result into a successful result without a value.
`Success(null)` is rejected. The default struct value represents `NotFound`.
Revision, schema, source, and origin metadata can still be attached to the result.

Write requests use an explicit `RevisionCondition`:

| Condition | Meaning |
| --- | --- |
| `None` | Write without an optimistic concurrency check. This is also the default. |
| `Match(revision)` | Require the exact observed revision. An empty revision remains a real token. |
| `MustNotExist` | Require the logical value to be absent. |

For optimistic writes after a read, use
`RevisionCondition.FromRevision(result.Revision)`: a missing revision means
`MustNotExist`, rather than an unchecked write. Pass `None` explicitly when an
unchecked write is intended.

Absence is scoped to the target. A JSON, XML, or YAML section can be absent in an
existing document, and a ZIP entry can be absent in an existing archive. Those
providers also protect the enclosing resource during read-modify-write. A
resource-level request instead checks absence of the resource itself. Batch
writes to one resource reject incompatible conditions before writing.

Application saves, source-local saves, routed patch saves, and edit-session
commits return `StateWriteReceipt`. `Sources` records each logical source ID,
resource ID, and resulting revision; `PhysicalWriteCount` reports the actual
number of backing-resource writes. Several logical source updates can share one
physical write. `Revision` is a convenience for a receipt containing exactly one
source. A no-op returns an empty receipt with zero physical writes.

Provider writers continue to return the lower-level `StateWriteResult`, whose
revision describes that provider operation. Application code should use the
receipt rather than discard source or resource identity. Multi-source failures
retain the existing successful and failed source information in
`StateMultiWriteException`; a receipt does not imply cross-resource atomicity.
