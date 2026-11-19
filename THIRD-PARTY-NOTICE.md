# Third-party notices

## Bouncy Castle

This project redistributes one binary:

| File | Origin | Version | SHA-256 |
|---|---|---|---|
| `lib/BouncyCastle.Crypto.dll` | `Portable.BouncyCastle` NuGet package, folder `lib/portable-net40+sl5+win8+wp8+wpa81` (PCL Profile328) | 1.8.2 | `84eae0ef772217f5421c24a34c313e9843a40f19740a8c85b95126ee77df75c2` |

Package page: <https://www.nuget.org/packages/Portable.BouncyCastle/1.8.2>
Upstream: <https://www.bouncycastle.org/csharp/> ·
<https://github.com/bcgit/bc-csharp>

Licence: the Bouncy Castle licence (<https://www.bouncycastle.org/csharp/licence.html>),
which is an MIT-style licence approved by the OSI. Copyright (c) 2000-2018
Legion of the Bouncy Castle Inc.

Why this specific build: it is the newest Bouncy Castle release whose package
contains a `lib` folder that targets `wpa81` — Windows Phone 8.1 WinRT — and it
has no package dependencies. Versions from 1.8.3 onward ship only
`net40`/`netstandard*`, neither of which VS2013 can consume for a phone project.
See `docs/tls-backend.md`.

No private key, certificate, pairing record or signing material is derived
from this library or shipped alongside it.
