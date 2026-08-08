# claude-unstick-windows

A 25 KB single-file tool that gets Claude Desktop out of the post-update
**"another program is currently using this file" / "already running"** state on
Windows — **without rebooting and without administrator rights**.

Every existing answer to this problem says *reboot your machine*. You don't have to.

## The problem

Claude Desktop installs as an **MSIX package** under
`C:\Program Files\WindowsApps\Claude_<version>_x64__<publisher>`. To update, Windows
has to replace those files. Two things hold file locks on them, and **neither is
visible on the Processes tab of Task Manager**:

1. **`CoworkVMService`** — a Windows **service** running as `LocalSystem`, binary
   `<package>\app\resources\cowork-svc.exe`. Its display name is just **"Claude"**,
   it is `AUTO_START`, and it is completely independent of the app window. Killing
   the process alone is useless: the Service Control Manager respawns it immediately.
2. **Orphaned `Claude.exe` helper processes** from the package that fail to exit when
   the window closes. A dozen of them is normal.

While either survives, the MSIX update cannot apply and the app refuses to relaunch.

Related reports: [#76357](https://github.com/anthropics/claude-code/issues/76357),
[#42776](https://github.com/anthropics/claude-code/issues/42776),
[#42897](https://github.com/anthropics/claude-code/issues/42897),
[#41743](https://github.com/anthropics/claude-code/issues/41743),
[#40645](https://github.com/anthropics/claude-code/issues/40645),
[#51954](https://github.com/anthropics/claude-code/issues/51954),
[#63397](https://github.com/anthropics/claude-code/issues/63397).

## Two findings that aren't in any of those threads

### 1. You do not need administrator rights

The service's security descriptor is:

```
D:(A;;CCLCSWRPWPDTLOCRRC;;;AU)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;S-1-5-80-...)
```

The first ACE grants **`AU` — Authenticated Users** — both `RP` (`SERVICE_START`) and
`WP` (`SERVICE_STOP`), and there is no deny ACE. **Any logged-in user can cycle the
service without UAC.** That is what makes this workable on a locked-down corporate
machine, and it is why a reboot was never actually necessary.

Verify it yourself:

```powershell
sc.exe sdshow CoworkVMService
```

### 2. Task Manager and `Get-Process` report the wrong path

MSIX redirects every per-user write the packaged app makes. Tools report the CLI as
living here:

```
C:\Users\<you>\AppData\Roaming\Claude\claude-code\<version>\claude.exe
```

The real on-disk location is:

```
C:\Users\<you>\AppData\Local\Packages\Claude_<publisher>\LocalCache\Roaming\Claude\claude-code\<version>\claude.exe
```

This is why people hunting for stale lock files never find them in the obvious place,
and why processes have to be matched on their true image path. The bundled Claude Code
CLI lives inside the package container too.

## What the tool does

1. Discovers the package, its AUMID and the service state **at runtime** — nothing is
   hardcoded, so it keeps working across version updates and on other machines.
2. Stops `CoworkVMService` and waits for it to actually reach `STOPPED`.
3. Kills everything Claude owns:
   - processes running from the WindowsApps package directory,
   - the Claude Code CLI inside the redirected MSIX data directory,
   - `cowork-svc.exe`,
   - **child processes** — MCP servers, hook scripts, spawned shells. These are found
     by walking the **parent chain**, because their own image paths reveal nothing
     about Claude. The walk is guarded against PID reuse by comparing creation times.
4. Deletes stale single-instance and update locks (`SingletonLock`, `SingletonCookie`,
   `SingletonSocket`, `lockfile`, `*.lock`, `locks\*`) in every relevant location,
   including the redirected MSIX directories.
5. Relaunches via **AUMID** using `IApplicationActivationManager::ActivateApplication`.
   A packaged app cannot be started from its WindowsApps path. Falls back to
   `shell:AppsFolder\<AUMID>`, then to a legacy non-packaged install
   (`%LOCALAPPDATA%\AnthropicClaude\app-*\claude.exe`).
6. Restarts the service **only after** the app is back up, so it cannot re-lock the
   package while a pending update is applying.
7. Verifies a new instance actually started before reporting success.

If stopping the service is ever denied on a differently-hardened machine, the tool
relaunches itself elevated. On a normal install that never happens.

## Usage

Download `claude-killer.exe` from [Releases](../../releases) and double-click it.
It runs **silently** and only shows a window if something went wrong.

| Flag | Behaviour |
|---|---|
| *(none)* | fix it, silent; dialog on failure only |
| `--diagnose` | report what was found, **change nothing** |
| `--verbose` | fix it, then show a summary |
| `--launch` | just start Claude Desktop |
| `--log <file>` | write the log to a file instead of a dialog |
| `--help` | usage |

Run from a terminal and the output goes to the console instead of a dialog.

Exit codes: `0` success, `1` the fix did not work, `2` unexpected error.

**Start with `--diagnose` on a machine you care about.** It touches nothing.

## Building

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Shortcut
```

Compiles with the `csc.exe` that ships inside Windows (.NET Framework 4.x), so
**nothing has to be installed** — no SDK, no NuGet, no runtime. Output is a ~25 KB
dependency-free executable at `bin\claude-killer.exe`. `-Shortcut` drops a shortcut
on the Desktop.

To deploy elsewhere, copy the single `.exe`.

The source deliberately stays C# 5 compatible (no string interpolation, no `?.`,
no `nameof`) because the in-box compiler is the old Roslyn build. Keep it that way
or the zero-install property is lost.

## Warning

The tool kills **all** Claude processes, including running Claude Code CLI sessions.
If you are working in a terminal with Claude Code, that session dies. This is
intentional — a stuck update means everything has to let go of the package.

## Verification status

| Verified | How |
|---|---|
| Package + AUMID discovery | matches what `Get-StartApps` reports, character for character |
| Process detection | `--diagnose` on a live stuck machine: found all package processes, the CLI in the redirected path, MCP servers, and spawned shells |
| AUMID activation | `ActivateApplication` returned `hr=0` and a live PID |
| Service stop right | from the SDDL; no deny ACE present |

The destructive path (stop → kill → clean → relaunch) was developed against a live
stuck machine but not executed end-to-end from inside the session that wrote it,
because doing so terminates that session. `--diagnose` exists so you can confirm
detection on your own machine before letting it act.

## License

MIT — see [LICENSE](LICENSE).
