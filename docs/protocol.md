# Protocol contract — KDE Connect as implemented by this client

Reference implementations (read-only, exact commits verified in `docs/baseline.md` §6):

- **D** = `kdeconnect-kde` @ `69ce9c5c49ce209600873bf10ddf61bfe4dfab1c`
- **A** = `kdeconnect-android` @ `189525cd18906b6b25f97a241329e86a11e4aced`

Citation format: `D:core/backends/lan/lanlinkprovider.cpp:347` (path + line of the
pinned checkout) or `D:<path> <symbol>`.

> Scope note: this contract describes what the **reference code at those two
> commits** does. It is not a claim about the KDE Connect version installed on
> the G14; recording that version is an open validation item.

---

## 1. Ports, addresses, discovery

| Channel | Value | Evidence |
|---|---|---|
| UDP discovery port (send **and** receive) | `1716` | `D:core/backends/lan/lanlinkprovider.h:75` `UDP_PORT = 1716` |
| TCP port range for the main link | `1716 … 1764` | `D:.../lanlinkprovider.h:76-77` `MIN_TCP_PORT`/`MAX_TCP_PORT` |
| TCP payload (file) port range | `1739 … 1764` | `D:core/backends/lan/compositeuploadjob.h:72-73` `MIN_PORT`/`MAX_PORT` |
| Broadcast target | `255.255.255.255` (and per-interface broadcast), plus configured custom devices | `D:.../lanlinkprovider.cpp:217`, `:252-260`; `A:.../LanLinkProvider.kt:515` |
| mDNS | `_kdeconnect._tcp` via Avahi/mdnsh (desktop) or `Dnssd` (Windows) / Android `NsdManager` | `D:.../lanlinkprovider.cpp:61-73`; Windows `Dnssd` is explicitly noted as a TODO at `D:.../lanlinkprovider.cpp:62-63` |

Behaviour:

1. On start and on network change the desktop broadcasts one UDP datagram
   containing an identity-type packet with `tcpPort` = the port its TCP server
   bound (`D:.../lanlinkprovider.cpp:141-156`, `:198-207`, `:252-260`).
   **There is no periodic UDP rebroadcast** — only `onStart()` and
   `debouncedOnNetworkChange()`.
2. A receiver of a UDP packet connects out to `sender_ip:tcpPort`
   (`D:.../lanlinkprovider.cpp:264-298` → `deviceDiscovered()` → `connectToHost`, `:347`).
3. If that outbound connect fails, the receiver replies with **its own**
   discovery packet to the sender (`D:.../lanlinkprovider.cpp:350-355`
   `recoverConnectionError()`), so the two sides can reverse the connection
   direction. This is the protocol's built-in "I cannot be reached, tell me
   your port" mechanism.
4. Packets from loopback are ignored unless in test mode (`D:...:274`).
5. `protocolVersion < 8` is refused (`D:...:315-318`, `:552-556`); a trusted
   device that *lowers* its protocol version is refused
   (`D:.../lanlinkprovider.cpp:329-333`, `isProtocolDowngrade()` `:576-580`).

### 1.1 Rate limits (interop-critical)

Both references rate-limit by **IP** and by **device id**, with a
"same key again inside N ms ⇒ drop" rule:

| | desktop | android |
|---|---|---|
| window | `500 ms` (`D:.../lanlinkprovider.cpp:49`) | `1000 ms` (`A:.../LanLinkProvider.kt:671`) |
| tracked keys | `1024` (`D:...:51`) | `255` (`A:...:673`) |
| unpaired connection cap | `42` (`D:...:47`) | `42` (`A:...:674`) |

The desktop applies `rateLimitByIp(sender)` when it **receives a UDP packet**
(`D:.../lanlinkprovider.cpp:278`) and again on `peerAddress()` when it
**accepts the TCP connection** (`D:.../lanlinkprovider.cpp:478`).

> **Interop rule derived from that code:** if this client sends a UDP discovery
> packet and then opens a TCP connection to the same peer within the peer's
> rate-limit window, the desktop drops the TCP connection. This client must
> therefore wait **> 600 ms** (window is 500 ms) between its own UDP send and
> the corresponding outbound TCP connect, or must not send UDP at all before
> connecting to a broadcast it merely received. Implemented as
> `RateLimitGuard.MinimumUdpToTcpDelay = 700 ms` (see `Transport/RateLimitGuard.cs`).

---

## 2. Packet framing

`D:core/networkpacket.cpp:43-63` (`serialize`) and `:65-93` (`unserialize`);
`A:.../NetworkPacket.kt:206-221`.

