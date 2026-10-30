# OIDC reference

Operator-reported legacy Django OAuth Toolkit settings—not a Keycloak realm export.
Production client IDs and secrets are deliberately omitted. This is legacy
integration evidence, not a production configuration to copy.

| Setting | Reported value |
| --- | --- |
| Client ID / client secret | Deliberately not shared |
| User | Blank |
| Redirect URI | `https://sso.hacklab.fi/realms/tampere/broker/mulysa/endpoint` |
| Post-logout redirect URIs | Blank |
| Client type | Confidential |
| Hash client secret | True |
| Name | `keycloack` |
| Skip authorization | True |
| Algorithm | HMAC with SHA-256 (HS256) |
| Allowed origins | Blank |

## Compatibility notes

- **Name is cosmetic:** `keycloack` can become `Keycloak`. Preserve the broker alias
  **`mulysa`**, which is identity-sensitive.
- **Legacy signing mismatch:** the Django reference's current DOT rejects HS256
  with hashed client secrets; its tests cover that rejection and valid HS256/
  unhashed and RS256/hashed combinations. This does not establish OpenIddict's
  configuration or justify copying the reported legacy combination. The new
  production provider should use persistent asymmetric signing keys and an HTTPS
  issuer, validated with the real broker at cutover; no live changes are authorized
  by this snapshot.
- **Still unknown:** actual issuer, scopes/PKCE, claim mappers, broker/downstream
  identity mappings and session behaviour. The Django reference emits `sub`, `email`,
  `name`; legacy `firstName`/`lastName` mappings need rehearsal, not a guessed split.

## New provider contract

Nulysa is the **OIDC server upstream of Keycloak**, not merely an OIDC login client.
Use OpenIddict with ASP.NET Core Identity, stable subject strings independent of
mutable emails and explicit admission/deletion gates on every relevant path.
Configure the allowed code/refresh flows, S256 PKCE, client authentication, scopes,
registered redirects and signed claims deliberately. Use 365-day idle refresh
expiry renewed by successful refresh, with exchange-time enforcement and safe
expired-record cleanup. Data Protection keys are separate from OIDC signing keys;
both must survive deployments and be protected outside source control.

Endpoint paths and optional-provider configuration must be explicit during S0,
not blindly copied from Django's `/o/` settings. Keep the provider disabled unless
configured, validate required production issuer/key settings when enabled, and
test deployment configuration reaches all processes that require it.

Registry login denial does not revoke existing Keycloak/downstream sessions.
S0 must prove direct protocol behaviour and the synthetic local Keycloak browser
flow. S1 adds real admission/reset/deletion workflows; S6 proves the legacy switch
with retained broker associations and frozen legacy subjects, then operator-assisted
production mapping validation. See [foundation status](foundations.md).

## Implemented S0 provider

OIDC is disabled by default. When enabled, configure these settings privately:

| Environment setting | Meaning |
| --- | --- |
| `Oidc__Enabled=true` | Enable the server endpoints below. |
| `Oidc__Issuer` | Fixed absolute issuer, with trailing `/`; HTTPS in Production. |
| `Oidc__SigningKeyPath` | Persistent RSA private PEM, at least 2048 bits. |
| `Oidc__EncryptionKeyPath` | A different persistent RSA private PEM for private tokens. |
| `Oidc__ClientId`, `Oidc__ClientSecret`, `Oidc__RedirectUri` | Inputs to the operator-only `register-client` command. |

Generate **local development keys only** under the ignored `.local/` directory:

```sh
mkdir -p .local/oidc
chmod 700 .local/oidc
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out .local/oidc/signing.pem
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out .local/oidc/encryption.pem
chmod 600 .local/oidc/*.pem
export Oidc__Enabled=true
export Oidc__Issuer='http://127.0.0.1:5000/'
export Oidc__SigningKeyPath="$PWD/.local/oidc/signing.pem"
export Oidc__EncryptionKeyPath="$PWD/.local/oidc/encryption.pem"
# Supply the synthetic broker ID, secret and exact callback through the environment.
dotnet run --project src/Nulysa -- register-client
```

`register-client` creates a confidential, trusted broker client with code/refresh
grants, registered redirects, `email`/`profile` permissions and required PKCE. It
refuses an existing ID rather than silently overwriting credentials or redirects.
It is a privileged maintenance command, not a public registration route.

| Endpoint | Purpose |
| --- | --- |
| `/.well-known/openid-configuration` | Discovery |
| `/.well-known/jwks` | Public signing keys |
| `/connect/authorize` | Authorization-code flow, account/cookie gate |
| `/connect/token` | Authenticated code redemption/rolling refresh, account gate |
| `/connect/userinfo` | Bearer-token/account gate, scope-limited live claims |

Only S256 challenges are accepted; implicit/password/client-credentials/device
grants are not enabled. Access tokens last one hour. Rolling refresh tokens expire
at 365 idle days; a successful refresh starts a new 365-day lifetime. Expiry is
checked inclusively at the boundary, independently of cleanup. Cookie validation
checks admission/pending deletion on every request, not just the periodic Identity
security-stamp interval. No membership-payment flag is used for login.

The local Keycloak test creates a disposable realm, broker alias `mulysa` and small
downstream client, validates signed tokens and checks both upstream and downstream
subjects after email changes. It deliberately records that an existing Keycloak
session still works after the upstream account is blocked. Synthetic direct DB
changes in this test are not an implementation of S1 email verification or deletion.

### Persistence and initial rotation policy

The RSA keys are independent of the shared Data Protection key ring. No ephemeral
signing/encryption fallback exists. Production also requires a private PFX certificate
to encrypt the Data Protection key ring at rest; provision and back up all three
purposes separately, with restricted mount permissions and separately protected passwords.

S0 loads one signing and one encryption key. **Do not rotate automatically.** Retain
working keys across releases. Replacing encryption keys invalidates outstanding
private codes/refresh tokens; removing a signing key can invalidate consumers of
older signatures. Any initial rotation must be coordinated as planned reauthentication,
with broker validation and rollback keys retained securely. Seamless overlapping
rotation needs implementation/rehearsal before a deployment that requires it.
Likewise, do not replace the Data Protection wrapping certificate or erase the ring
without a reviewed rewrapping/recovery procedure. Public deployment remains gated on
these operational rehearsals; restarting S0 is not a key-rotation operation.
