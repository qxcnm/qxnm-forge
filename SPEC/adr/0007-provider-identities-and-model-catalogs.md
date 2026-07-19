# ADR 0007: Separate provider identity, API family, adapter, and model catalog

- Status: Accepted
- Date: 2026-07-13
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

The frozen v1 census contains 35 live Provider identities, nine text API
families, and one image API family. The first transport fixture used synthetic
identifiers such as `openai-compatible` to exercise an adapter without claiming
a live brand. At the same time, the current Provider manifest records only
identity, family membership, and high-level authentication classes, although
the architecture requires governed endpoint, model, capability, and quirk
metadata.

PI contains a large generated model snapshot: 1,041 text rows and 35 image
rows. qxnm-forge needs a frozen, reproducible evidence catalog, but treating every
row as a permanent protocol constant or public support claim would make catalog
refreshes wire-breaking and would advertise models for which the local
installation has no usable credentials.

## Decision

The following namespaces are distinct:

- `providerId` is a frozen live service or gateway identity from
  `providers.v1.json`, such as `openai`, `anthropic`, or `openrouter`.
- `apiFamily` is one of the frozen wire contracts, such as
  `openai-completions` or `anthropic-messages`.
- `adapterId` is an implementation-internal family adapter selector. It is not
  a public Provider identity and MUST NOT create a provider-level support
  claim.
- `modelId` is opaque within one Provider identity. The same spelling under a
  different Provider is a different selectable model.

Conformance-only synthetic Provider IDs MAY be advertised only when the daemon
is explicitly started in conformance mode. They prove an adapter, not a frozen
brand. Production capability advertisements and provider-level matrix claims
use canonical `providerId` values only.

The v1 evidence set freezes the Provider/API-family identity sets and captures
all 1,076 observed rows in `models.v1.json`. The snapshot records its source
commit, extraction version, per-provider source counts, Provider/family
association, normalized capabilities, endpoint strategy, non-secret fixed
headers, compatibility fields, and bounded model limits. The rows freeze what
was observed, not what is supported or permanently guaranteed. A later
versioned catalog may be updated independently without a protocol or Session
version change when existing identifiers retain semantics; it must keep its
own immutable provenance and cannot silently rewrite `models.v1.json`.

`models/list` returns only models that are both present in the selected catalog
or explicit local configuration and usable by an actually configured Provider
route. Absence of credentials, required endpoint variables, project/region
metadata, or a supported authentication path excludes that route from normal
advertisement. Conformance mode may advertise the fixture model assigned by
the runner without reading a live credential.

The Provider manifest v0.2 is the route-level governance record used before
brand claims are added. Each route uses Schema-governed fields for:

- canonical Provider and API-family association;
- fixed, environment-supplied, or bounded-template endpoint strategy;
- allowed authentication profiles and credential source names;
- non-secret required headers;
- model catalog reference and capability overrides;
- an enumerated compatibility-quirk set mechanically equal to the referenced
  catalog rows; a quirk needs a fixture before it can earn conformance.

Arbitrary header maps, arbitrary template expressions, executable callbacks,
and unnamespaced extension objects are forbidden. Credentials are resolved at
the final request boundary and never enter the manifest, catalog, Session,
protocol capability DTO, fixture observation, or diagnostic text.

## Consequences

- The 35-brand manifest cannot be interpreted as current support by itself.
- Family-level conformance can be completed before a brand authentication path,
  but it cannot raise provider-level claims.
- Model catalogs can be refreshed reproducibly without silently expanding the
  frozen Provider/API scope.
- The frozen 1,076-row evidence snapshot is auditable but does not make 1,076
  model support claims; `models/list` remains configuration- and capability-
  filtered.
- All five implementations can consume the same data contract while retaining
  independent native HTTP, SSE, WebSocket, OAuth, and cloud-auth code.
- Existing synthetic fixture IDs remain available only as adapter probes and
  must be documented as aliases rather than canonical live Provider names.
