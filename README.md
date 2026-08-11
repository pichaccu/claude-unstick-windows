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

## Identity comes from what is registered, not from what the service claims

This one is worth calling out, because getting it wrong is what made the first
version fail on a machine mid-update.

The obvious place to learn which package you are dealing with is the service's
registered `ImagePath`. During an update that value **already points at the new
version**, while only the old one is still registered and activatable. Derive the
AppUserModelId from it and `ActivateApplication` returns `0x800704C7`
(`ERROR_CANCELLED`) — Windows is being asked to start something that does not exist
yet.

So the package identity is resolved with `FindPackagesByPackageFamily`, which only
ever reports packages actually **registered** for the current user, and the AUMID is
read out of the package with `GetPackageApplicationIds` rather than assembled from a
naming convention. If that read ever fails, the report says so in as many words
instead of quietly guessing.

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

### The repair ladder

Freeing the locks is not always enough, so the repair escalates. **Every rung is
verified by watching for a real process** — nothing is assumed to have worked, and
the first rung that succeeds ends the run:

| Rung | Action | Fixes |
|---|---|---|
| 1 | stop service, kill processes, clear locks, activate | orphaned processes and stale locks |
| 2 | re-register the package for the current user (`Add-AppxPackage -Register … -ForceApplicationShutdown`) | a half-applied update whose registration is broken |
| 3 | Restart Manager → kill the remaining lock holders that are safe to kill | a non-Claude holder that is plainly yours |

Rung 2 needs no admin: a user may re-register a package already installed for them,
and the manifest under `WindowsApps` is readable without elevation.

Rung 3 is deliberately conservative. It **never** kills security software, Windows
services, processes under `%WINDIR%`, or anything in another session — those get
named in the report instead, because terminating them would be both wrong and
useless. If one of those is the blocker, the tool tells you which, so an
administrator can add the right exclusion rather than guess.

When every rung fails, the report states what specifically is in the way instead of
suggesting a reboot as a reflex.

## Usage

### The script — recommended, and the only option on a locked-down machine

[`claude-repair.ps1`](claude-repair.ps1) does the same job using **only built-in
PowerShell cmdlets**. No compiled code, no `Add-Type`, nothing to install.

```powershell
powershell -ExecutionPolicy Bypass -File claude-repair.ps1
```

Use this one if you are on a work machine. There is no binary for antivirus to
flag, and an administrator can read the whole thing before allowing it — which is a
realistic ask, unlike "please add an exception for this unsigned .exe from GitHub".

`-Diagnose` reports and changes nothing. `-KeepCli` leaves running Claude Code CLI
sessions alone. The shell running the script, and everything it descends from, is
never terminated — so it always survives to finish the repair.

If Windows refuses to run the downloaded file, clear the Mark of the Web:

```powershell
Unblock-File .\claude-repair.ps1
```

### The executable — for convenience

Download `claude-repair.exe` from [Releases](../../releases) and double-click it.
It runs **silently** and only shows a window if something went wrong.