- Encoding: **UTF-8**, JSON in **compact** form, terminated by a single
  `'\n'` (`D:networkpacket.cpp:55-59`).
- One packet per line; readers use `readLine()` and cap the line length
  (`D:.../landevicelink.cpp:127-137`; `A:.../LanLinkProvider.kt:164`).
- Envelope fields:

  | field | type | meaning |
  |---|---|---|
  | `id` | number | sender's `currentMSecsSinceEpoch()` (`D:networkpacket.cpp:46`) |
  | `type` | string | packet type, required; empty ⇒ parse failure (`D:...:76-79`) |
  | `body` | object | payload fields |
  | `payloadSize` | number | only present when the packet carries a payload (`D:...:50-53`); `-1` = endless stream (`D:core/networkpacket.h:85`) |
  | `payloadTransferInfo` | object | only present with a payload; currently `{"port": <int>}` (`D:.../compositeuploadjob.cpp:142-144`) |

- Number handling: receivers accept an integer or a double for integer fields
  (`A:.../NetworkPacket.kt:71-78` `getIntOrNull`), and booleans accept
  `"true"`/`"false"` case-insensitively (`A:...:100-110`).
- `deviceId` present in `body` is scrubbed of characters that are not legal in
  D-Bus object paths before use (`D:networkpacket.cpp:85-90`,
  `DBusHelper::filterNonExportableCharacters`).
- **Frame size limits** (this client's own, chosen to be at least as strict as
  the references):

  | limit | value | basis |
  |---|---|---|
  | identity/connection frame | 8 KiB | `D:.../lanlinkprovider.cpp:45` `MAX_IDENTITY_PACKET_SIZE = 8192` (Android allows 512 KiB, `A:...:668`) |
  | any application frame | 1 MiB (this client) | desktop's ceiling is 32 MiB (`D:.../landevicelink.cpp:22` `MAX_PACKET_SIZE`); this client uses the tighter value and must not advertise features needing more |
  | UDP datagram | 512 KiB ceiling on receive (this client) | `A:...:669` `MAX_UDP_PACKET_SIZE = 512*1024` |
  | concurrent unpaired links | 42 | `D:...:47`, `A:...:674` |

### 2.1 Packet type / capability model

- Capabilities **are packet type strings**. Plugin metadata declares
  `X-KdeConnect-OutgoingPacketType` and `X-KdeConnect-SupportedPacketType`
  (`D:plugins/ping/kdeconnect_ping.json:166-171`), and the identity packet
  carries them as `outgoingCapabilities` / `incomingCapabilities`
  (`D:core/deviceinfo.h:139-149`).
- A device may only **send** a type that the *peer* declares it accepts:
  `Device::sendPacket` refuses anything that is not a protocol packet and not
  in the peer's `incomingCapabilities` (`D:core/device.cpp:390-393`).
- A device only **delivers** a type it itself declared:
  `Device::privateReceivedPacket` dispatches on `m_pluginsByIncomingCapability`
  and drops unknown types, closing any payload socket (`D:core/device.cpp:404-425`).
- Any non-protocol packet received while **not paired** causes the desktop to
  send an unpair (`D:core/device.cpp:421-424`).

---

## 3. Identity, connection and secure-identity packets

All three are `type = "kdeconnect.identity"`. Fields come from
`D:core/deviceinfo.h:118-149`.

| packet | sent where | fields |
|---|---|---|
| **UDP discovery** `toUdpDiscoveryPacket(tcpPort)` | UDP 1716 | `deviceId`, `deviceName`, `protocolVersion`, `tcpPort` |
| **Connection** `toConnectionPacket(target, targetVersion)` | first plaintext line on a TCP connection the sender initiated | `deviceId`, `deviceName`, `protocolVersion`, `targetDeviceId`, `targetProtocolVersion` |
| **Identity** `toIdentityPacket()` | first line **inside** TLS | `deviceId`, `deviceName`, `deviceType`, `protocolVersion`, `incomingCapabilities[]`, `outgoingCapabilities[]` |

Validation (`D:core/deviceinfo.h:172-193`):

