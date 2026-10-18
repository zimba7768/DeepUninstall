# Testing DeepUninstall

DeepUninstall deletes things, so test it on something you don't care about first.

## 1. Unit tests

```powershell
dotnet test
```

## 2. Automated end-to-end test (recommended)

`tests\Fixtures\LeftoverTestApp` contains **Leftover Test App**, a harmless fake program whose uninstaller
is deliberately sloppy. It leaves behind AppData folders, registry settings, a startup entry, a file type,
an "Open with" entry, an App Paths entry, a desktop shortcut and a log folder, just like a lot of real software.

This script builds it, installs it for the current user (no admin needed), uninstalls it with the
DeepUninstall CLI, and checks that every leftover is gone:

```powershell
powershell -ExecutionPolicy Bypass -File tests\Fixtures\LeftoverTestApp\Test-EndToEnd.ps1
```

You need [Inno Setup 6](https://jrsoftware.org/isinfo.php) (`winget install JRSoftware.InnoSetup`).
The script ends with `END-TO-END TEST PASSED` when everything was removed.

## 3. Try the desktop app by hand

1. Build the test app installer: `ISCC.exe tests\Fixtures\LeftoverTestApp\LeftoverTestApp.iss`
2. Run `artifacts\fixtures\LeftoverTestApp-Setup.exe`.
   Add `/ALLUSERS` (from an admin terminal) to install it machine-wide, which also creates a scheduled task and HKLM entries.
3. Open DeepUninstall, select **Leftover Test App** and click **Uninstall**.
4. Review the leftover list and remove it. Everything should be listed as High confidence.

## 4. Preview what DeepUninstall would remove for a real program

The `scan` command is a dry run. It changes nothing:

```powershell
deepuninstall scan "Program name"
```

While the program is still installed, it lists everything that would count as a leftover if it remained
after uninstalling. This is the safest way to check the matcher against your own software. Please report
anything that looks wrong using the "Wrong leftover" issue template.

## 5. Test in Windows Sandbox (safest for real programs)

Windows 11 Pro includes **Windows Sandbox**, a throwaway copy of Windows that is wiped when you close it.
Enable it under *Turn Windows features on or off → Windows Sandbox*, then copy the DeepUninstall installer
and a real program's installer into the sandbox, install both, and try uninstalling. Nothing you do there
affects your real PC.
