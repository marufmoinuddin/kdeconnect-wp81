# Compatibility Matrix — KDEConnect.WP81

Generated: 2026-11-20 · Source revision: `kdeconnect-kde@69ce9c5c`, `kdeconnect-android@189525cd`
· Target: Windows Phone 8.1 **WinRT** (`WindowsPhoneApp`, 8.1, C# 5) · Protocol version: **8**

**Source app:** KDE Connect (KDE) · **Evidence index:** `wpa81-winrt-api-inventory.md`
(13-item API inventory with per-claim URLs), `docs/tls-backend.md` (TLS evidence).

## Classification legend

| # | Classification | Meaning |
|---|---|---|
| 1 | Directly portable | Existing code/dependency works with minimal changes |
| 2 | Adaptable | Same behavior, different WP 8.1 API |
| 3 | Requires reimplementation | Original platform code unusable, equivalent behaviour possible |
| 4 | Partially supported | Only part of the original behavior is possible |
| 5 | Privileged access required | Needs capabilities an ordinary app does not get |
| 6 | Unsupported | The platform prevents it |
| 7 | Unverified | Not yet investigated — **not a decision; blocks implementation** |

## Matrix

| Feature | Source implementation (file:line) | Class | WP 8.1 target API / approach | Model | Capability | Evidence | Notes / partial alternative |
|---|---|---|---|---|---|---|---|
| UDP discovery — **receive** (bind 1716) | `lanlinkprovider.cpp:132`, `:264-298` | 2 | `DatagramSocket.BindServiceNameAsync("1716")` + `MessageReceived` | WinRT | `internetClientServer` | inventory §1 (S1/S3/S5/S6) | Inbound "critical ports" list is unpublished → treat 1716 as unverified-but-likely-OK; test on device |
| UDP discovery — **send broadcast** | `lanlinkprovider.cpp:198-260` | 4 | `DatagramSocket.SendToAsync` to `255.255.255.255`, **plus fallbacks**: subnet-directed `x.x.x.255` and `JoinMulticastGroup` | WinRT | `internetClientServer` | inventory §1: MS never documents broadcast-send on 8.1; SO 23504957 (limited broadcast not received) | Class 4 not 2: broadcast-send is **INCONCLUSIVE** in MS docs. Implement all three paths and pick at runtime |
| TCP link — accept inbound | `lanlinkprovider.cpp:471-501` | 2 | `StreamSocketListener.BindServiceNameAsync` (1716–1764) | WinRT | `internetClientServer` | inventory §2 (S1/S4/S5) | Required: this side plays TLS client |
| TCP link — dial outbound | `lanlinkprovider.cpp:337-348` | 2 | `StreamSocket.ConnectAsync` | WinRT | `internetClientServer` | inventory §2 | Required: this side plays TLS server |
| **TLS server role** | `lanlinkprovider.cpp:379-388` | 3 | **Not possible in WinRT** → in-process `TlsServerProtocol.Accept()` from Bouncy Castle | WinRT + PCL | `internetClientServer` | `docs/tls-backend.md` §2 (MS: *"SSL/TLS negotiation as a server is not implemented for a StreamSocket"*) | The single hardest gate; see §3 of that doc |
| **TLS client role** | `lanlinkprovider.cpp:563-574` | 3 | WinRT `UpgradeToSslAsync` exists but cannot pin or present a cert → in-process `TlsClientProtocol.Connect()` | WinRT + PCL | `internetClientServer` | `docs/tls-backend.md` §2 | |
| **Present our certificate** | `lanlinkprovider.cpp:634-635`, `SslHelper.kt:209-221` | 3 | `TlsAuthentication.GetClientCredentials()` / `AbstractTlsServer.GetCredentials()` returning `DefaultTlsSignerCredentials` | WinRT + PCL | — | `docs/tls-backend.md` §3.2 | WinRT has no client-certificate API at all; this is why direct pairing is blocked without BC |
| Peer certificate pinning / validation | `lanlinkprovider.cpp:637-643`, `:691-699`, `:709-714` | 3 | `TlsAuthentication.NotifyServerCertificate()` + `NotifyClientCertificate()`, then byte-compare against the pinned DER | WinRT + PCL | — | `docs/tls-backend.md` §3.2 | WinRT offers only an ignore-list (`IgnorableServerCertificateErrors`) — not acceptable, and not needed once BC is in-process |
| Self-signed certificate generation (CN=deviceId) | `sslhelper.cpp:139-257`, `SslHelper.kt:65-140` | 3 | `X509V3CertificateGenerator` + `RsaKeyPairGenerator` (both present in the `wpa81` build) | WinRT + PCL | — | `docs/tls-backend.md` §3.1 | Matches reference: CN=deviceId, O=KDE, OU=KDE Connect, −1y/+10y, SHA-512 |
| Certificate / key persistence | `kdeconnectconfig.cpp`, `RsaHelper.kt` | 2 | `ApplicationData.Current.LocalFolder` (files, not `LocalSettings`) | WinRT | none | inventory §8 default app-data folders | Never in Git, logs or the package |
| Packet framing + JSON (identity/pair/plugins) | `networkpacket.cpp:43-93` | 1 | Already implemented in `Core/`, host-tested | — | — | `hosttests/` 190 assertions | No WinRT dependency by design |
| Pairing state machine + verification code | `pairinghandler.cpp:28-209` | 1 | Already implemented in `Core/PairingHandler.cs`, host-tested | — | — | `hosttests/` | Includes 1800 s clock-skew rule and 30 s timeout |
| Device list / manual IP / link states / reconnect | `device.cpp`, `lanlinkprovider.cpp` | 2 | `Core/` logic + `StreamSocket` + suspend/resume handlers | WinRT | `internetClientServer` | inventory §4 (S8/S9) | Sockets may be closed on suspend → reconnect-on-resume, bounded backoff |
| **Background / screen-off operation** | Android services | 6 | none — no raw-socket background agent; `ControlChannelTrigger` explicitly unsupported on phone | WinRT | — | inventory §4 (S7/S8) | Foreground-only + reconnect on resume. See `known-limitations.md` L2 |
| ping (both directions) | `pingplugin.cpp:30-45` | 1 | pure packets | — | — | `docs/protocol.md` §7 | |
| battery reporting | `batteryplugin.cpp` | 2 | `Windows.Phone.Devices.Power.Battery` (`RemainingChargePercent`) — **not** `Windows.Devices.Power.Battery` | WinRT | none | inventory §5 (S1/S26) | Phone-only namespace; the desktop `Battery` class is not documented for 8.1 |
| media control (play/pause/next/prev, volume) | `mprisremoteplugin.cpp:96-140` | 2 | pure packets `kdeconnect.mpris.request` ↔ `kdeconnect.mpris` | WinRT | `internetClientServer` | `docs/protocol.md` §7 | WP8.1 never touches its own SMTC for this — it is a remote control only |
| system volume control | `remotesystemvolumeplugin.cpp:43-57` | 2 | pure packets | WinRT | `internetClientServer` | `docs/protocol.md` §7 | |
| touchpad / keyboard / presenter | `x11remoteinput.cpp`, `presenterplugin.cpp:66-107` | 2 | pure packets | WinRT | `internetClientServer` | `docs/protocol.md` §7 | |
| find-my-phone (ring) | `findmyphoneplugin.h:13` | 4 | `MediaElement.Play()` / `BackgroundMediaPlayer.Current` in **foreground**; `Windows.Phone.Devices.Notification.VibrationDevice` as an extra | WinRT | none | inventory §10/§11 (S1/S10/S26) | `AudioGraph` does not exist on 8.1; no documented max-volume API |
| **file receive** | `shareplugin.cpp`, `landevicelink.cpp:142-158` | 2 | dial the sender's payload port as TLS client; store via `StorageFolder` / `DownloadsFolder` (create-your-own files need no capability) | WinRT + PCL | `internetClientServer` | inventory §8 (S13/S22) | `KnownFolders.Downloads` does **not** exist → use `Windows.Storage.DownloadsFolder` |
| **file send** | `compositeuploadjob.cpp:74-234` | 4→3 | payload sender must be a **TLS server** on 1739–1764 → only possible with the BC backend | WinRT + PCL | `internetClientServer` | `docs/tls-backend.md` | Class 4 until BC compiles on device, then 3. Source file chosen with `FileOpenPicker` (suspends the app — handle continuation) |
| send text to desktop clipboard | `clipboardplugin.cpp` | 2 | send `kdeconnect.clipboard {content}` — text typed into **our own** UI; no clipboard API needed | WinRT | `internetClientServer` | `docs/known-limitations.md` L1 | |
| **receive** desktop clipboard | `clipboardplugin.cpp` | 6 | none — WP8.1 WinRT has no `Clipboard` class at all | WinRT | — | inventory §6 (S19 Microsoft Press errata: *"Windows Phone does not support the Clipboard class"*) | Not advertised in `incomingCapabilities`; see L1 |
| show desktop notifications as toasts | `sendnotifications/notificationslistener.h` | 4 | in-app display now; `ToastNotificationManager` needs a manifest declaration whose exact 8.1 XML was **not** verified | WinRT | none | inventory §9 (S12) | Toast XML marked class 7 → deferred; foreground display implemented instead |
| **read phone notifications → desktop** | Android `NotificationsPlugin` | 6 | none — no listener API for other apps' notifications on 8.1 | WinRT | — | `wp81-reverse-porting` §3 #3 | Not advertised |
| SMS sync / telephony / contacts | `smsplugin.cpp`, `telephonyplugin.cpp`, `contactsplugin.cpp` | 6 | restricted capabilities (`chat`, `contacts`, `phoneCall`) needing company accounts; compose-dialog and `ContactPicker` only | WinRT | restricted | inventory §12 (S2/S16/S17/S28) | Not advertised; see L3 |
| run commands on the phone | `runcommandplugin.cpp` | 6 | no shell on WP8.1 | WinRT | — | — | Not advertised |
| lock/unlock the phone | `lockdeviceplugin.cpp` | 6 | no API for third-party apps | WinRT | — | — | Not advertised |
| sftp / browse phone files | `sftpplugin.cpp` | 6 | sandbox + no SSH server | WinRT | — | inventory §8 | Not advertised |
| digitizer / virtual monitor / share input devices | `digitizerplugin.cpp`, `virtualmonitorplugin.cpp` | 6 | no such hardware/API surface on phone | WinRT | — | — | Not advertised |
| mDNS discovery | `mdnshdiscovery.cpp` | 7 | `Windows.Networking.ServiceDiscovery.Dnssd` exists on 8.1 but was not verified in this pass | WinRT | `internetClientServer` | `lanlinkprovider.cpp:62-63` notes `Dnssd` as the Windows route | **Deferred**: UDP discovery is sufficient for v1; not implemented, not advertised |
| Toast-capable manifest declaration | — | 7 | exact 8.1 XML not verified | WinRT | — | inventory §9 ("INCONCLUSIVE") | Blocks toast; in-app display used instead |

## Summary counts

| Classification | Count |
|---|---|
| 1 Directly portable | 4 |
| 2 Adaptable | 12 |
| 3 Requires reimplementation | 5 |
| 4 Partially supported | 4 |
| 5 Privileged access required | 0 |
| 6 Unsupported | 9 |
| 7 Unverified | 2 |

**Rules applied:** no implementation work starts on a feature marked 7 (mDNS and
toast are deferred, not built). Every class-6 feature has a
`docs/known-limitations.md` entry and is **absent from the identity packet's
capability lists**, so the desktop will never be told about a stub.