- `deviceId` must match `^[a-zA-Z0-9_-]{32,38}$`.
- identity packet additionally requires a non-empty `deviceName` after
  `filterName()`; `filterName` strips ``["',;:.!?()[]<>]`` and truncates to 32
  characters (`D:.../deviceinfo.h:165-170`).
- `deviceType` ∈ `unknown|desktop|laptop|phone|tablet|tv`
  (`D:.../deviceinfo.h:26-53`); this client sends `phone`.
- `protocolVersion` = **8** (`D:core/networkpacket.cpp:33`
  `NetworkPacket::s_protocolVersion = 8`).

---

## 4. Connection sequence and TLS roles

**The roles are inverted relative to convention.** Stated verbatim by Android:

> "If I'm the TCP server I will be the SSL client and vice-versa."
> `A:.../LanLinkProvider.kt:330-331`

and by the desktop's own log strings:

> "Starting server SSL (I'm the client TCP socket)" — `D:.../lanlinkprovider.cpp:379`
> "Starting client SSL (I'm the server TCP socket)" — `D:.../lanlinkprovider.cpp:563`

| who opens the TCP connection | TLS role | evidence |
|---|---|---|
| **TCP client** (the one that dialed) | **TLS server** — sends its cert as the *server* certificate, calls `startServerEncryption()` | `D:.../lanlinkprovider.cpp:337-388` |
| **TCP server** (the one that accepted) | **TLS client** — calls `startClientEncryption()` | `D:.../lanlinkprovider.cpp:471-574` |

### 4.1 Sequence (protocol version 8)

```
A (dialer)                                    B (acceptor)
----------- UDP discovery (optional) -------->   (B learns A's tcpPort)
   |                                              |
   |============ TCP connect ====================>|
   |--- connection packet (plaintext, 1 line) --->|   D:...:367-369  /  A:...:160-192
   |                                              |   validates targetDeviceId,
   |                                              |   targetProtocolVersion
   |<============ TLS handshake =================>|   A = TLS server, B = TLS client
   |   both sides present their certificate      |   D:...:630-654 configureSslSocket
   |--- identity (plaintext JSON, inside TLS) -->|   D:...:444-446
   |<-- identity (plaintext JSON, inside TLS) ----|   D:...:405-441, 1 s deadline
   |   both check: protocolVersion unchanged,    |   D:...:425-436
   |   deviceId unchanged vs the plaintext one   |
   |   both check: peer cert CN == deviceId      |   D:...:691-699 addLink()
   |============ link established ==============|
   |<--- kdeconnect.pair (optional) ------------>|
   |<=========== plugin packets ================>|
```

Timeouts and hard checks:

- Plaintext connection packet must arrive within **1000 ms**
  (`D:.../lanlinkprovider.cpp:491-499`).
- Secure identity must arrive within **1000 ms** of the handshake completing
  (`D:.../lanlinkprovider.cpp:396-403`).
- `protocolVersion` and `deviceId` must be **identical** in the plaintext and
  secure identity packets, otherwise the socket is aborted
  (`D:.../lanlinkprovider.cpp:425-436`; same in `A:...:354-365`).
- `targetDeviceId`, if present, must equal the local device id, and
  `targetProtocolVersion`, if present, must equal the local protocol version
  (`D:.../lanlinkprovider.cpp:530-541`).
- The peer certificate's **subject display name must equal the `deviceId`**
  from the identity packet; otherwise the link is aborted
  (`D:.../lanlinkprovider.cpp:691-699`). The certificate is therefore bound to
  the device id by construction: the cert is generated with `CN = deviceId`.

### 4.2 Why this is hard on Windows Phone 8.1

Consequences of §4 for a client that must interoperate with **stock** KDE
Connect:

1. It must be able to act as a **TLS server** (when it dials out) *and* as a
   **TLS client** (when it accepts).
2. It must **present its own certificate** in both roles — as the server
   certificate when it dials, and as the *client* certificate when it accepts.
   The acceptor path reads `socket->peerCertificate()` on a TLS **client**
   connection, i.e. it expects the dialer's *client* certificate
   (`D:.../lanlinkprovider.cpp:437`, `:693`).
3. The desktop as TLS server requests a client certificate: `VerifyPeer` when
   the device is already trusted, `QueryPeer` when it is not
   (`D:.../lanlinkprovider.cpp:637-643`). Android mirrors this with
   `needClientAuth` / `wantClientAuth` (`A:.../SslHelper.kt:209-221`).
4. It must be able to **verify or pin a self-signed certificate** that is not
   in any system trust store, and to *reject* a changed certificate for an
   already-paired device (`D:.../lanlinkprovider.cpp:637-643`,
   `D:core/backends/lan/landevicelink.cpp:146-158` for the payload socket).

The Windows Phone 8.1 WinRT socket API (`Windows.Networking.Sockets.StreamSocket`)
provides **client-side TLS only**, with no API to supply a client certificate
and no certificate-validation callback. That is the single hardest gate in this
port and is tracked in `docs/feature-matrix.md` (row *TLS backend*) and
`docs/validation-matrix.md`. Everything else in this contract is implementable
on the platform.

---

## 5. Certificates, keys and trust

Generation (`D:core/sslhelper.cpp`, `A:.../SslHelper.kt:65-140`):

| property | value | evidence |
|---|---|---|
| key | RSA 2048 (desktop also offers EC P-256) | `D:core/sslhelper.cpp:84-137` (`generateRsaPrivateKey`, `NID_X9_62_prime256v1` for EC) |
| cert | self-signed X.509 v3 | `D:.../sslhelper.cpp:139-257` |
| subject / issuer | `CN=<deviceId>`, `O=KDE`, `OU=KDE Connect` | `D:.../sslhelper.cpp:178-205`; `A:.../SslHelper.kt:109-113` |
| validity | notBefore = now − 1 year, notAfter = now + 10 years | `D:.../sslhelper.cpp:207-211`; `A:...:114-115` |
| signature | SHA-512 | `D:.../sslhelper.cpp:233` (`EVP_sha512`); `A:...:125` (`SHA512withRSA`) |
| serial | random 160-bit (desktop) / `1` (Android) | `D:.../sslhelper.cpp:161`; `A:...:117` |
| TLS version | "TLS up to 1.2" — Android explicitly avoids TLS 1.3 | `A:.../SslHelper.kt:165` |

Trust model — **trust on first use, with explicit user approval**:

| state | TLS behaviour | evidence |
|---|---|---|
| unknown peer | desktop: `QueryPeer` (accept any cert, then check CN) / Android: `trustAllCerts` during handshake | `D:.../lanlinkprovider.cpp:641-643`; `A:.../SslHelper.kt:57-63,174-177` |
| paired peer | desktop: `CA = stored peer cert`, `VerifyPeer` / Android: `TrustManager` initialised with only that stored cert | `D:.../lanlinkprovider.cpp:637-641`; `A:.../SslHelper.kt:156-172` |
| paired peer, **different** cert presented | link refused (desktop aborts when the stored certificate does not match) | `D:.../lanlinkprovider.cpp:709-714`; `A:.../LanLinkProvider.kt:415-419` |

This client's trust policy (strictly stronger than the references, and required
by the mission brief — **no accept-all callback, no silent re-trust**):

