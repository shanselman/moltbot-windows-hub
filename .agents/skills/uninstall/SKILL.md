---
name: uninstall
description: Run the headless CLI uninstall path for a local OpenClaw Companion dev build (--uninstall --dry-run, then --confirm-destructive) to remove the WSL gateway distro, gateway CLI/service, gateway identity/tokens, exec approvals, autostart entries, node-context state, Local AI, Tailscale state, and other AppData artifacts. Use when the user asks to uninstall, clean up, or reset a local dev install/gateway, or to test the uninstall engine.
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

1. **Build with the dev identity, for the matching architecture.** `.\build.ps1`
   alone defaults to release identity even in a Debug configuration, and a
   release-identity exe would unregister the real `OpenClawGateway` distro and mutate
   `%APPDATA%\OpenClawTray` instead of the `-Dev` copies below (the output path is
   identical either way, so nothing later in this procedure would catch that
   mistake). Always build with `-DevBuild`, and verify the marker before proceeding:

   ```powershell
   .\build.ps1 -DevBuild   # skip only if you already built with -DevBuild

   $arm64 = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq
       [System.Runtime.InteropServices.Architecture]::Arm64
   $arch = if ($arm64) { 'win-arm64' } else { 'win-x64' }
   $outDir = "src\OpenClaw.Tray.WinUI\bin\Debug\net10.0-windows10.0.22621.0\$arch"
   $exe = "$outDir\OpenClaw.Tray.WinUI.exe"

   $identity = (Get-Content "$outDir\app-identity.txt" -Raw).Trim()
   if ($identity -ne 'dev') { throw "Build output identity is '$identity', not 'dev' - rebuild with -DevBuild." }
   ```

2. **Stop only the dev instance, by PID.** A background `OpenClaw.Tray.WinUI.exe`
   (e.g. the tray icon) holds its own log/journal files open, which makes later steps
   fail to delete the AppData Logs directory. A release-identity tray can share the
   same process name, so match on the resolved `$exe` path and confirm before
   stopping; this repo kills processes by PID only, never by name
   (see `scripts/dev-reset-rebuild-launch.ps1`):

   ```powershell
   $exePath = (Resolve-Path $exe).Path
   $devProcs = Get-Process OpenClaw.Tray.WinUI -ErrorAction SilentlyContinue |
       Where-Object { $_.Path -eq $exePath }
   $devProcs | Format-Table Id, Path -AutoSize   # confirm these are the dev instance before stopping
   $devProcs | ForEach-Object { Stop-Process -Id $_.Id -Force }
   ```

3. **Dry-run first**, capturing output and exit code (a bare shell invocation can swallow
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

4. **Check for real state the dry-run doesn't surface**, e.g. a live WSL distro:

   ```powershell
   wsl -l -v
   ```

   If the distro named in step 3's output is present, flag to the user that it will be
   unregistered (`wsl --unregister`), permanently deleting its filesystem.

   The dry-run only logs rollback step IDs, not the concrete files each step would
   touch. If it matters to the user's situation, inspect `%APPDATA%\OpenClawTray-Dev\`
   directly before consenting (gateway registry, `windows-node-context.json`, Local AI
   config, Tailscale state).

5. **Confirm with the user**, then run the real uninstall the same way:

   ```powershell
   $p = Start-Process -FilePath $exe -ArgumentList '--uninstall','--confirm-destructive' -PassThru -Wait `
       -RedirectStandardOutput out2.log -RedirectStandardError err2.log
   $p.ExitCode
   Get-Content out2.log
   ```

6. **Verify** the specific artifacts each rollback step is supposed to clean up: not the
   AppData roots themselves. `TrayArtifactCleanup` (registry/files/settings only, no WSL
   calls) and the WSL/gateway rollback steps between them reset or remove selected files
   and records in place; neither deletes `%APPDATA%\OpenClawTray[-Dev]` or
   `%LOCALAPPDATA%\OpenClawTray[-Dev]`, so asserting those roots are gone will report a
   successful run as failed:

   ```powershell
   wsl -l -v   # target distro should no longer be listed
   Test-Path "$env:APPDATA\OpenClawTray-Dev\Logs"        # should be False
   Test-Path "$env:LOCALAPPDATA\OpenClawTray-Dev\Logs"   # should be False
   Test-Path "$env:LOCALAPPDATA\OpenClawTray-Dev\run.marker"  # should be False
   Test-Path "$env:APPDATA\OpenClawTray-Dev\exec-approvals.json"  # should be False
   Test-Path "$env:LOCALAPPDATA\OpenClawTray-Dev\windows-node-context.json"  # should be False, if it existed
   # settings.json: GatewayUrl removed; EnableNodeMode/AutoStart reset to false unless
   # other gateway records remain
   # Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name OpenClawTray-Dev -ErrorAction SilentlyContinue
   # should error/be absent (autostart registry key removed)
   ```

   With step 2's kill done, `%APPDATA%\OpenClawTray-Dev\Logs` should also be gone; if a
   `Failed to delete AppData Logs directory` warning still shows up, it's the current
   run's own still-open log/journal file and is harmless.

## Notes

- Log and journal for each run are printed at the end of stdout, under
  `%APPDATA%\OpenClawTray[-Dev]\Logs\Setup\uninstall-engine-<timestamp>.jsonl`.
- For a real installed app (not a dev build), the user-facing path is **Settings → Apps
  → Installed apps → OpenClaw Companion → Uninstall**; see `docs/SETUP.md#uninstalling`
  and the README's Uninstall section. This skill is for exercising the underlying CLI
  path directly, e.g. for dev cleanup or testing the uninstall engine itself.
