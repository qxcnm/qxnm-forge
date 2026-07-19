# ADR 0008: Provider credential, origin, template, and redirect boundaries

- Status: Accepted
- Date: 2026-07-14
- Project: qxnm-forge
- Author: 高宏顺 `<18272669457@163.com>`

## Context

Provider configuration combines values with very different trust levels:
public model metadata, secret credentials, cloud credential selectors, endpoint
templates, explicit private-cloud endpoints, and non-secret compatibility
headers. Treating all of them as an arbitrary map would make five independent
implementations disagree about precedence and would create credential leakage,
SSRF, open-redirect, and log-injection risks.

The fixed PI evidence includes ordinary HTTPS endpoints, Cloudflare account and
gateway templates, a Google Vertex location template, Azure models whose base
URL is intentionally empty, AWS/Google ambient credential chains, OAuth, and a
small set of non-secret fixed headers. It does not justify accepting arbitrary
template expressions, silently following redirects, or storing credentials in
the shared manifest or model catalog.

## Decision

### Credential material and precedence

`providers.v1.json` records only credential **source names and kinds**. It MUST
NOT contain a credential value. `models.v1.json`, Session journals, protocol
capabilities, fixtures, events, diagnostics, exception text, and artifacts also
MUST NOT contain credential values.

An implementation resolves credentials at the final request boundary. An
explicit per-request credential or an explicitly selected stored credential
takes precedence over a named environment source; ambient cloud chains are
consulted only by routes that list the corresponding auth profile. Empty
values mean unavailable, not an anonymous credential. A route with no usable
auth profile or required endpoint configuration MUST NOT be advertised by
normal `models/list`.

Environment access is closed over the manifest names. Implementations MUST NOT
enumerate or serialize the whole process environment. Names classified as
`secret` are redacted even when their value does not resemble a known token.
Credential-file, selector, and metadata-endpoint values are sensitive
configuration and MUST be omitted from ordinary diagnostics. Tests use canary
values and assert absence rather than snapshotting a secret-shaped string.

OAuth browser redirects are part of the named OAuth profile, not Provider HTTP
redirect permission. OAuth implementations MUST use state/PKCE where the
upstream flow supports them, bind loopback callbacks to a local interface, and
never reuse an OAuth redirect URI as a model-request endpoint.

### Endpoint identity and template substitution

Every catalog endpoint has exactly one strategy:

- `fixed`: the normalized HTTPS URL is catalog evidence;
- `template`: the normalized HTTPS URL contains only declared variables;
- `runtime-required`: the PI snapshot supplied no URL and the route cannot be
  used until explicit configuration supplies one.

Catalog and runtime URLs MUST use HTTPS outside the isolated loopback
conformance exception. They MUST NOT contain user information, control
characters, a query string, or a fragment. URL parsing occurs after bounded
template substitution and before DNS resolution. Scheme-relative URLs,
backslashes, percent-encoded authority delimiters, and values that change the
parsed authority are rejected.

Only three snapshot variables are admitted:

| Variable | Value policy | Bound source |
| --- | --- | --- |
| `CLOUDFLARE_ACCOUNT_ID` | `cloudflare-identifier-v1`: ASCII letters, digits, `_` or `-`, 1–128 characters | same-named configuration value |
| `CLOUDFLARE_GATEWAY_ID` | `cloudflare-identifier-v1`: ASCII letters, digits, `_` or `-`, 1–128 characters | same-named configuration value |
| `location` | `google-location-v1`: lowercase ASCII letters, digits or `-`, 1–63 characters; begins and ends alphanumeric | `GOOGLE_CLOUD_LOCATION` |

Substitution is literal and performed exactly once. Values are not executable
expressions and cannot introduce `/`, `.`, `%`, `@`, `:`, `?`, `#`, `{`, or
`}`. A new variable or value policy requires a Schema change, an ADR update,
and hostile endpoint fixtures before implementations may consume it.

Azure's empty snapshot endpoint is represented as `runtime-required`; qxnm-forge
does not invent a public Azure resource name. `AZURE_OPENAI_BASE_URL` or the
derived `AZURE_OPENAI_RESOURCE_NAME` route is explicit configuration. Standard
Azure service suffixes may be accepted directly; sovereign, private-link, or
custom origins require an explicit origin allow decision and MUST NOT be
inferred from model text. AWS region and Google project/location selection use
their named cloud route and cannot enable an unrelated generic URL override.

### Origin comparison and redirects

The authorization origin is the tuple of lowercase scheme, IDNA-normalized
host, and effective port after configuration and template validation. Paths do
not broaden that origin. DNS resolution, proxy selection, and connection reuse
MUST NOT silently replace this authorization decision.

Provider model requests default to **no automatic redirects**. A 3xx response
is a structured, non-retryable Provider error unless a later family-specific
specification names an exact transition and conformance fixture. In particular:

- credentials, cookies, cloud signatures, and credential-bearing custom
  headers MUST NOT be forwarded to a different origin;
- 301/302/303 MUST NOT rewrite a credential-bearing POST into a GET;
- 307/308 MUST NOT replay a request body or non-idempotent tool-related turn at
  another origin;
- a same-origin redirect is still rejected in v1, avoiding cross-language HTTP
  client differences and redirect loops;
- redirect targets are never learned into the catalog or Session.

AWS container credential endpoints and OAuth authorization/token endpoints are
separate named credential transports. They follow their SDK/profile security
rules and never inherit permission from a model route. A credential endpoint
cannot become the Provider request origin.

### Header policy

Credential placement belongs to the API-family adapter or an explicitly named
header policy. Model rows may contain only the Schema-enumerated non-secret
fixed headers observed in the snapshot. `Authorization`, `Proxy-Authorization`,
`Cookie`, `Set-Cookie`, and `x-api-key` are forbidden in the model catalog.
Caller-supplied headers cannot override Host, content length, signing headers,
or the selected credential placement without a separate approved extension.

Cloudflare AI Gateway uses its named policy: place the configured token in
`cf-aig-authorization` and suppress family-default `Authorization`/`x-api-key`
injection. Other fixed catalog headers are merged only by the
`api-family-default-with-model-headers` policy.

## Consequences

- The shared data is useful without access to real credentials and remains
  safe to validate in default CI.
- Five HTTP stacks produce the same redirect and origin outcome instead of
  inheriting library defaults.
- Private and sovereign cloud endpoints remain possible, but require explicit
  configuration and origin trust rather than catalog guesswork.
- Adding a template, credential source, special header, or redirect transition
  is a public security change requiring Schema, ADR, fixture, and conformance
  updates.
- `implemented` transport code does not become a Provider support claim until
  its auth/origin behavior is conformant; live credentials are tested only by
  separately enabled smoke tests.