It does slightly more than the script — Restart Manager lock-holder detection and
Win32 package APIs — but it is a signed-by-nobody binary, so see
[Signing and antivirus false positives](#signing-and-antivirus-false-positives)
before deploying it anywhere managed.

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

**Start with `--diagnose` on a machine you care about.** It touches nothing, writes a
full report to `%TEMP%\claude-killer-diagnose.txt` and opens it.

The report answers the questions that actually matter when the simple case does not
apply:

- **Who really holds the package files** — via the Restart Manager API
  (`RmStartSession`/`RmGetList`), the same mechanism installers use for
  *"the following applications are using files that need to be updated"*. It names
  processes **and services**, across sessions, without admin. If the lock holder is
  antivirus, an indexer, or another user's session, this is what says so.
- **Whether the package identity is real or guessed.** The tool derives the package
  from the service's registered `ImagePath`. If `OpenPackageInfoByFullName` then
  fails, the AUMID is *constructed* rather than read, and activating it returns
  `0x800704C7` (`ERROR_CANCELLED`). The report labels this explicitly instead of
  hiding it.
- **What is actually registered** — `Get-AppxPackage` version and `Status`. A
  mismatch against the service's `ImagePath` means a half-applied update, which is a
  different failure from a mere file lock.

The same evidence block is printed automatically whenever a repair attempt fails.

## Signing and antivirus false positives

A small, unsigned .NET binary that terminates processes and stops a service is close
to a textbook machine-learning false positive. Windows Defender has flagged this tool
on at least one machine. It is a false positive — the whole source is in this repo,
it is ~1000 lines, and you can build it yourself in one command.

**The honest answer to this problem is not to ship a binary at all.** Asking people
to whitelist an unsigned executable does not work: nobody does it for a random tool,
and on a corporate machine nobody *can*. That is why
[`claude-repair.ps1`](claude-repair.ps1) exists and is the recommended path — plain
text, readable before it runs, nothing to flag.

If you do want the executable, here is what actually helps, in order of how much
good it does:

**1. Real file metadata — done, in the binary.** Company, product, description and
version are populated from `src/AssemblyInfo.cs`, and the build fails if they are
missing. An unsigned binary with `FileVersion 0.0.0.0` and no company name is far
more likely to be flagged than an identical one carrying proper metadata.

**2. Report it to Microsoft — free, and the only thing that fixes it everywhere.**
Submit the binary at
[microsoft.com/wdsi/filesubmission](https://www.microsoft.com/en-us/wdsi/filesubmission)
as a **software developer**, category *"Incorrectly detected as malware"*. Turnaround
is usually 24–72 hours, after which the verdict is corrected in the cloud for every
Defender installation — including managed corporate ones you cannot touch yourself.
**This is the fix for a corporate machine.**

**3. Authenticode signature.** `build.ps1 -Sign` signs the binary with a self-signed
certificate created in your own user store — no admin needed — and timestamps it via
DigiCert so the signature outlives the certificate.

Be clear about what a self-signed signature buys you: **nothing, on a machine that
does not trust the certificate.** It does not make Defender's heuristics stand down,
and it does not silence SmartScreen for other people. It only helps on machines where
you deliberately install the certificate, via `trust-cert.ps1` — read the warning at
the top of that script first, because it makes the machine trust everything signed
with that key.

A signature that helps *everyone* needs a real certificate from a public CA. For a
public MIT-licensed project like this one, Certum's open-source code signing
certificate is the cheap route (roughly €30/year); an OV certificate from Sectigo or
DigiCert runs into the hundreds. Sign with the same `-Sign` flow, pointing
`-CertSubject` at the purchased certificate.

**4. Expect SmartScreen on first download regardless.** A binary downloaded from
GitHub carries the Mark of the Web, so Windows shows *"Windows protected your PC"*
until the file builds reputation. **More info → Run anyway**, or clear the mark with:

```powershell
Unblock-File .\claude-repair.exe
```

What this project will not do is obfuscate, pack, or otherwise dress the binary up to
slip past a scanner. Those techniques are themselves what scanners look for, and
hiding from security software is not a thing a repair tool should do.

## Building

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Sign -Shortcut
```

Compiles with the `csc.exe` that ships inside Windows (.NET Framework 4.x), so
**nothing has to be installed** — no SDK, no NuGet, no runtime. Output is a ~39 KB
dependency-free executable at `bin\claude-repair.exe`. `-Sign` adds a timestamped
Authenticode signature, `-Shortcut` drops a shortcut on the Desktop. Both are
optional; a bare `build.ps1` just compiles.

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

Version 2.0.0 additionally verified: registered-package identity resolution
(`FindPackagesByPackageFamily` agrees with `Get-AppxPackage`), activation through
the resolved AUMID, non-admin readability of the `WindowsApps` manifest that rung 2
depends on, and the shape of the re-registration command.

### Honest limits

The mid-update failure that motivated version 2.0.0 was reproduced on a managed
corporate machine but could not be re-tested there afterwards. Rung 2 targets the
root cause identified from that machine's output — an identity taken from the
service's `ImagePath` while a different package was the registered one — and rungs 1
and 3 cover the alternatives. That reasoning is sound and each piece is individually
verified, but the full ladder has not been observed rescuing that specific machine.

Rung 2 has not been executed end-to-end either: running it re-registers the package
and force-closes Claude, which on the development machine would terminate the
session doing the testing. Its precondition and command shape were verified instead.

If it fails for you, `--diagnose` names the blocker — please open an issue with it.

## License

MIT — see [LICENSE](LICENSE).
