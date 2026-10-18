# Contributing to DeepUninstall

Thanks for helping! Bug reports, false-positive reports, ideas and pull requests are all welcome.

## Reporting a wrong leftover (most valuable!)

If DeepUninstall flags something that does **not** belong to the program, or misses something that does,
please open an issue with:

1. The program name, version and publisher (from `deepuninstall info "<name>"`).
2. The output of `deepuninstall scan "<name>"` (a dry run that changes nothing). Remove anything private.
3. Which item was wrong, and why.

False positives are treated as high-priority bugs.

## Development setup

- Windows 10/11, [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Visual Studio 2022, Rider or VS Code

```powershell
dotnet build
dotnet test
```

The desktop app's manifest requires administrator rights. To debug it, run Visual Studio as administrator,
or set the environment variable `__COMPAT_LAYER=RunAsInvoker` to launch it without elevation (uninstalling
won't work then, but browsing and scanning will).

Use `deepuninstall scan "<program>"` to try out matcher changes on real installs without changing anything.

## Ground rules for the engine

DeepUninstall deletes things, so safety beats thoroughness:

- **Every delete goes through `SafetyGuard`.** Don't bypass it. If something legitimate is blocked, extend the guard deliberately and add a test.
- **High confidence needs hard evidence**: a path into the confirmed install folder, or a distinctive exact name match. Anything weaker is Medium or Low and is not pre-selected.
- **No fuzzy matching.** Prefix, substring or similarity matching on names produces false positives.
- **Back up before you delete.** Registry changes go through `BackupStore`.
- Add unit tests for matcher and guard changes (`tests/DeepUninstall.Core.Tests`).

## Pull requests

- Keep changes focused. Separate refactors from behaviour changes.
- Make sure `dotnet build` has no warnings and `dotnet test` passes.
- Describe how you tested, ideally with a real program.
