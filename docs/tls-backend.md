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
| Present a **client certificate** | **NO** | `StreamSocket.UpgradeToSslAsync` has no local-certificate parameter |
| Custom validation callback | **NO** | only `StreamSocketControl.IgnorableServerCertificateErrors` (an ignore list) plus post-hoc inspection of `StreamSocketInformation.ServerCertificate` |
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

**NOT proven — must not be claimed until they run:**

1. That VS2013 + NuGet 2.x actually restores `Portable.BouncyCastle` 1.8.2 into
   this project and that the PCL compiles against the wpa81 reference surface.
   → Windows build gate.
2. That the compiled ARM binary runs the handshake on a physical phone.
   → device gate.
3. That BC's TLS stack interoperates with Qt `QSslSocket` on the G14
   (protocol version and cipher-suite negotiation). BC 1.8.x negotiates
   TLS 1.0–1.2; Qt's defaults and Android's `TLSv1.2` pin
   (`SslHelper.kt:165`) must be matched by configuration, not assumed.
   → real-peer gate.

Until gates 1–3 pass, direct pairing status is **BLOCKED**, not DEPLOYMENT_READY.

## 5. Fallback if the package does not restore

Recorded so the next action is small and specific:

1. Vendor the single `BouncyCastle.Crypto.dll` from
   `lib/portable-net40+sl5+win8+wp8+wpa81/` into the repo as a binary
   reference (`<Reference Include="BouncyCastle.Crypto"><HintPath>…</HintPath></Reference>`).
   Licence (MIT) permits this; a `THIRD-PARTY-NOTICE` is required.
2. If the PCL surface mismatches, compile Bouncy Castle's own sources into the
   project with the portable compilation symbols — only after confirming the
   source revision's C# language level is ≤ 5.

Option 1 is strongly preferred: it is one file, no restore, no build-symbol
guessing.
