# Known limitations — KDEConnect.WP81

Every entry states the OS restriction, whether it is model-specific (WinRT vs
Silverlight), whether a partial alternative exists, and whether it is permanent.
Generated: 2026-11-20. Companion: `docs/compatibility-matrix.md`.

---

## L1 — The phone cannot read or receive the clipboard

- **Restriction:** Windows Phone 8.1 WinRT has no `Clipboard` class at all.
  Three independent sources agree:
  1. Archived MSDN `Clipboard` requirements page, *Applies to: Windows 8.x and
     Windows Phone 8.x*, **"Minimum supported phone: None supported"** —
     <https://web.archive.org/web/20150626102847/http://msdn.microsoft.com/en-us/library/windows/apps/windows.applicationmodel.datatransfer.clipboard.aspx>
  2. Microsoft Press errata for *Windows Runtime via C#*: *"The HUGE difference
     is that Windows Phone does not support the Clipboard class, so Windows
     Phone apps cannot manipulate the Clipboard at all."*
  3. Microsoft's Silverlight→WinRT mapping page lists the Clipboard under
     Silverlight-only APIs with *"no Windows Phone Store app equivalent"*
     (`dn642082`).
  Microsoft's Windows-Phone WinRT type inventory lists
  `Windows.ApplicationModel.DataTransfer` types but **no `Clipboard`**.
- **Model-specific:** yes — this is the WinRT model. Windows Phone 8.1
  *Silverlight* has `System.Windows.Clipboard`, but it is **write-only** and
  belongs to a different project model; this project is WinRT and the two
  models must not be mixed (`wp81-reverse-porting` §0 rule 3). Even switching
  models would only buy write-only clipboard support, not the read side this
  plugin needs.
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

## L9 — Several theme *styles* that exist on Windows 8.1 are absent on Phone 8.1

- **Restriction:** the phone's resource dictionary is not the desktop's.
  `{ThemeResource CaptionTextBlockStyle}` parses on Windows and **throws at
  runtime on the phone**: `XamlParseException: Cannot find a Resource with the
  Name/Key CaptionTextBlockStyle`. Because it throws while the page is being
  built, the whole page fails to load - the app shows the splash and then
  either dies or lands on the fallback screen.
- **Evidence:** observed on a Nokia device against this build
  (`UnhandledException -> XamlParseException ... CaptionTextBlockStyle
  [Line: 34 Position: 58]`, caught by the crash log this project writes to
  `LocalFolder\crash.log`).
- **What this client does instead:** no XAML in this repository uses a
  `*TextBlockStyle` or `AppBarBackgroundThemeBrush` key any more. Sizes and
  weights are set as literal `FontSize`/`FontWeight`, card backgrounds are a
  literal colour, and code-behind never reaches into
  `Application.Current.Resources[...]`. The single remaining theme key is
  `ApplicationPageBackgroundThemeBrush`, which is proven to resolve on device.
- **Model-specific:** yes (phone). Believed absent from the Phone 8.1 theme
  dictionary; the desktop dictionary does not tell you this.
- **Partial alternative:** reintroduce individual keys only after confirming
  them on the device; a missing key is a runtime crash, not a build error, so
  it cannot be caught by the compiler.

## L10 — Members verified absent / present on the Phone 8.1 surface

Each "absent" entry below cost a Windows round-trip to discover, because none of
them can be caught without the phone SDK. They are recorded so they are not
re-learned.

**Confirmed ABSENT on Windows Phone 8.1** (desktop/UWP has them):

| Member | Failure |
|---|---|
| `{ThemeResource *TextBlockStyle}` (`Caption`, `Body`, `Subheader`, `Header`, `Title`) | runtime `XamlParseException: Cannot find a Resource with the Name/Key ...` while building the page — see L9 |
| `{ThemeResource AppBarBackgroundThemeBrush}` | avoided for the same reason |
| `ContentDialog.DefaultButton` | `CS1061` |
| `ContentDialogButton` (enum) | `CS0103` |
| `Windows.Storage.DownloadsFolder` | `CS0103` — the class does not exist at all |
| `Windows.ApplicationModel.Core.CoreApplication.RequestRestartAsync` | UWP only |
| `KnownFolders.DocumentsLibrary` | reachable only with `documentsLibrary`, which a Phone Store app cannot declare |

**Confirmed PRESENT and working in this app** (safe to use):

`ContentDialog.PrimaryButtonText`, `ContentDialog.SecondaryButtonText`,
`ContentDialogButtonClickEventArgs`, `TextBox.PlaceholderText`,
`TextBox.AcceptsReturn`, `CommandBar.ClosedDisplayMode`,
`CommandBar.PrimaryCommands`, `ListView.IsItemClickEnabled`,
`TextBlock.CharacterSpacing`, `ManipulationMode`/`ManipulationDelta`,
`Tapped`/`DoubleTapped`, `Slider.StepFrequency`, `ProgressRing.IsActive`,
`{ThemeResource ApplicationPageBackgroundThemeBrush}`.

Nothing here is inferred from documentation; the "present" list is what this
build actually renders on a Nokia device.

## L11 — The app must activate promptly or it is terminated silently

- **Behaviour:** Windows Phone terminates an app that has not activated its UI
  within a short deadline. The termination raises **no managed exception**, so
  `Application.UnhandledException` never fires and `crash.log` stays empty. The
  app simply disappears after the splash screen.
- **How this client triggered it:** startup generated an RSA key pair *before*
  showing any UI. On this hardware that takes seconds, which is long enough to
  miss the deadline on a first run.
- **Fix:** `OnLaunched` now installs a code-built "Starting..." screen and calls
  `Window.Current.Activate()` **before** any identity work, then replaces the
  content with the real page once services are ready. The starting screen has no
  XAML resources and no bindings, so it cannot fail to parse.
- **Diagnosis when it still vanishes:** `startup.log` in `LocalFolder` records one
  line per startup step. The last line written names the step that was running.
  It is shown in the app's Startup report dialog, which is why "no crash log"
  is no longer the same thing as "no information".

## L12 - Sockets outlive the process for a moment, so a fast relaunch loses the ports

- **Symptom:** the second launch reports
  `TCP listener on 1716 failed (Only one usage of each socket address ...)`
  and `UDP discovery on 1716 failed (A method was called at an unexpected time,
  0x8000000E)`. Discovery and inbound links then do not work for that run.
- **Cause:** the previous instance has not finished releasing its sockets.
  WinRT reports the UDP case as `0x8000000E` rather than `EADDRINUSE`, which
  reads like a coding error and is not one.
- **Fix:** both binds retry on a short backoff (0 / 250 / 500 / 1000 / 2000 ms
  for TCP, 0 / 300 / 700 / 1500 / 2500 ms for UDP), each attempt with a fresh
  socket, and each failure is written to `startup.log`. A launch that succeeds
  on the third attempt records `tcp: bound on attempt 3` rather than looking
  identical to a first-try success.
