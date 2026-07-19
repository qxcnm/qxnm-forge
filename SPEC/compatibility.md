# qxnm-forge compatibility and support policy

## Version axes

Protocol, session journal, faux scenario, provider manifest, model catalog, and
individual artifact formats have independent versions. Protocol, journal, and
faux scenario currently use `"0.1"`; Provider manifest v0.2 references model
catalog v1.0. A product release version does not imply any one data-format
version.

Protocol negotiation occurs only in `initialize`. The client offers an ordered
set of versions and the server selects exactly one offered version. No common
version is an initialization error. Once selected, every message on that
connection uses that version.

Session readers MUST reject a newer unknown major format without modifying it.
They MAY migrate an older supported format only by the backup-and-replace
procedure in [session.md](session.md).

## Strict core, explicit extensions

Core DTOs reject unknown fields. Forward-compatible implementation data may be
placed only in an `extensions` object keyed by a reverse-DNS namespace such as
`org.example.feature`. Receivers that do not recognize a namespace MUST retain
it when round-tripping durable records and otherwise ignore it. Extension data
MUST NOT change core state-machine ordering or permission decisions.

中文说明：不能把任意字段偷偷塞进核心对象。扩展数据必须进入有命名空间的
`extensions`，且扩展不得绕过权限或改变公共状态机。

Adding a required core field, changing an enum interpretation, changing event
ordering, or weakening a security default requires a new protocol/session
version as applicable. Adding a new optional extension namespace does not.

## Capability levels

Every language/feature cell has exactly one level:

- `unsupported` — absent or intentionally unavailable;
- `implemented` — code exists, but common conformance is incomplete;
- `conformant` — deterministic common tests pass on every claimed platform;
- `live-verified` — conformant plus an explicitly enabled redacted live smoke
  test against the relevant provider/platform.

Only `conformant` and `live-verified` are public support. A provider is not a
single Boolean: authentication, text, streaming, tools, reasoning, image input,
image output, and retry behavior have independent cells.

Provider capability claim 的规范身份是
`(providerId, apiFamily, feature)`，不是 `(providerId, feature)` 或 Provider 品牌级布尔值。
同一 Provider 的 sibling route 必须分别取证、分级和撤销；尤其 OpenRouter 的文本与图像
route 不能因共享 `providerId` 合并。实现语言、平台和 evidence 仍作为该 route-scoped
cell 的独立维度记录。缺少 `apiFamily` 的 Provider target 是歧义输入，不能用于公开支持
声明或从一个 family 外推另一个 family。

## Unknown values

Within a negotiated version, unknown core methods, event types, enum values, or
fields are errors unless the relevant schema explicitly says they are open.
Clients MUST use stable error codes and MUST NOT parse error messages. Unknown
extension namespaces are handled as above.

## Deprecation

A stable protocol version remains readable for at least one subsequent stable
version. Removing support requires release notes and a migration path. Security
fixes may disable unsafe behavior immediately, but persisted data remains
recoverable through an offline migration/export tool.

## PI import boundary

PI Session v3 import is a one-way compatibility adapter. Import MUST read but
never modify the source file, write a new qxnm-forge journal, preserve the source
Session ID, exact source-byte SHA-256 and pinned reference commit as provenance,
and report every skipped, quarantined, redacted, extension-only or lossily
converted entry. Raw cwd, parent-session and caller source paths are not
portable and MUST NOT be persisted; this privacy rule supersedes the earlier
ambiguous requirement to preserve a source path. The interactive CLI may show
the caller-selected path only during explicit import confirmation. Exporting
qxnm-forge sessions back to PI is outside v1.
