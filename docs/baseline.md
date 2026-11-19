# Baseline — KDEConnect.WP81

Date of capture: 2026-11-19 (UTC+06:00)

## 1. Project archive

| Item | Status |
|---|---|
| Expected archive path | `/home/maruf/workspace/KDEConnect.WP81.zip` |
| Present? | **NO — the file does not exist at that path.** |
| Supplied input | Extracted project directory delivered as a WebUI attachment: `/home/maruf/.hermes/webui/attachments/ef14eec2f260/KDEConnect.WP81/` |

No replacement project was created. The provided attachment directory was copied
verbatim into the working repository (see §3). The original attachment was left
untouched.

## 2. Inventory of the supplied project (before any change)

Solution root of the attachment:
`.../ef14eec2f260/KDEConnect.WP81/KDEConnect.WP81/`

| File | Notes |
|---|---|
| `KDEConnect.WP81.sln` | VS 2013, Format 12.00, VisualStudioVersion 12.0.40629.0, one project `{C0CA489B-2586-47C0-8F1D-FDBADFB99499}` |
| `KDEConnect.WP81/KDEConnect.WP81.csproj` | ToolsVersion 12.0, `OutputType=AppContainerExe`, `TargetPlatformVersion=8.1`, `TargetPlatformIdentifier=WindowsPhoneApp`, `ProjectTypeGuids={76F1466A-8B6D-4E39-A767-685A06062A39};{FAE04EC0-...}` (Windows Phone 8.1 + C#) |
| `KDEConnect.WP81/Package.appxmanifest` | Identity `Name=9e1c61c5-0950-459c-b7fe-4f4559a7a62b`, `Publisher=CN=Maruf`, `Version=1.0.0.1`, `PhoneProductId=9e1c61c5-...`, capability `internetClientServer` only |
| `KDEConnect.WP81/App.xaml(.cs)` | Blank application template |
| `KDEConnect.WP81/MainPage.xaml(.cs)` | Blank page template, `NavigationCacheMode.Required` |
| `KDEConnect.WP81/Properties/AssemblyInfo.cs` | AssemblyVersion/FileVersion 1.0.0.0 |
| `Assets/*.scale-240.png` | 6 template logos |
| `KDEConnect.WP81.csproj.user` | local, git-ignored |
| `KDEConnect.WP81.v12.suo` | local, git-ignored |

### Solution configurations (as declared in the .sln)

`Debug|Any CPU`, `Debug|ARM`, `Debug|x86`, `Release|Any CPU`, `Release|ARM`,
`Release|x86` — all six map to the same-named project configuration and all
carry `.Build.0` and `.Deploy.0`, so ARM Release **is** a real, mapped
configuration. No invented configurations are required.

### Project configurations (as declared in the .csproj)

| Configuration | PlatformTarget | OutputPath | DefineConstants |
|---|---|---|---|
| Debug\|AnyCPU | AnyCPU | `bin\Debug\` | `DEBUG;TRACE;NETFX_CORE;WINDOWS_PHONE_APP` |
| Release\|AnyCPU | AnyCPU | `bin\Release\` | `TRACE;NETFX_CORE;WINDOWS_PHONE_APP` |
| Debug\|ARM | ARM | `bin\ARM\Debug\` | `DEBUG;TRACE;NETFX_CORE;WINDOWS_PHONE_APP` |
| Release\|ARM | ARM | `bin\ARM\Release\` | `TRACE;NETFX_CORE;WINDOWS_PHONE_APP` |
| Debug\|x86 / Release\|x86 | x86 | `bin\x86\...` | same pattern |

### Build-relevant properties

- `AppxBundle = Never` (already set — this is the proven install route).
- `AppxBundlePlatforms = arm`
- `AppxAutoIncrementPackageRevision = True`
- `MinimumVisualStudioVersion = 12`
- Imports `Microsoft.Windows.UI.Xaml.CSharp.targets` (WinRT XAML targets).
- **No NuGet `packages.config`, no assembly references, no `ProjectReference`.**
  The ItemGroup that would hold references is an empty comment placeholder.
- Compile items: `App.xaml.cs`, `MainPage.xaml.cs`, `Properties\AssemblyInfo.cs`.
  Page items: `App.xaml`, `MainPage.xaml`.

### Sign-off on baseline content

The only signing material visible in the manifest is `Publisher="CN=Maruf"`.
No `.pfx`, no `.p12`, no certificate files were present in the archive (correctly).
Signing identity therefore has to come from the Windows host's local signing
setup when packaging.

## 3. Working repository

Created at `/home/maruf/workspace/KDEConnect.WP81/`.

- Source of the copy: the attachment directory, minus build output
  (`obj/`, `bin/`, `AppPackages/`, `BundleArtifacts/`, `*.suo`).
- The attachment itself was not modified.
- `.gitignore` added: `bin/`, `obj/`, `AppPackages/`, `BundleArtifacts/`,
  `*.suo`, `*.user`, `*.pfx`, `*.p12`, `*.cer`, `.vs/`, `TestResults/`, `*.log`.
- Baseline commit: `b510e02` — *"Baseline: blank VS2013 WP8.1 WinRT project as provided"*.
  15 files tracked, no build output, no keys.

## 4. Build / package artifacts found in the attachment (not tracked in git)

These were part of the supplied directory and were deliberately **not** copied,
because they are stale build output of the blank project:

| Artifact | Observation |
|---|---|
| `AppPackages/KDEConnect.WP81_1.0.0.0_arm_bundle_Debug.appxbundle` | older, bundle |
| `AppPackages/KDEConnect.WP81_1.0.0.1_ARM_Debug.appxupload` | current version |
| `AppPackages/KDEConnect.WP81_1.0.0.1_ARM_Debug_Test/KDEConnect.WP81_1.0.0.1_ARM_Debug.appx` | **standalone ARM .appx, matches the proven install route** |
| `AppPackages/.../KDEConnect.WP81_1.0.0.0_Debug_Test/KDEConnect.WP81_1.0.0.0_arm_Debug.appxbundle` | the bundle whose PacMan install was reported to fail with `0x800710dd` |
| `obj/ARM/Debug/**` | full intermediate tree for ARM Debug |
| `BundleArtifacts/arm.txt` | bundle manifest fragment |

Recorded baseline facts from the mission brief (not re-verified on this host):

- The blank project compiled under `Debug | Any CPU`.
- The standalone ARM `.appx` installed with *Generate app bundle = Never*.
- The earlier ARM Debug `.appxbundle` failed in PacMan with `0x800710dd`.
  That error is **not** reinterpreted here as a confirmed signing failure.

## 5. Toolchain reality on this host

| Tool | Present? |
|---|---|
| `git` 2.43.0 | yes |
| `python3` 3.14.9 | yes |
| `dotnet` SDK | **no** (`~/.dotnet` contains only a `corefx` scratch dir) |
| `mono` / `mcs` / `csc` | **no** |
| `msbuild` / `xbuild` | **no** |
| Windows VM with VS2013 | **not reachable from this session** |

Consequence: no C# compilation of any kind, and no phone build, can be
performed on this host. Everything that requires VS2013 MSBuild, the Windows
Phone 8.1 SDK, a package signing step, or a physical device is recorded as
`NOT RUN` in `docs/validation-matrix.md` with the exact command that has to be
run on the Windows validation host.

## 6. Upstream references (read-only)

Both checkouts were already present in the workspace and were verified to be
exactly at the commits named in the mission brief:

| Repository | Required commit | Observed HEAD | Match |
|---|---|---|---|
| `/home/maruf/workspace/kdeconnect-kde` | `69ce9c5c49ce209600873bf10ddf61bfe4dfab1c` | `69ce9c5c49ce209600873bf10ddf61bfe4dfab1c` | yes |
| `/home/maruf/workspace/kdeconnect-android` | `189525cd18906b6b25f97a241329e86a11e4aced` | `189525cd18906b6b25f97a241329e86a11e4aced` | yes |

Both were read only. No commit was created in, and no file was modified in,
either checkout.

## 7. What the baseline does *not* prove

- It does not prove that the solution builds on the Windows validation host;
  no MSBuild ran here.
- It does not prove anything about the installed KDE Connect version on the
  G14. The upstream HEAD commits above are a **reference**, not evidence of
  what the installed peer sends or accepts. Recording the installed desktop
  version is an open item (see `docs/validation-matrix.md`).
- It does not prove that the recorded PacMan `0x800710dd` bundle failure was a
  signing problem.
