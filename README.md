# DeepUninstall

**A free, open-source uninstaller for Windows that removes programs completely, and safely.**

Most programs leave things behind when you uninstall them: folders in AppData, registry keys,
services, scheduled tasks, startup entries, dead right-click menu items, firewall rules and
shortcuts that point nowhere. DeepUninstall runs the program's own uninstaller first, then finds
and removes what it left behind, without touching anything that belongs to Windows or to other
programs.

![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4) ![Windows 10/11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4) ![License: MIT](https://img.shields.io/badge/License-MIT-green)

> **Status: early preview (0.1).** The engine is tested against real installs, but this is new
> software that deletes files and registry keys. Keep the restore point option on, and review the
> leftover list before you click Remove.

## Features

**A modern Windows 11 app**
- Fluent design with Mica, and light/dark mode that follows your Windows setting.
- Program list with icons, search, sorting and a details panel; Backups page to restore registry entries from any past cleanup.
- Nothing runs in the background: no service, no tray icon, no startup entry, no telemetry.

**Uninstalling**
- Lists every installed program (per-machine 64-bit, per-machine 32-bit and per-user), with icons, sizes, dates and installer type.
- Waits for the uninstaller to *really* finish. Uninstallers built with Inno Setup, NSIS and InstallShield copy themselves to `%TEMP%` and exit immediately. DeepUninstall runs them inside a Windows Job Object and tracks every child process, so the leftover scan never starts while the real uninstaller is still running.
- Fixes MSI entries that open the *repair* dialog (`MsiExec /I{…}`) by always using `/X{…}`.
- Silent uninstall for MSI, Inno Setup, NSIS, WiX Burn bundles and Squirrel apps.
- Force removal for programs whose uninstaller is missing or broken.
- Batch uninstall: select several programs and they're processed one after another.
- Detects an uninstall that was cancelled or failed (the program is still registered) and stops before cleanup instead of deleting files the program still uses.

**Finding leftovers**
- Install folder, plus folders named after the program in Program Files, ProgramData, AppData (Roaming, Local, LocalLow), `AppData\Local\Programs` and VirtualStore.
- Registry settings in `HKCU\Software` and `HKLM\Software` (64 and 32-bit views), including `Software\<Publisher>\<Program>`.
- Start Menu, Desktop and taskbar shortcuts that point into the install folder, or are broken.
- Windows services and scheduled tasks that run from the install folder.
- Startup entries (`Run`, `RunOnce`) and their Task Manager "Startup apps" state.
- Shell integration: COM components, right-click menu handlers, icon overlays, preview and thumbnail handlers, file types, protocols, "Open with" entries, and default-programs registrations.
- App Paths entries and Windows Firewall rules.

**Reports**
- After every cleanup, save a report of everything that was found and removed, as a web page or a text file.
- A copy is kept with the backups, so you can open it later from the Backups page.

**Registry Cleaner**
- **Quick Clean** (one click), **Safe**, **Moderate** and **Advanced** scans, plus a **Managed** scan where you pick the checks.
- 11 checks: dead startup entries, App Paths, "Open with" programs, the program name cache, shared DLL counters, broken uninstall entries, file type handlers, firewall rules, shell extension approvals, COM components and empty settings keys.
- Only flags entries with proof they're dead: the file they point to is definitely missing from a fixed local drive. Unplugged USB drives, network paths and Windows' own entries are never touched.
- Everything removed is backed up and can be restored from the Backups page. To be honest about it: registry cleaning rarely makes Windows faster; it tidies up after removed software.

**Safety first**
- **Confidence levels.** Every leftover is rated High, Medium or Low, with a plain-English reason. Only High-confidence items are pre-selected.
- **Strict name matching.** Names must match exactly after normalisation. "Fire" never matches "Firefox". Generic names ("Common", "Update", "Tools"…) are ignored, and so are names shared with another installed program.
- **Version aware.** Removing Python 3.13 will not flag a "Python 3.14" folder, and family names like "Python" are only offered as Medium.
- **Hard-wired protection.** A safety guard checks every item twice (when found and right before removal). It refuses `C:\Windows`, Program Files and AppData themselves, user folders, other programs' install folders, essential file types such as `txtfile` and `exefile`, and system registry areas.
- **Undo options.** Optional System Restore point, files go to the Recycle Bin, and every registry key or value is exported to a `.reg` file first (double-click to restore). Backups live in `%LocalAppData%\DeepUninstall\Backups`.
- **Locked files** are scheduled for deletion at the next restart instead of failing.
- Junctions and symlinks are removed as links and never followed.

## Download

Get the latest version from the [Releases](../../releases) page:

| File | What it is |
| --- | --- |
| `DeepUninstall-Setup-<version>.exe` | **Installer (recommended).** Adds a Start Menu shortcut, an optional desktop icon, and optionally puts the `deepuninstall` CLI on your PATH. |
| `DeepUninstall-<version>-portable-win-x64.exe` | Portable app. A single file, nothing to install. |
| `deepuninstall-cli-<version>-win-x64.zip` | The command-line tool on its own. |

No .NET installation is needed. DeepUninstall needs administrator rights (uninstallers, services and machine-wide settings all require them), so Windows will ask for permission when it starts.

The builds are not code-signed yet, so Windows SmartScreen may show "Windows protected your PC". Click **More info → Run anyway**.

## Testing

See [TESTING.md](TESTING.md). It includes a harmless fake program with a deliberately sloppy uninstaller, and a script that installs it, removes it with DeepUninstall and checks that nothing was left behind.

## Command line

The `deepuninstall` CLI uses the same engine, which is handy for scripting and for previewing what a cleanup would do:

```
deepuninstall list [filter]                 List installed programs
deepuninstall info <program>                Show details and the uninstall commands
deepuninstall scan <program>                Dry run: show what the leftover scan finds right now
deepuninstall uninstall <program> [options] Uninstall, then clean up leftovers
deepuninstall registry [safe|moderate|advanced] [--clean] [--yes]
                                            Scan the registry for dead entries (scan only unless --clean)

  --silent            Use the program's silent uninstall mode when available
  --force             Skip the program's uninstaller (for broken entries) and clean up directly
  --include-medium    Also remove medium-confidence leftovers
  --yes               Do not ask for confirmation before removing leftovers
  --no-restore-point  Do not create a System Restore point
  --permanent         Delete files permanently instead of using the Recycle Bin
```

`scan` changes nothing, so it's a safe way to see what DeepUninstall would consider a program's footprint.

## Building from source

Requirements: Windows 10 or 11 and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
git clone https://github.com/zimba7768/DeepUninstall.git
cd DeepUninstall
dotnet build
dotnet test
```

Run the desktop app from `src\DeepUninstall.App\bin\Debug\net9.0-windows\DeepUninstall.exe`, or the CLI from `src\DeepUninstall.Cli\bin\Debug\net9.0-windows\deepuninstall.exe`.

To build the release files (portable app, CLI and installer) into `artifacts\`:

```powershell
winget install JRSoftware.InnoSetup   # once
.\installer\build.ps1 -Version 0.2.0
```

## Project layout

| Path | What it is |
| --- | --- |
| `src/DeepUninstall.Core` | The engine: program discovery, uninstaller runner, leftover scanner, safety guard, removal with backups |
| `src/DeepUninstall.App` | WPF desktop app |
| `src/DeepUninstall.Cli` | Command-line tool |
| `tests/DeepUninstall.Core.Tests` | Unit tests (xUnit) |
| `tests/Fixtures/LeftoverTestApp` | Fake program with a sloppy uninstaller, plus the end-to-end test script |
| `installer` | Inno Setup installer script and the release build script |

Inside the engine:

- `Discovery/`: reads the Uninstall registry keys and detects the installer type.
- `Uninstall/`: parses uninstall command lines, builds silent commands, and runs the uninstaller in a Job Object.
- `Matching/NameMatcher.cs`: decides whether a folder or key name belongs to a program.
- `Leftovers/`: the scanner and its probes (files, registry, shortcuts, services, tasks, autoruns, shell integration, firewall).
- `Safety/SafetyGuard.cs`: what may never be deleted.
- `Removal/`: removal, Recycle Bin, reboot-time deletion, `.reg` backups.

## Roadmap

- Install monitoring: snapshot the system before and after an install for a perfect uninstall later
- Microsoft Store / MSIX apps and Windows features
- PATH and environment variable cleanup
- Browser extension and toolbar removal
- Optional on-demand install tracking (snapshot before and after a setup; nothing resident)
- Localisation
- Signed releases and winget package

Ideas and pull requests are welcome; see [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE). DeepUninstall is provided as is, without warranty. You are responsible for reviewing what you remove.
