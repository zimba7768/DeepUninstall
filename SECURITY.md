# Security policy

DeepUninstall runs with administrator rights and deletes files and registry entries, so security and
safety bugs matter.

## Reporting a vulnerability

Please **do not** open a public issue for security problems. Instead, use GitHub's
[private vulnerability reporting](../../security/advisories/new) for this repository.

Examples of what counts:

- A way to make DeepUninstall delete files or registry keys outside what it reports (path traversal, junction tricks, crafted registry values).
- A way for a malicious program's registry entries to run arbitrary commands through DeepUninstall beyond its own uninstaller.
- Bypasses of the safety guard.

You'll get an acknowledgement as soon as possible, and credit in the release notes if you'd like it.
