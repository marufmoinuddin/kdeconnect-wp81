# KDE Connect for Windows Phone 8.1

A KDE Connect client for **Windows Phone 8.1**, written in C# for the **WinRT**
project model in **Visual Studio 2013**. It speaks the KDE Connect wire protocol
so that a Windows Phone can pair with, and exchange packets with, stock KDE
Connect on a desktop, on the local network.

> **Status: in development. The protocol engine is written and tested; the
> application shell is not.** See [Status](#status) before assuming anything
> below runs on a device — several gates are explicitly **NOT RUN**.

## Why this exists

The stock KDE Connect has no Windows Phone client. The obvious route — write
it in .NET against `SslStream` and TLS — does not work, because Windows Phone
8.1 WinRT refuses to supply a client certificate to a TLS handshake, and KDE
Connect requires **mutual TLS with certificate pinning**. That is not a
preference in the protocol; it is the trust model.

So this project verifies each assumption against the platform before writing to
it, and where the platform cannot do the job it supplies the missing piece
itself (a Bouncy Castle TLS backend that runs as either endpoint).

The full evidence trail — what the platform supports, what it does not, and
which decisions came from which source — is in [`docs/`](docs/):

| Document | Contents |
|---|---|
| [`docs/protocol.md`](docs/protocol.md) | The wire contract: discovery, handshake, TLS roles, pairing, plugins |
| [`docs/compatibility-matrix.md`](docs/compatibility-matrix.md) | Every feature marked possible / impossible / conditionally possible, with the source |
| [`docs/known-limitations.md`](docs/known-limitations.md) | Honest list of what this client cannot do on WP8.1, and why |
| [`docs/tls-backend.md`](docs/tls-backend.md) | The mutual-TLS backend, its trust rules and its limits |
| [`docs/baseline.md`](docs/baseline.md) | What was in the starting project |

## Status

| Gate | State |
|---|---|
| Protocol core implemented | ✅ |
| Mutual-TLS backend implemented | ✅ |
| Link establishment implemented | ✅ |
| Persistence implemented | ✅ |
| Host test suite passing (356 assertions, C# 5) | ✅ |
| Project structure validated for VS2013 | ✅ 0 errors |
| Windows build (MSBuild, ARM), signed `.appx` produced | ✅ |
| Installed and launched on a WP8.1 phone | ✅ |
| Application UI written | ✅ |
| Plugins: ping, battery, clipboard (out), media, touchpad, presenter, volume, find-my-phone | ✅ |
| Discovery over UDP broadcast | implemented, **not verified on a live LAN** |
| **Paired against stock KDE Connect** | ❌ **NOT RUN** |
| **End-to-end feature test against a desktop** | ❌ **NOT RUN** |
| File transfer (payload) | ❌ not implemented |

The code in this repository has been compiled at the **C# 5 language level**
and exercised against a real TLS stack on a desktop, but it has **never been
compiled by Visual Studio 2013 or run on a Windows Phone**. Treat every claim
above as scoped to what the tests actually cover.

## Repository layout

```
KDEConnect.WP81.sln              Visual Studio 2013 solution
KDEConnect.WP81/                 the phone project
  Core/                          protocol core — portable, no WinRT types
  Transport/                     TCP, UDP discovery, TLS link establishment
  Security/                      certificate generation, Bouncy Castle TLS backend
  lib/BouncyCastle.Crypto.dll    vendored TLS implementation (see notice below)
docs/                            evidence and design documents
hosttests/                       host-side test suite (NOT part of the solution)
```

`Core/`, `Transport/` and `Security/` are deliberately free of `Windows.*`
types wherever possible, which is what lets the host test suite exercise the
real handshake code rather than a mock.

## Building (Windows, Visual Studio 2013)

Prerequisites: **Visual Studio 2013 Update 5** with the **Windows Phone 8.1
tools** installed.

Copy the **whole repository folder** — the project references
`..\lib\BouncyCastle.Crypto.dll`, so the `lib/` directory must travel with it.

Open `KDEConnect.WP81.sln`, then either build from the IDE or:

```
"C:\Program Files (x86)\MSBuild\12.0\Bin\MSBuild.exe" "KDEConnect.WP81.sln" /t:Rebuild /p:Configuration=Debug /p:Platform="Any CPU" /v:minimal
```

`Debug | Any CPU` is the configuration the original blank project was verified
to build in, so it isolates this project's code from any packaging problem. For
a device package:

```
... /t:Rebuild /p:Configuration=Release /p:Platform=ARM
```

Package with **bundle = Never** and a **release certificate**.

## Running the tests

The test suite lives outside the phone project on purpose: it targets .NET 8
and must never be referenced by the WP8.1 `.csproj`.

```
cd hosttests
dotnet build
dotnet run
```

Static XAML checks live in the repository too, because the failures they catch
are invisible to a C# compiler and only surface as a `XamlParseException` on the
device:

```
python3 tools/xaml-check.py .
```

It rejects malformed XAML, a `StaticResource` that references a key declared
later in the same file (`WinRT` resolves resources in document order), a
`ThemeResource` key outside the confirmed Phone 8.1 set, members known to be
missing on the phone (`docs/known-limitations.md` L9 and L10), and a
`Page.*` attached-property element nested inside the content tree
(`WMC0010: Unknown attachable member 'Page.BottomAppBar' on element 'Grid'`).

### Icons

The tile and logo assets are generated, not drawn by hand, so they can be
changed consistently:

```
python3 tools/make-icons.py          # writes KDEConnect.WP81/Assets/*.scale-240.png
```

One white mark on a transparent background, sized and centred from its own ink
bounds: the phone silhouette plus two arcs. Transparent tiles take the system
accent colour, which is what keeps them looking native rather than pasted. The
splash screen keeps a dark bed because a fully transparent splash renders as a
white flash. Requires Pillow.

It compiles the phone project's own `Core/`, `Transport/` (portable files) and
`Security/` sources with **`LangVersion=5`**, so anything that passes here is
also valid for the VS2013 compiler. It covers JSON framing, packets, pairing
policy, the handshake validation matrix, capability gating, rate limiting, the
buffered line reader, persistence, and the complete link establishment
sequence in both directions over loopback with real TLS.

## What is not implemented yet

- **File transfer** (payload TLS on ports 1739-1764), send or receive
- Clipboard *receive* — WP8.1 has no clipboard API, so this is impossible, not pending
- Album art on the media page (needs a second HTTP fetch per track)
- Find-my-phone raises a full-screen alert rather than a ringtone: no background
  audio, and the app ships no sound asset

Discovery works over UDP broadcast with subnet-directed fallback, plus a manual
"add device by IP" path that unicasts our identity to one host when broadcast is
blocked. None of it has been exercised against a real desktop yet, so treat it as
implemented-but-unproven rather than known-good.

See [`docs/known-limitations.md`](docs/known-limitations.md) for the platform
constraints that shape the list above, and the compatibility matrix for the
full feature-by-feature position.

## Third-party

`lib/BouncyCastle.Crypto.dll` is **Bouncy Castle 1.8.2**, vendored as a binary
because it is the newest release that ships a Windows Phone 8.1 (PCL) build.
Its provenance, hashes and licence terms are recorded in
[`THIRD-PARTY-NOTICE.md`](THIRD-PARTY-NOTICE.md).