1. **Unknown peer.** The handshake may complete, but the connection is not
   usable until *all* of the following hold: the peer certificate parses; its
   `CN` equals the `deviceId` carried in the secure identity packet; it is
   inside its validity window; the secure identity's `deviceId`/`protocolVersion`
   match the plaintext ones; **and** the operator explicitly approves the pair
   request. Nothing is persisted before approval.
2. **Paired peer.** The presented certificate must be byte-identical (DER) to
   the pinned certificate for that `deviceId`. Any difference aborts the
   connection and is surfaced as *certificate changed* — it never silently
   re-pins.
3. **Unpair** deletes the pinned certificate and the device record, so the old
   trust record can no longer grant access.

### 5.1 Pairing handshake

`D:core/backends/pairinghandler.cpp`, `A` equivalent.

| packet | body | meaning |
|---|---|---|
| request | `{"pair": true, "timestamp": <epoch **seconds**>}` | `D:.../pairinghandler.cpp:109-114` |
| accept | `{"pair": true}` (no timestamp) | `D:.../pairinghandler.cpp:135` |
| reject / cancel / unpair / timeout | `{"pair": false}` | `D:.../pairinghandler.cpp:156,164,175` |

State machine (`D:.../pairinghandler.cpp:28-84`):

- `NotPaired` + `pair:true` ⇒ require `timestamp` present and
  `|timestamp − now| ≤ 1800 s` (`ALLOWED_TIMESTAMP_TIME_DIFFERENCE_SECONDS`,
  `D:.../pairinghandler.cpp:16,55-59`); mismatch ⇒ *"Device clocks are out of
  sync"*. Then ⇒ `RequestedByPeer`, start timer, notify UI.
- `Requested` (we asked) + `pair:true` ⇒ `Paired`.
- `RequestedByPeer` + `pair:true` ⇒ ignored (second request before timeout).
- `Paired` + `pair:true` **with** a timestamp ⇒ treated as unpair-then-request;
  without a timestamp ⇒ ignored (an accept from an already-trusted device).
