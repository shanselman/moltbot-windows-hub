---
name: uninstall
description: Run the headless CLI uninstall path for a local OpenClaw Companion dev build (--uninstall --dry-run, then --confirm-destructive) to remove the WSL gateway distro, CLI, and AppData state. Use when the user asks to uninstall, clean up, or reset a local dev install/gateway, or to test the uninstall engine.
---

# Uninstall (dev build)

`OpenClaw.Tray.WinUI.exe` embeds the SetupEngine and accepts `--uninstall` directly, so
no installer is required to exercise this path; a local `dotnet build` output works.
This walks a real (not just installed-via-Inno-Setup) uninstall of dev/test state:
WSL gateway distro, `openclaw` CLI inside WSL, gateway systemd service, Tailscale
state, and the tray's AppData files.

This is destructive. Confirm with the user before the final `--confirm-destructive`
run, and call out anything it will touch that isn't disposable dev state (e.g. a WSL
distro or Tailscale session actually in use).

## Procedure

1. **Find the matching-architecture build.** The exe is a framework-dependent apphost;
   it must match the machine's CPU architecture (check with `systeminfo | findstr
   "System Type"`; ARM64 machines need the `win-arm64` output, not `win-x64`, even if
   `dotnet --list-runtimes` shows a runtime installed):

   ```powershell
   $arch = if ((Get-CimInstance Win32_OperatingSystem).OSArchitecture -match 'ARM') { 'win-arm64' } else { 'win-x64' }
   $exe = "src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.22621.0\$arch\OpenClaw.Tray.WinUI.exe"
   # (build first if it doesn't exist yet: .\build.ps1)
   ```

2. **Dry-run first**, capturing output and exit code (a bare shell invocation can swallow
   the exit code for a WinUI apphost; use `Start-Process -Wait -PassThru`):

   ```powershell
   $p = Start-Process -FilePath $exe -ArgumentList '--uninstall','--dry-run' -PassThru -Wait `
       -RedirectStandardOutput out.log -RedirectStandardError err.log
   $p.ExitCode
   Get-Content out.log
   ```

   Review the "Would rollback: ..." lines for each of the ~37 steps. Note the
   `--data-dir` / `--distro-name` printed at the top; dev-branch builds use
   `OpenClawTray-Dev` / `OpenClawGateway-Dev`, separate from a real install's
   `OpenClawTray` / `OpenClawGateway`.

3. **Check for real state the dry-run doesn't surface**, e.g. a live WSL distro:

   ```powershell
   wsl -l -v
   ```

   If the distro named in step 2's output is present, flag to the user that it will be
   unregistered (`wsl --unregister`), permanently deleting its filesystem.

4. **Confirm with the user**, then run the real uninstall the same way:

   ```powershell
   $p = Start-Process -FilePath $exe -ArgumentList '--uninstall','--confirm-destructive' -PassThru -Wait `
       -RedirectStandardOutput out2.log -RedirectStandardError err2.log
   $p.ExitCode
   Get-Content out2.log
   ```

5. **Verify** nothing expected survived:

   ```powershell
   wsl -l -v
   Test-Path "$env:APPDATA\OpenClawTray-Dev"
   Test-Path "$env:LOCALAPPDATA\OpenClawTray-Dev"
   ```

   A `Failed to delete AppData Logs directory` warning because the run's own log/journal
   file is still open is expected and harmless.

## Notes

- Log and journal for each run are printed at the end of stdout, under
  `%APPDATA%\OpenClawTray[-Dev]\Logs\Setup\uninstall-engine-<timestamp>.jsonl`.
- For a real installed app (not a dev build), the user-facing path is **Settings → Apps
  → Installed apps → OpenClaw Companion → Uninstall**; see `docs/SETUP.md#uninstalling`
  and the README's Uninstall section. This skill is for exercising the underlying CLI
  path directly, e.g. for dev cleanup or testing the uninstall engine itself.
