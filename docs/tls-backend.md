# TLS backend — evidence and design

Date: 2026-11-19/20 · Status: **design proven from binaries; not yet compiled into the phone app**

## 1. Why a TLS backend is the critical gate

KDE Connect requires mutual TLS where the certificate *is* the device identity:

- The dialer becomes the TLS **server**, the acceptor becomes the TLS **client**
  (`docs/protocol.md` §4; `lanlinkprovider.cpp:379` / `:563`,
  `LanLinkProvider.kt:330-331`).
- After the handshake both sides read the peer certificate and require
  `subjectDisplayName() == deviceId` (`lanlinkprovider.cpp:691-699`).
- The desktop as TLS server requests a client certificate — `VerifyPeer` when
  the device is already trusted, `QueryPeer` when it is not
  (`lanlinkprovider.cpp:637-643`); Android mirrors this with
  `needClientAuth`/`wantClientAuth` (`SslHelper.kt:209-221`).

So a client must be able to do **all three** of: act as TLS server, act as TLS
client, and present its own certificate.

## 2. What Windows Phone 8.1 WinRT can do — verified

Research (archived Microsoft documentation):

| Capability | Verdict | Evidence |
|---|---|---|
| TLS **server** on `StreamSocketListener` | **NO** | *"This support for SSL/TLS is limited to using the StreamSocket object as the client in the SSL/TLS negotiation. SSL/TLS cannot currently be used by the StreamSocketListener … since the SSL/TLS negotiation as a server is not implemented for a StreamSocket."* — <https://learn.microsoft.com/en-us/previous-versions/windows/apps/jj150597(v=win.10)> |
| `System.Net.Security.SslStream` | **NO** | not in the ".NET for Windows Store apps" 4.5.1 surface (no `System.Net.Security`) |
| `System.Net.Sockets` (`TcpClient`, `Socket`) | **NO** | not in the ".NET for Windows Store apps" surface |
| Present a **client certificate** | **NO** | *"The client support for SSL/TLS does not include the ability to use client certificates."* — <https://learn.microsoft.com/en-us/previous-versions/windows/apps/jj150597(v=win.10)> |
| Custom validation callback | **NO** | only `StreamSocketControl.IgnorableServerCertificateErrors` (an ignore list of `ChainValidationResult`) plus post-hoc inspection of `StreamSocketInformation.ServerCertificate` — no delegate exists (`StreamSocketControl` members, archived 8.1 MSDN: <https://web.archive.org/web/20140130015622/http://msdn.microsoft.com/en-us/library/windows/apps/windows.networking.sockets.streamsocketcontrol.aspx>) |

Also checked and rejected:

- `HttpBaseProtocolFilter.ClientCertificate` *does* send a client certificate, but
  only for `Windows.Web.Http` requests — not for a raw `StreamSocket`
  (<https://learn.microsoft.com/en-us/uwp/api/windows.web.http.filters.httpbaseprotocolfilter.clientcertificate>).
- A self-signed peer fails by default with `0x80096004` ("The signature of the
  certificate cannot be verified"); the only lever is adding `Untrusted` to
  `IgnorableServerCertificateErrors`, which is an ignore-list, not validation.
- Independent confirmation that `StreamSocketListener` has no TLS server role:
  <https://github.com/MicrosoftDocs/winrt-api/issues/1594> and the HITB 2016
  whitepaper *The Windows Phone Freakshow*.
| Plain TCP client / listener | **YES** | `StreamSocket`, `StreamSocketListener` |

**Conclusion:** WinRT alone cannot complete a KDE Connect handshake in either
direction. An in-process TLS stack is mandatory for direct pairing — exactly
the trap the porting skill warns about (§3 #9, §10).

## 3. Bouncy Castle — verified against the actual package binary

Package: **`Portable.BouncyCastle` 1.8.2** (NuGet).

| Property | Value | How verified |
|---|---|---|
| `lib/` folders shipped | `net40`, `netstandard1.0`, `netstandard1.3`, `netstandard2.0`, **`portable-net40+sl5+win8+wp8+wpa81`** | listed from the `.nupkg` on `api.nuget.org` |
| PCL profile | `.NETPortable0.0-Profile328` | `Portable.BouncyCastle.nuspec` |
| Dependencies for that profile | **none** | `<group targetFramework=".NETPortable0.0-Profile328" />` with no children |
| Target framework moniker | `wpa81` = Windows Phone 8.1 WinRT — the exact target of this project | folder name |
| Licence | Bouncy Castle licence (<https://www.bouncycastle.org/csharp/licence.html>), MIT | nuspec `licenseUrl` |
| May a wpa81 project reference a PCL at all? | **YES** | Microsoft's NuGet TFM table lists `portable-net45+win8+wpa81` (Profile111) and `portable-net45+win8+wpa81+wp8` (Profile259) as installable in a *Windows Phone 8.1 (UWP)* project — <https://learn.microsoft.com/en-us/nuget/reference/target-frameworks>; `Microsoft.Bcl` 1.1.10 really ships `lib/portable-net45+win8+wpa81` |
| Type count in that build | 1732 | `System.Reflection.Metadata` enumeration of the DLL |

Earlier versions **do** ship a `wpa81` folder (`1.7.0.1` … `1.8.2`); from
`1.8.3` onward only `net40`/`netstandard*` remain, and `netstandard2.0` cannot
be consumed by VS2013/NuGet 2.x. **1.8.2 is the newest usable version.**

### 3.1 Types present in the `wpa81` build (all confirmed by enumeration)

TLS (namespace `Org.BouncyCastle.Crypto.Tls` — the `Org.BouncyCastle.Tls`
rename only happened in 1.9.0):

```
TlsClientProtocol   TlsServerProtocol   TlsProtocol   TlsContext
AbstractTlsClient   AbstractTlsServer   DefaultTlsClient   DefaultTlsServer
TlsClient  TlsServer  TlsPeer  TlsAuthentication  TlsCredentials
TlsKeyExchange  TlsSignerCredentials  AbstractTlsSignerCredentials
DefaultTlsSignerCredentials  Certificate  CertificateRequest  TlsCompression  TlsCipher
```

Certificates and keys:

```
Org.BouncyCastle.X509.X509V3CertificateGenerator     <- builds our self-signed cert
Org.BouncyCastle.X509.X509Certificate / X509CertificateParser
Org.BouncyCastle.Crypto.Generators.RsaKeyPairGenerator
Org.BouncyCastle.Crypto.Parameters.RsaKeyParameters / RsaPrivateCrtKeyParameters
Org.BouncyCastle.Crypto.ISigner / SignerUtilities / Signers.RsaDigestSigner
Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory
Org.BouncyCastle.Asn1.X509.{X509Name, SubjectPublicKeyInfo, AlgorithmIdentifier,
                            TbsCertificateStructure, X509CertificateStructure, X509Extensions}
```

`Org.BouncyCastle.Cert.X509v3CertificateBuilder` and
`Org.BouncyCastle.Operator.*` are **absent** from the portable build — use
`X509V3CertificateGenerator` instead (it is present).

### 3.2 Exact API to code against (dumped from the DLL, not from memory)

```csharp
// handshake is blocking -> run it off the UI thread
var proto = new TlsClientProtocol(rawStream, new SecureRandom());
proto.Connect(tlsClient);          // TLS CLIENT role  (we accepted the TCP connection)

var proto2 = new TlsServerProtocol(rawStream, new SecureRandom());
proto2.Accept(tlsServer);          // TLS SERVER role  (we dialled the TCP connection)

Stream appData = proto.Stream;     // TlsProtocol.get_Stream() -> application data
proto.Close(); proto.Flush(); proto.IsClosed;
int n = proto.ReadApplicationData(buf, off, len);
```

Role mapping for this protocol (remember: **dialer = TLS server**):

| We … | call | subclass | must override |
|---|---|---|---|
| dialled out | `TlsServerProtocol.Accept()` | `AbstractTlsServer` / `DefaultTlsServer` | `GetCipherSuites()`, `GetCredentials()`, `GetKeyExchange()`, plus `GetCertificateRequest()` and `NotifyClientCertificate()` to request and read the desktop's client certificate |
| accepted inbound | `TlsClientProtocol.Connect()` | `AbstractTlsClient` / `DefaultTlsClient` | `GetCipherSuites()`, `GetKeyExchange()`, `GetAuthentication()` |

Abstract members confirmed by metadata (`MethodAttributes.Abstract`):

- `AbstractTlsClient`: `GetCipherSuites()`, `GetKeyExchange()`, `GetAuthentication()`
- `AbstractTlsServer`: `GetCipherSuites()`, `GetCredentials()`, `GetKeyExchange()`
- `TlsAuthentication`: `NotifyServerCertificate(Certificate)`,
  `GetClientCredentials(CertificateRequest)` → this is how **our certificate is
  presented as a client certificate**, which is the piece WinRT cannot do.

Credentials:

```csharp
new DefaultTlsSignerCredentials(tlsContext, ourCertificate, ourPrivateKey)
new DefaultTlsSignerCredentials(tlsContext, ourCertificate, ourPrivateKey, signatureAndHashAlgorithm)
```

Certificate generation:

```csharp
var gen = new X509V3CertificateGenerator();
gen.SetSerialNumber(...); gen.SetIssuerDN(new X509Name("CN=...,O=KDE,OU=KDE Connect"));
gen.SetSubjectDN(...);     gen.SetNotBefore(now - 1y); gen.SetNotAfter(now + 10y);
gen.SetPublicKey(pub);     gen.SetSignatureAlgorithm("SHA512withRSA");
X509Certificate cert = gen.Generate(privateKey);   // or Generate(ISignatureFactory)
```

This matches the reference exactly: `CN=<deviceId>`, `O=KDE`, `OU=KDE Connect`,
validity −1y/+10y, SHA-512 (`sslhelper.cpp:178-233`, `SslHelper.kt:109-127`).

## 4. What this does and does not prove

**Proven here (Linux, offline):**

- A NuGet build exists whose `lib/` folder explicitly targets `wpa81`.
- That DLL contains a complete TLS implementation in both roles, certificate
  generation, RSA key generation and X.509 parsing.
- Its PCL profile has no package dependencies.
- The exact members required are present and their abstract/virtual flags are
  known, so the implementation can be written against a verified surface.
- **`Security/BouncyCastleTlsBackend.cs` compiles against the real 1.8.2
  assembly with 0 errors / 0 warnings, and completes real mutual-TLS handshakes
  over loopback TCP** — verified by `hosttests/` (215 assertions, all passing):
  both role directions, peer certificate exchange, CN == deviceId on both
  sides, byte-identical pinned certificate, application data crossing the
  tunnel intact, and handshake timeout instead of hanging.

Three defects were found *by those tests* and fixed against the matching
`pcl-v1.8.2` source rather than by guesswork:

| # | Failure | Root cause (source) | Fix |
|---|---|---|---|
| 1 | `internal_error(80)` | `AbstractTlsServer.MaximumVersion` defaults to **TLS 1.1**, so `IsTlsV12 != (CertificateRequest.SupportedSignatureAlgorithms != null)` (`TlsServerProtocol.cs:202`) | override `MaximumVersion` to `TLSv12` and supply the algorithm list conditionally |
| 2 | `ArgumentException: cannot be null for (D)TLS 1.2+` | `DefaultTlsSignerCredentials.cs:36` | pass an explicit `sha256+rsa` |
| 3 | `internal_error(80)` after `GetCredentials` | `TlsRsaKeyExchange.ProcessServerCredentials` requires `TlsEncryptionCredentials`, not a signer (`TlsRsaKeyExchange.cs:37-45`) | server uses `DefaultTlsEncryptionCredentials`; client keeps the signer for `CertificateVerify` |

**NOT proven — must not be claimed until they run:**

1. That VS2013 accepts the vendored `lib/BouncyCastle.Crypto.dll` reference
   (PCL Profile328) against the wpa81 reference surface and that the ARM binary
   builds. → Windows build gate.
2. That the compiled ARM binary runs the handshake on a physical phone.
   → device gate.
3. That BC's TLS stack interoperates with Qt `QSslSocket` on the G14
   (protocol version and cipher-suite negotiation). BC 1.8.x negotiates
   TLS 1.0–1.2; Qt's defaults and Android's `TLSv1.2` pin
   (`SslHelper.kt:165`) must be matched by configuration, not assumed.
   → real-peer gate.

Until gates 1–3 pass, direct pairing status is **BLOCKED**, not DEPLOYMENT_READY.

## 5. Fallback if the package does not restore

Recorded so the next action is small and specific, in preference order:

1. **Vendor the single DLL.** Take `BouncyCastle.Crypto.dll` from
   `lib/portable-net40+sl5+win8+wp8+wpa81/` of `Portable.BouncyCastle` 1.8.2 and
   reference it directly:
   `<Reference Include="BouncyCastle.Crypto"><HintPath>…\BouncyCastle.Crypto.dll</HintPath></Reference>`.
   One file, no NuGet restore, no build-symbol guessing. The licence permits
   redistribution; a `THIRD-PARTY-NOTICE` is required.
2. **Compile Bouncy Castle from source** inside the phone project if the PCL
   surface does not match.

   *Now proven for the language level:* Bouncy Castle **1.9.0** sources
   (`crypto/src/**`, `crypto/bzip2/**`) build with
   `LangVersion=5`, `DefineConstants=PORTABLE;NO_THREADS`, **0 warnings / 0
   errors** — evidence: workspace-local build log at
   `~/.hermes/cache/scratch/bc190_csharp5/build.log`.

   Two caveats keep this at option 2 rather than 1:
   - that build targeted `netstandard2.0`, **not** `wpa81`, so BCL API
     availability on the phone profile is still unproven;
   - Roslyn's `LangVersion=5` is a close proxy for, but not identical to,
     VS2013's `csc`.

   Also note 1.9.0 renamed the TLS namespace to `Org.BouncyCastle.Tls`; the API
   surface in §3.2 was dumped from **1.8.2** (`Org.BouncyCastle.Crypto.Tls`), so
   the two paths are not interchangeable without re-dumping.

Option 1 is preferred: it matches the verified API surface exactly.