- `pair:false` in `Requested`/`RequestedByPeer` ⇒ *"Canceled by other peer"*;
  in `Paired` ⇒ unpaired.
- Timeout: **30 000 ms** (`D:.../pairinghandler.h:20`), on expiry sends
  `pair:false` (`D:.../pairinghandler.cpp:173-179`).

**Verification code** (`D:.../pairinghandler.cpp:188-209`) — displayed for
manual comparison, not enforced by the protocol:

```
a = own certificate public key DER
b = peer  certificate public key DER
if (a < b) swap so that a >= b        // byte-wise comparison
code = SHA256( a || b || str(timestamp) ).hex().upper()[0..7]
```

This client computes and displays the same value so the operator can compare
it against the desktop's. Trust is still granted only by explicit approval.

---

## 6. Payload (file) transfer

Sender and receiver roles are **fixed by the protocol**, independent of who
opened the main connection:

```
sender                                          receiver
  |  opens TCP listener on 1739..1764            |
  |--- kdeconnect.share.request (on the MAIN     |
  |    TLS link) with payloadSize +              |
  |    payloadTransferInfo {"port": N} --------->|
  |                                              |  dials sender_ip:N
  |<============ TCP connect ====================|
  |<============ TLS handshake ==================|  sender = TLS **server**
  |   sender startServerEncryption()             |  D:.../compositeuploadjob.cpp:231-233
  |   receiver connectToHostEncrypted(...,trusted)|  D:.../landevicelink.cpp:146-158
  |--- raw payload bytes (NOT framed) ---------->|  D:.../uploadjob.cpp:63-86
  |   sender closes socket when input exhausted   |  D:.../uploadjob.cpp:71-75,88-92
```

Details:

- The announced packet is a **header**: the payload device is stripped before
  re-sending (`D:.../compositeuploadjob.cpp:138-145`), and extra fields
  `numberOfFiles`, `totalPayloadSize` are added.
- One listener serves a queue of files; the next file is announced while the
  current one transfers (`D:.../compositeuploadjob.cpp:149-162`).
- Waiting for the receiver is bounded to **30 000 ms**
  (`D:.../compositeuploadjob.cpp:36`, `:294-305`).
- Receiver reads exactly `payloadSize` bytes and treats a short/long read as an
  error (`A:.../CompositeReceiveFileJob.kt:274-275`).
- The payload socket is always TLS with `trusted=true`, i.e. pinned
  (`D:.../landevicelink.cpp:149`).
- Chunk size on send is 4096 bytes (`D:.../uploadjob.cpp:68`).

**Implication for this client:** an *outgoing* file requires acting as a TLS
**server** on ports 1739-1764; an *incoming* file requires acting as a TLS
**client** that pins the sender. Both fall under the §4.2 gate.

Sanitisation requirements implemented by this client (mission brief §Phase 3D):

- Filenames are stripped of path separators, `..`, control characters and
  reserved names; the file is created only inside the app's own download
  folder; an existing file is never overwritten (a numeric suffix is added).
- `payloadSize` is checked against a configured maximum **before** the
  connection is opened; a mismatch between announced and actual bytes fails the
  transfer and deletes the partial file.

---

## 7. Plugin packets used by this client

Directions are from the phone's point of view. `out` = phone sends,
`in` = phone receives. Field lists are taken from the reference code cited in
the last column.

