# Known limitations — KDEConnect.WP81

Every entry states the OS restriction, whether it is model-specific (WinRT vs
Silverlight), whether a partial alternative exists, and whether it is permanent.
Generated: 2026-11-20. Companion: `docs/compatibility-matrix.md`.

---

## L1 — The phone cannot read or receive the clipboard

- **Restriction:** Windows Phone 8.1 WinRT has no `Clipboard` class at all.
  Microsoft's own statement (Microsoft Press errata for *Windows Runtime via C#*,
  "Updates for Windows 8.1"): *"The HUGE difference is that Windows Phone does
  not support the Clipboard class, so Windows Phone apps cannot manipulate the
  Clipboard at all."*
  Microsoft's Windows-Phone WinRT type inventory lists
  `Windows.ApplicationModel.DataTransfer` types but **no `Clipboard`**.
- **Model-specific:** yes — this is the WinRT model. Windows Phone 8.1
  *Silverlight* has `System.Windows.Clipboard`, but it is **write-only** and
  belongs to a different project model; this project is WinRT and the two
  models must not be mixed (`wp81-reverse-porting` §0 rule 3).
- **Partial alternative — yes, and it is implemented:**
  - **Outgoing works.** The operator types the text into our own UI and we send
    `kdeconnect.clipboard {content}`. The desktop's clipboard plugin writes it
    locally. No clipboard API is involved, so nothing is lost on that side.
  - **Incoming does not.** A clipboard pushed by the desktop would have nowhere
    to go, so `kdeconnect.clipboard` and `kdeconnect.clipboard.connect` are
    deliberately **not** in our `incomingCapabilities`. The desktop will never
    send them.
- **Permanent:** yes, for as long as the target is WP8.1 WinRT.

## L2 — No background or screen-off operation

- **Restriction:** once suspended, *"existing network connections may be
  closed"* and incoming packets may be dropped (Microsoft, *Staying connected in
  the background*, 8.x/Phone 8.x). `ControlChannelTrigger` is *"not supported on
  Windows Phone"*. There is no documented socket background agent for wpa81, and
  `TimeTrigger` granularity is 30 minutes on phone.
- **Model-specific:** yes (phone, both models).
- **Partial alternative — yes:** foreground sessions plus reconnect-on-resume
  with bounded backoff. The app is fully usable while open, and re-establishes
  links when the operator returns to it. A `PushNotificationTrigger` path could
  be explored later but is not implemented.
- **Permanent:** yes, at the OS level.

## L3 — SMS, telephony and contacts are not accessible

- **Restriction:** reading/deleting SMS requires the restricted `chat`
  capability; direct contacts access requires the restricted `contacts`
  capability; call APIs sit behind restricted `phoneCall`. Restricted
  capabilities require a company account / extra onboarding that a normal app
  does not get. The `SmsReceived` system trigger is documented for mobile
  broadband, not the phone SIM.
- **Model-specific:** yes (restricted capabilities are a Store/certification
  concept).
- **Partial alternative — yes, but not useful for KDE Connect:** a compose
  dialog (`ChatMessageManager.ShowComposeSmsMessageAsync`) and a
  `ContactPicker` exist, both user-driven. They do not provide the silent read
  or send that the KDE Connect `sms`/`telephony`/`contacts` plugins require, so
  none of those packet types are advertised.
- **Permanent:** yes for silent access.

## L4 — Cannot read the phone's notifications to forward them

- **Restriction:** Windows Phone 8.1 has no API that lets one app read another
  app's notifications.
- **Model-specific:** yes.
- **Partial alternative — yes, opposite direction:** notifications *sent by the
  desktop* can be shown on the phone (foreground display). The reverse
  direction is impossible. Not advertised.
- **Permanent:** yes.

## L5 — Cannot execute commands, lock the phone, or serve files over sftp

- **Restriction:** WP8.1 has no shell for third-party apps, no API to lock the
  device, and no way to run an SSH/SFTP server inside an app sandbox.
- **Model-specific:** yes.
- **Partial alternative:** none.
- **Permanent:** yes. `kdeconnect.runcommand*`, `kdeconnect.lock*`,
  `kdeconnect.sftp` are not advertised.

## L6 — File send requires the in-process TLS server (conditional)

- **Restriction:** the payload *sender* must be a TLS server on ports
  1739–1764, and WinRT cannot do server-side TLS (see `docs/tls-backend.md` §2).
- **Model-specific:** yes (WinRT socket API).
- **Partial alternative:** the Bouncy Castle backend supplies the server role in
  process. Until that backend is compiled into the phone app and proven on a
  device, **file send is NOT verified** while file receive is.
- **Status:** implementation written against a verified API surface
  (`docs/tls-backend.md` §3.2); gates open — see `docs/validation-matrix.md`.

## L7 — UDP broadcast may not be delivered

- **Restriction:** Microsoft documents no broadcast-send behaviour for
  Windows 8.1/Phone 8.1; community reports say limited-broadcast
  `255.255.255.255` is not received on WP8.1 unless the WLAN adapter is bound,
  and that phones can send it where PCs cannot. The inbound "critical ports"
  list is never published.
- **Model-specific:** yes (phone).
- **Partial alternative — yes:** send to `255.255.255.255`, to the
  subnet-directed broadcast `x.x.x.255`, and to the multicast group; also
  respond to whatever we receive. Manual IP entry is always available as the
  guaranteed path.
- **Status:** to be proven on the device; discovery is class 4, not class 2.

## L8 — Direct pairing depends on an unproven build step

- **Restriction:** none at the OS level — this is a *verification* gap, not a
  platform gap. A `wpa81`-compatible Bouncy Castle build exists and its API was
  read out of the binary, but no Windows build has yet restored, compiled or run
  it.
- **Model-specific:** no.
- **Partial alternative:** vendor the single DLL as a binary reference
  (`docs/tls-backend.md` §5) if NuGet restore fails.
- **Status:** **BLOCKED** until the Windows build gate and the real-peer
  handshake gate pass. Not to be described as working.