| plugin | type | dir | body fields | reference |
|---|---|---|---|---|
| ping | `kdeconnect.ping` | out, in | `message?` (string) | `D:plugins/ping/pingplugin.cpp:30-45` |
| battery | `kdeconnect.battery` | out, in | `currentCharge` (int 0-100), `isCharging` (bool), `thresholdEvent?` | `D:plugins/battery/batteryplugin.cpp`; `A:.../battery/BatteryPlugin.kt` |
| media control | `kdeconnect.mpris.request` | out | `player`, `action` ∈ `Play\|Pause\|Stop\|Next\|Previous\|PlayPause`, or `requestNowPlaying`, `requestVolume`, `requestPlayerList`, `Seek` (int ms), `SetPosition` (int ms), `setVolume` (int), `albumArtUrl` | `D:plugins/mprisremote/mprisremoteplugin.cpp:96-140`; action names `D:.../mprisremoteplayernowplaying-macos.cpp:128-158` |
| media state | `kdeconnect.mpris` | in | `player`, `playerList[]`, `title`, `album`, `artist`, `albumArtUrl`, `isPlaying`, `length`, `pos`, `rate`, `volume`, `canPlay/canPause/canGoNext/canGoPrevious/canSeek`, `transferringAlbumArt` | `D:plugins/mprisremote/mprisremoteplayer.cpp`; `D:.../mprisremoteplugin.cpp:32-83` |
| touchpad | `kdeconnect.mousepad.request` | out | `dx`, `dy` (double, relative), `scroll`, `singleclick`, `doubleclick`, `rightclick`, `middleclick`, `singlehold`, `singlerelease`, `key`, `specialKey`, `alt`, `ctrl`, `shift`, `super` | `D:plugins/mousepad/x11remoteinput.cpp`; `A:.../mousepad/MousePadPlugin.kt` |
| keyboard state | `kdeconnect.mousepad.keyboardstate` | in | modifier state (`state`, `alt`, `ctrl`, `shift`, `super`) | `D:plugins/mousepad/mousepadplugin.cpp` |
| presenter | `kdeconnect.presenter` | out | `dx`, `dy` (float, normalised), `stop` (bool) | `D:plugins/presenter/presenterplugin.cpp:66-107` |
| system volume | `kdeconnect.systemvolume.request` | out | `name`, `volume` (int) **or** `name`, `muted` (bool) | `D:plugins/remotesystemvolume/remotesystemvolumeplugin.cpp:43-57` |
| system volume state | `kdeconnect.systemvolume` | in | `sinkList` (array of `{name, volume, muted, enabled}`) **or** `name` + `volume`/`muted` | `D:.../remotesystemvolumeplugin.cpp:24-41` |
| share (send) | `kdeconnect.share.request` | out | `filename`, `payloadSize`, `payloadTransferInfo{"port"}`, `numberOfFiles`, `totalPayloadSize`, `lastModified?`, `creationTime?`, `open?`, or `text` / `url` for non-file shares | `D:plugins/share/shareplugin.cpp`; `D:.../compositeuploadjob.cpp:131-147` |
| share (receive) | `kdeconnect.share.request` | in | as above; also `kdeconnect.share.request.update` with `numberOfFiles`, `totalPayloadSize` | `D:.../compositeuploadjob.cpp:271-280` |
| clipboard | `kdeconnect.clipboard` | out, in | `content` (string), `timestamp` (ms) | `A:.../clipboard/ClipboardPlugin.kt`; `D:plugins/clipboard/clipboardplugin.cpp` |
| clipboard sync | `kdeconnect.clipboard.connect` | out, in | `timestamp` | `D:plugins/clipboard/kdeconnect_clipboard.json` |
| find my phone | `kdeconnect.findmyphone.request` | in | (empty) | `D:plugins/findmyphone/findmyphoneplugin.h:13` |

Packets this client **does not** advertise (and therefore never sends or
accepts): `kdeconnect.sms.*`, `kdeconnect.telephony*`, `kdeconnect.contacts.*`,
`kdeconnect.notification*`, `kdeconnect.runcommand*`, `kdeconnect.sftp*`,
`kdeconnect.lock*`, `kdeconnect.connectivity_report`, `kdeconnect.digitizer*`,
`kdeconnect.virtualmonitor*`, `kdeconnect.shareinputdevices*`.

---

## 8. Malformed-input and lifecycle rules implemented by this client

Derived from the reference failure paths and the mission brief:

1. Every frame length is bounded (§2); an over-long frame closes the link.
2. A JSON parse failure drops the frame and keeps the link open (desktop
   behaviour, `D:networkpacket.cpp:65-93` returns false and the caller
   `continue`s) — except during the handshake, where it aborts.
3. Unknown `type` ⇒ drop, close any payload socket (`D:core/device.cpp:409-417`).
4. Non-protocol packet while unpaired ⇒ drop **and** send `pair:false`
   (`D:core/device.cpp:421-424`).
5. Duplicate `deviceId` on a new link ⇒ the old link is reused only if the
   certificate matches, otherwise aborted (`D:.../lanlinkprovider.cpp:706-717`).
6. Partial reads: framing is line-based with an accumulator, so a packet split
   across TCP segments is reassembled; a packet arriving together with the
   identity packet must still be processed (`D:.../landevicelink.cpp:53-56`).
7. Disconnects and stale callbacks: each link owns its socket; a callback that
   arrives for a superseded socket is ignored (desktop compares `m_socket`
   before acting, `D:.../compositeuploadjob.cpp:170-206`).
8. Suspend/resume: sockets are torn down on suspend and the link is
   re-established on resume by re-running discovery; bounded reconnect with
   exponential backoff, capped attempts.
