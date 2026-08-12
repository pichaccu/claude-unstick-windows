// Recovers Claude Desktop on Windows after a failed MSIX update, without a reboot
// and without administrator rights.
//
// Root cause (anthropics/claude-code issues #76357, #42776, #42897, #41743):
// Claude Desktop ships as an MSIX package. Two things keep file locks on it after
// an update, and neither is visible on the Processes tab of Task Manager:
//   1. CoworkVMService - a LocalSystem service displayed simply as "Claude",
//      running <package>\app\resources\cowork-svc.exe. AUTO_START, independent of
//      the app window, so killing the process alone is useless: the SCM respawns it.
//   2. Orphaned Claude.exe helper processes from the package.
//
// No admin needed: the service SDDL grants Authenticated Users
// SERVICE_START|SERVICE_STOP (RP/WP in the "…;;;AU" ACE).
//
// Targets .NET Framework 4.8 and builds with the in-box csc.exe, so the output is a
// small exe with no install footprint. Keep the syntax C# 5 compatible: no string
// interpolation, no ?., no nameof, no out-var. All user-facing text lives in
// Messages.cs; code and comments stay English-only.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeKiller
{
    internal static class Program
    {
        private const string ServiceName = "CoworkVMService";
        private const int ServiceWaitSeconds = 25;
        private const int ProcessExitWaitMs = 8000;
        private const int VerifyTimeoutMs = 25000;

        private static readonly List<string> Log = new List<string>();
        private static bool Verbose;
        private static bool DryRun;
        private static bool AlreadyElevated;
        private static bool LaunchOnly;
        private static string LogFile = "";
        private static int OwnPid;
        private static string OwnPath = "";

        [STAThread]
        private static int Main(string[] argv)
        {
            OwnPid = Process.GetCurrentProcess().Id;
            try { OwnPath = Assembly.GetExecutingAssembly().Location; }
            catch (Exception) { OwnPath = ""; }

            for (int i = 0; i < argv.Length; i++)
            {
                string a = argv[i].TrimStart('-', '/').ToLowerInvariant();
                if (a == "d" || a == "diagnose" || a == "dry-run" || a == "dryrun") DryRun = true;
                else if (a == "v" || a == "verbose") Verbose = true;
                else if (a == "elevated") AlreadyElevated = true;
                else if (a == "launch" || a == "start") LaunchOnly = true;
                else if (a == "log" && i + 1 < argv.Length) LogFile = argv[++i];
                else if (a == "lang" && i + 1 < argv.Length) Msg.Override(argv[++i]);
                else if (a == "h" || a == "help" || a == "?") { ShowHelp(); return 0; }
            }

            try
            {
                return Run();
            }
            catch (Exception ex)
            {
                Say("FATAL: " + ex.GetType().Name + ": " + ex.Message);
                Report(Msg.T("title.error"), MessageBoxIcon.Error);
                return 2;
            }
        }

        private static int Run()
        {
            Install info = Discover();

            Say(Msg.T("hdr.package", info.PackageFullName.Length > 0 ? info.PackageFullName : Msg.T("val.notfound")));
            Say(Msg.T("hdr.aumid", info.Aumid.Length > 0 ? info.Aumid : Msg.T("val.notfound")));
            Say(Msg.T("hdr.service", info.ServiceState));

            if (LaunchOnly)
            {
                bool started = Activate(info);
                Say(started ? Msg.T("act.launchok") : Msg.T("act.launchfail"));
                Report(Msg.T("title.launch"), started ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                return started ? 0 : 1;
            }

            List<Target> targets = FindTargets(info);
            Say(Msg.T("proc.targets", targets.Count));
            foreach (Target t in targets) Say("  [" + t.Pid + "] " + t.Name + " - " + t.Reason);

            if (DryRun) return RunDiagnostics(info, targets);

            // 1. Drop the service first. It is the lock nobody can see, and it
            //    respawns cowork-svc.exe if only the process is killed.
            bool serviceWasPresent = info.ServiceState != Msg.T("val.absent");
            if (serviceWasPresent && !StopService())
            {
                if (!AlreadyElevated && TryRelaunchElevated()) return 0;
                Say(Msg.T("svc.continue"));
            }

            Say(Msg.T("proc.killed", KillAll(info)));
            Say(Msg.T("locks.removed", CleanLocks(info)));

            // 2. Escalating repair. Every rung is verified by watching for a real
            //    process; the first one that works ends the run.
            if (TryActivateAndVerify(info, Msg.T("try.baseline")))
                return Finish(serviceWasPresent, true);

            // Activation can fail purely because a framework dependency is not
            // healthy. The main package still reports Status=Ok in that case, which
            // is why this is checked before blaming the registration.
            if (RepairDependencies(info) && TryActivateAndVerify(info, Msg.T("try.deps")))
                return Finish(serviceWasPresent, true);

            if (RepairRegistration(ref info) && TryActivateAndVerify(info, Msg.T("try.reregister")))
                return Finish(serviceWasPresent, true);

            if (ReleaseHolders(info) > 0 && TryActivateAndVerify(info, Msg.T("try.holders")))
                return Finish(serviceWasPresent, true);

            Say("");
            Say(Msg.T("res.failed"));
            ReportEvidence(info, FindTargets(info));
            AdviseFinalSteps(info);
            return Finish(serviceWasPresent, false);
        }

        private static int RunDiagnostics(Install info, List<Target> targets)
        {
            Say("");
            Say(Msg.T("ev.allprocs"));
            foreach (Proc p in EnumerateProcesses())
            {
                if (!Regex.IsMatch(p.Name, @"^(claude|node|cowork-svc)$", RegexOptions.IgnoreCase)) continue;
                Say("  [" + p.Pid + "] " + p.Name + " path='" + p.Path + "' -> " +
                    (targets.Any(t => t.Pid == p.Pid) ? Msg.T("ev.target") : Msg.T("ev.skipped")));
            }

            ReportEvidence(info, targets);
            Say("");
            Say(Msg.T("ev.dryrun"));

            // Far too long for a dialog: write it to a file and open it so it can be
            // read, kept and pasted somewhere.
            bool opened = false;
            if (LogFile.Length == 0)
            {
                LogFile = Path.Combine(Path.GetTempPath(), "claude-repair-diagnose.txt");
                opened = true;
            }
            Report(Msg.T("title.diagnose"), MessageBoxIcon.Information);
            if (opened)
            {
                try { Process.Start("notepad.exe", "\"" + LogFile + "\""); }
                catch (Exception) { }
            }
            return 0;
        }

        // ---------------------------------------------------------------- repair ladder

        private static int Finish(bool serviceWasPresent, bool ok)
        {
            if (serviceWasPresent) StartService();

            if (ok)
            {
                Say(Msg.T("res.done"));
                if (Verbose) Report(Msg.T("title.done"), MessageBoxIcon.Information);
                return 0;
            }

            Report(Msg.T("title.failed"), MessageBoxIcon.Error);
            return 1;
        }

        private static bool TryActivateAndVerify(Install info, string label)
        {
            Say("");
            Say(Msg.T("try.attempt", label));
            if (!Activate(info)) return false;
            bool up = WaitForApp(info);
            Say(up ? Msg.T("try.ok") : Msg.T("try.timeout", VerifyTimeoutMs / 1000));
            return up;
        }

        private sealed class Dependency
        {
            public string FullName = "";
            public string Status = "";
            public string InstallLocation = "";
            public bool Healthy
            {
                get { return string.Equals(Status, "Ok", StringComparison.OrdinalIgnoreCase); }
            }
        }

        private static List<Dependency> GetDependencies()
        {
            List<Dependency> deps = new List<Dependency>();
            string text = RunPowerShell(
                "$p = Get-AppxPackage -Name Claude* | Sort-Object Version -Descending | Select-Object -First 1; " +
                "if ($p) { foreach ($d in $p.Dependencies) { " +
                "$d.PackageFullName + '|' + $d.Status + '|' + $d.InstallLocation } }");

            foreach (string line in text.Split('\n'))
            {
                string[] parts = line.Trim().Split('|');
                if (parts.Length < 2 || parts[0].Length == 0) continue;
                Dependency d = new Dependency();
                d.FullName = parts[0];
                d.Status = parts[1];
                if (parts.Length > 2) d.InstallLocation = parts[2];
                deps.Add(d);
            }
            return deps;
        }

        // A packaged app will not activate if one of its framework dependencies is
        // missing or broken, and the main package's own Status says nothing about it.
        private static bool RepairDependencies(Install info)
        {
            Say("");
            Say(Msg.T("dep.header"));

            List<Dependency> deps = GetDependencies();
            if (deps.Count == 0) { Say(Msg.T("dep.nodeps")); return false; }

            foreach (Dependency d in deps) Say(Msg.T("dep.item", d.FullName + " | " + d.Status));

            List<Dependency> broken = deps.Where(d => !d.Healthy && d.InstallLocation.Length > 0).ToList();
            if (broken.Count == 0) { Say(Msg.T("dep.allok")); return false; }

            Say(Msg.T("dep.repairing", broken.Count));
            foreach (Dependency d in broken)
            {
                string manifest = Path.Combine(d.InstallLocation, "AppxManifest.xml");
                string result = RunPowerShell(
                    "try { Add-AppxPackage -Register '" + manifest.Replace("'", "''") +
                    "' -DisableDevelopmentMode -ErrorAction Stop; 'OK' } catch { 'ERR: ' + $_.Exception.Message }");
                Say("  " + d.FullName + " -> " + result.Trim());
            }
            return true;
        }

        // Repair for a half-applied update: the files are on disk but the
        // registration is broken or points at a version that is not really there.
        // No admin needed - a user may re-register a package installed for them.
        private static bool RepairRegistration(ref Install info)
        {
            Say("");
            Say(Msg.T("reg.header"));

            string result = RunPowerShell(
                "$p = Get-AppxPackage -Name Claude*; " +
                "if (-not $p) { 'ERR: no registered Claude package' } else { " +
                "foreach ($x in $p) { try { " +
                "Add-AppxPackage -DisableDevelopmentMode -Register ($x.InstallLocation + '\\AppxManifest.xml') -ForceApplicationShutdown -ErrorAction Stop; " +
                "'OK ' + $x.PackageFullName } catch { 'ERR: ' + $_.Exception.Message } } }");

            foreach (string line in result.Split('\n'))
            {
                string s = line.Trim();
                if (s.Length > 0) Say("  " + s);
            }

            // Re-registering the package starts its service again, and that service
            // is exactly what holds the package files. Without stopping it here the
            // next activation attempt is blocked by the repair we just performed.
            ServiceController probe = null;
            try { probe = new ServiceController(ServiceName); } catch (Exception) { }
            if (probe != null)
            {
                try
                {
                    probe.Refresh();
                    if (probe.Status != ServiceControllerStatus.Stopped)
                    {
                        Say(Msg.T("svc.retry"));
                        StopService();
                    }
                }
                catch (Exception) { }
                finally { probe.Close(); }
            }

            // Registration may now resolve to a different version.
            info = Discover();
            Say(Msg.T("reg.identity",
                info.PackageFullName.Length > 0 ? info.PackageFullName : Msg.T("val.notfound"),
                info.AumidSource));

            return info.Aumid.Length > 0;
        }

        private static readonly Regex SecuritySoftware = new Regex(
            @"defender|msmpeng|mssense|sense(ir|ndr|cn)|falcon|crowdstrike|sentinel|carbonblack|cbdefense|" +
            @"cylance|sophos|symantec|mcafee|mfe|trendmicro|tmccsf|kaspersky|avp\b|eset|ekrn|bitdefender|" +
            @"norton|webroot|tanium|qualys|nessus|forcepoint|netskope|zscaler|xagt|fireeye",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static bool IsOurService(Holder h)
        {
            if (string.Equals(h.Service, ServiceName, StringComparison.OrdinalIgnoreCase)) return true;
            if (h.Path.Length > 0 &&
                h.Path.IndexOf("cowork-svc", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // Only touches what is plainly ours to touch. Security agents, system
        // services and other users' sessions are named, never killed - terminating
        // those would be both wrong and useless.
        private static string WhyNotSafeToKill(Holder h, int mySession)
        {
            // Checked first: our own service reports session 0 because it runs as
            // LocalSystem, and treating that as "another session" is what made an
            // earlier version refuse to deal with the one holder that mattered.
            if (IsOurService(h)) return "";

            if (h.Service.Length > 0) return Msg.T("safe.service");

            if (SecuritySoftware.IsMatch(h.App) || (h.Path.Length > 0 && SecuritySoftware.IsMatch(h.Path)))
                return Msg.T("safe.security");

            if (h.SessionId != (uint)mySession) return Msg.T("safe.session", h.SessionId);

            if (h.Path.Length > 0)
            {
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (h.Path.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase))
                    return Msg.T("safe.system");
            }
            else return Msg.T("safe.nopath");

            return "";
        }

        private static int ReleaseHolders(Install info)
        {
            Say("");
            Say(Msg.T("hold.header"));

            string err;
            List<Holder> holders = WhoHoldsFiles(PackageFilesToProbe(info), out err);
            if (err.Length > 0) Say("  " + err);
            if (holders.Count == 0) { Say(Msg.T("hold.none")); return 0; }

            int mySession = Process.GetCurrentProcess().SessionId;
            int released = 0;

            foreach (Holder h in holders)
            {
                if (h.Pid == OwnPid) continue;

                // A LocalSystem process cannot be killed by a normal user, but its
                // service can be stopped - and we hold exactly that right.
                if (IsOurService(h))
                {
                    Say(Msg.T("hold.skip", h.Pid, h.App, Msg.T("hold.ourservice")));
                    if (StopService()) released++;
                    continue;
                }

                string why = WhyNotSafeToKill(h, mySession);
                if (why.Length > 0) { Say(Msg.T("hold.skip", h.Pid, h.App, why)); continue; }

                try
                {
                    Process p = Process.GetProcessById(h.Pid);
                    p.Kill();
                    p.WaitForExit(ProcessExitWaitMs);
                    released++;
                    Say(Msg.T("hold.killed", h.Pid, h.App));
                }
                catch (ArgumentException) { }
                catch (Exception ex) { Say(Msg.T("proc.failed", h.Pid, h.App, ex.Message)); }
            }

            Say(Msg.T("hold.count", released));
            return released;
        }

        // When every rung failed, say what specifically is in the way rather than
        // offering a reflexive "try rebooting".
        private static void AdviseFinalSteps(Install info)
        {
            Say("");
            Say(Msg.T("adv.header"));

            if (!info.PackageRegistered || info.Aumid.Length == 0)
            {
                Say(Msg.T("adv.notreg"));
                Say(Msg.T("adv.reinstall"));
                Say(Msg.T("adv.reset"));
                return;
            }

            if (SideloadingBlocked())
            {
                Say(Msg.T("adv.sideload"));
                Say(Msg.T("adv.sideload2"));
                Say(Msg.T("adv.sideload3"));
                return;
            }

            List<Dependency> broken = GetDependencies().Where(d => !d.Healthy).ToList();
            if (broken.Count > 0)
            {
                Say(Msg.T("adv.deps"));
                foreach (Dependency d in broken) Say(Msg.T("adv.blockitem", d.FullName, d.Status));
                Say(Msg.T("adv.reinstall"));
                return;
            }

            string err;
            List<Holder> holders = WhoHoldsFiles(PackageFilesToProbe(info), out err);
            int mySession = Process.GetCurrentProcess().SessionId;
            List<Holder> blocked = holders.Where(h => WhyNotSafeToKill(h, mySession).Length > 0).ToList();

            if (blocked.Count > 0)
            {
                Say(Msg.T("adv.blocked"));
                foreach (Holder h in blocked)
                    Say(Msg.T("adv.blockitem", h.App, WhyNotSafeToKill(h, mySession)));
                Say(Msg.T("adv.exclusion"));
                Say(Msg.T("adv.exclusion2"));
                return;
            }

            Say(Msg.T("adv.nothing"));
            Say(Msg.T("adv.nothing2"));
            Say(Msg.T("adv.nothing3"));
            Say(Msg.T("adv.log"));
        }

        // ---------------------------------------------------------------- discovery

        private sealed class Install
        {
            public string PackageDir = "";
            public string PackageFullName = "";
            public string PackageFamilyName = "";
            public string Aumid = "";
            public string ServiceState = "";

            // Provenance, so a failure can be told apart from a lucky guess.
            public string IdentitySource = "unknown";
            public readonly List<string> AllAumids = new List<string>();
            public bool PackageRegistered;
            public bool AumidResolved;
            public string AumidSource = "none";
        }

        private static Install Discover()
        {
            Install info = new Install();
            info.ServiceState = QueryServiceState();

            // Step 1 - find the family name. Any source will do; the family part is
            // stable across versions, unlike the full name.
            string hintDir = "";
            string svcExe = ReadServiceImagePath();
            if (svcExe.Length > 0) hintDir = PackageRootOf(svcExe);

            if (hintDir.Length == 0)
            {
                foreach (Proc p in EnumerateProcesses())
                {
                    string root = PackageRootOf(p.Path);
                    if (root.Length > 0) { hintDir = root; break; }
                }
            }

            string family = hintDir.Length > 0 ? FamilyFromFullName(Path.GetFileName(hintDir)) : "";
            if (family.Length == 0) family = FamilyFromRegistry();

            // Step 2 - ask which package of that family is REGISTERED. During an
            // update the service ImagePath already points at the new version while
            // only the old one is registered; activating the new identity then fails
            // with ERROR_CANCELLED. Registration is the authority.
            if (family.Length > 0)
            {
                info.PackageFamilyName = family;
                foreach (string fullName in RegisteredPackages(family))
                {
                    info.PackageFullName = fullName;
                    info.Aumid = ResolveAumid(info);
                    if (info.AumidResolved)
                    {
                        info.IdentitySource = "registered package (FindPackagesByPackageFamily)";
                        info.PackageDir = InstallDirOf(fullName, hintDir);
                        break;
                    }
                }
            }

            // Step 3 - nothing registered resolved; fall back to the hint so the
            // process killing and lock clearing still run.
            if (!info.AumidResolved && hintDir.Length > 0)
            {
                info.PackageDir = hintDir;
                info.PackageFullName = Path.GetFileName(hintDir);
                info.PackageFamilyName = FamilyFromFullName(info.PackageFullName);
                info.IdentitySource = "service ImagePath (no registered package)";
                info.Aumid = ResolveAumid(info);
            }

            if (info.PackageDir.Length == 0 && hintDir.Length > 0) info.PackageDir = hintDir;
            return info;
        }

        // Registered package full names for a family, newest first. Pure Win32, no
        // admin, no PowerShell - and unlike the service registry entry it only ever
        // reports packages that can actually be activated.
        private static List<string> RegisteredPackages(string familyName)
        {
            List<string> names = new List<string>();
            try
            {
                uint count = 0, bufLen = 0;
                int rc = Native.FindPackagesByPackageFamily(familyName,
                    Native.PACKAGE_FILTER_HEAD | Native.PACKAGE_FILTER_DIRECT,
                    ref count, IntPtr.Zero, ref bufLen, IntPtr.Zero, IntPtr.Zero);

                if (rc == Native.ERROR_INSUFFICIENT_BUFFER && count > 0)
                {
                    IntPtr namePtrs = Marshal.AllocHGlobal((int)count * IntPtr.Size);
                    IntPtr buffer = Marshal.AllocHGlobal((int)bufLen * 2);
                    IntPtr props = Marshal.AllocHGlobal((int)count * sizeof(uint));
                    try
                    {
                        rc = Native.FindPackagesByPackageFamily(familyName,
                            Native.PACKAGE_FILTER_HEAD | Native.PACKAGE_FILTER_DIRECT,
                            ref count, namePtrs, ref bufLen, buffer, props);
                        if (rc == 0)
                        {
                            for (int i = 0; i < count; i++)
                            {
                                IntPtr p = Marshal.ReadIntPtr(namePtrs, i * IntPtr.Size);
                                string n = Marshal.PtrToStringUni(p);
                                if (!string.IsNullOrEmpty(n)) names.Add(n);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(namePtrs);
                        Marshal.FreeHGlobal(buffer);
                        Marshal.FreeHGlobal(props);
                    }
                }
            }
            catch (Exception) { }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            names.Reverse(); // highest version first
            return names;
        }

        private static string FamilyFromRegistry()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\ActivatableClasses\Package"))
                {
                    if (k != null)
                    {
                        foreach (string sub in k.GetSubKeyNames())
                        {
                            if (sub.StartsWith("Claude_", StringComparison.OrdinalIgnoreCase))
                            {
                                string fam = FamilyFromFullName(sub);
                                if (fam.Length > 0) return fam;
                            }
                        }
                    }
                }
            }
            catch (Exception) { }

            // Publisher hash is stable for a given signing identity.
            return "Claude_pzs8sxrjxfjjc";
        }

        private static string InstallDirOf(string fullName, string hintDir)
        {
            if (hintDir.Length > 0)
            {
                string parent = Path.GetDirectoryName(hintDir);
                if (!string.IsNullOrEmpty(parent))
                {
                    string candidate = Path.Combine(parent, fullName);
                    if (Directory.Exists(candidate)) return candidate;
                }
            }
            string guess = Path.Combine(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps"),
                fullName);
            return Directory.Exists(guess) ? guess : hintDir;
        }

        // C:\Program Files\WindowsApps\Claude_1.2_x64__abc\app\Claude.exe
        //   -> C:\Program Files\WindowsApps\Claude_1.2_x64__abc
        private static string PackageRootOf(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return "";
            Match m = Regex.Match(exePath, @"^(.*?\\WindowsApps\\Claude_[^\\]+)\\", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }

        // Claude_1.24012.11.0_x64__pzs8sxrjxfjjc -> Claude_pzs8sxrjxfjjc
        private static string FamilyFromFullName(string fullName)
        {
            int first = fullName.IndexOf('_');
            int last = fullName.LastIndexOf("__", StringComparison.Ordinal);
            if (first <= 0 || last <= first) return "";
            return fullName.Substring(0, first) + "_" + fullName.Substring(last + 2);
        }

        private static string ResolveAumid(Install info)
        {
            string fullName = info.PackageFullName;
            string familyName = info.PackageFamilyName;

            // Ask the package model itself. If this fails the identity is stale -
            // the registration points at a package that is not really there, which
            // is what a half-applied update looks like.
            try
            {
                IntPtr pir;
                if (Native.OpenPackageInfoByFullName(fullName, 0, out pir) == 0)
                {
                    info.PackageRegistered = true;
                    try
                    {
                        uint len = 0, count = 0;
                        Native.GetPackageApplicationIds(pir, ref len, IntPtr.Zero, out count);
                        if (len > 0)
                        {
                            IntPtr buf = Marshal.AllocHGlobal((int)len);
                            try
                            {
                                if (Native.GetPackageApplicationIds(pir, ref len, buf, out count) == 0 && count > 0)
                                {
                                    for (int i = 0; i < count; i++)
                                    {
                                        IntPtr sp = Marshal.ReadIntPtr(buf, i * IntPtr.Size);
                                        string extra = Marshal.PtrToStringUni(sp);
                                        if (!string.IsNullOrEmpty(extra)) info.AllAumids.Add(extra);
                                    }
                                    if (info.AllAumids.Count > 0)
                                    {
                                        info.AumidResolved = true;
                                        info.AumidSource = "read from the package";
                                        return info.AllAumids[0];
                                    }
                                }
                            }
                            finally { Marshal.FreeHGlobal(buf); }
                        }
                    }
                    finally { Native.ClosePackageInfo(pir); }
                }
            }
            catch (Exception) { }

            // Last resort: the conventional application id. This is a GUESS - if the
            // package is not properly registered, activating it yields ERROR_CANCELLED.
            info.AumidSource = "GUESS (the package could not be opened)";
            return familyName.Length > 0 ? familyName + "!Claude" : "";
        }

        // ---------------------------------------------------------------- service

        private static string ReadServiceImagePath()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + ServiceName))
                {
                    if (k == null) return "";
                    object v = k.GetValue("ImagePath");
                    if (v == null) return "";
                    string s = Environment.ExpandEnvironmentVariables(v.ToString()).Trim();
                    if (s.StartsWith("\""))
                    {
                        int end = s.IndexOf('"', 1);
                        if (end > 1) s = s.Substring(1, end - 1);
                    }
                    return s;
                }
            }
            catch (Exception) { return ""; }
        }

        private static string QueryServiceState()
        {
            try
            {
                using (ServiceController sc = new ServiceController(ServiceName))
                {
                    return sc.Status.ToString();
                }
            }
            catch (Exception) { return Msg.T("val.absent"); }
        }

        private static bool StopService()
        {
            try
            {
                using (ServiceController sc = new ServiceController(ServiceName))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        Say(Msg.T("svc.already"));
                        return true;
                    }
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(ServiceWaitSeconds));
                    Say(Msg.T("svc.stopped"));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Say(Msg.T("svc.stopfail", ex.Message));
                return false;
            }
        }

        private static void StartService()
        {
            try
            {
                using (ServiceController sc = new ServiceController(ServiceName))
                {
                    if (sc.Status == ServiceControllerStatus.Running) return;
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(ServiceWaitSeconds));
                    Say(Msg.T("svc.started"));
                }
            }
            catch (Exception ex)
            {
                Say(Msg.T("svc.startfail", ex.Message));
            }
        }

        private static bool TryRelaunchElevated()
        {
            if (OwnPath.Length == 0) return false;
            try
            {
                Say(Msg.T("svc.elevate"));
                ProcessStartInfo psi = new ProcessStartInfo(OwnPath);
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.Arguments = "--elevated --lang " + Msg.CurrentLanguage + (Verbose ? " --verbose" : "");
                Process p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit();
                return true;
            }
            catch (Exception)
            {
                return false; // UAC declined, or no admin rights at all
            }
        }

        // ---------------------------------------------------------------- processes

        private sealed class Proc
        {
            public int Pid;
            public string Name = "";
            public string Path = "";
        }

        private sealed class Target
        {
            public int Pid;
            public string Name = "";
            public string Reason = "";
        }

        private sealed class WmiInfo
        {
            public int ParentPid;
            public string CommandLine = "";
            public DateTime Created = DateTime.MinValue;
        }

        private static List<Proc> EnumerateProcesses()
        {
            List<Proc> list = new List<Proc>();
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception) { return list; }

            foreach (Process p in all)
            {
                Proc item = new Proc();
                try { item.Pid = p.Id; item.Name = p.ProcessName; }
                catch (Exception) { continue; }
                item.Path = ImagePathOf(item.Pid);
                list.Add(item);
                try { p.Dispose(); }
                catch (Exception) { }
            }
            return list;
        }

        private static string ImagePathOf(int pid)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                if (Native.QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString();
                return "";
            }
            finally { Native.CloseHandle(h); }
        }

        private static Dictionary<int, WmiInfo> ProcessMetadata()
        {
            Dictionary<int, WmiInfo> map = new Dictionary<int, WmiInfo>();
            try
            {
                using (ManagementObjectSearcher s = new ManagementObjectSearcher(
                    "SELECT ProcessId, ParentProcessId, CommandLine, CreationDate FROM Win32_Process"))
                {
                    foreach (ManagementObject mo in s.Get())
                    {
                        try
                        {
                            object pid = mo["ProcessId"];
                            if (pid == null) continue;
                            WmiInfo w = new WmiInfo();
                            object ppid = mo["ParentProcessId"];
                            if (ppid != null) w.ParentPid = Convert.ToInt32(ppid);
                            object cmd = mo["CommandLine"];
                            if (cmd != null) w.CommandLine = cmd.ToString();
                            object created = mo["CreationDate"];
                            if (created != null)
                            {
                                try { w.Created = ManagementDateTimeConverter.ToDateTime(created.ToString()); }
                                catch (Exception) { }
                            }
                            map[Convert.ToInt32(pid)] = w;
                        }
                        catch (Exception) { }
                    }
                }
            }
            catch (Exception) { }
            return map;
        }

        // Legacy (unpackaged) install locations. The MSIX build redirects all of
        // these under LocalAppData\Packages\<family>\LocalCache.
        private static List<string> UserRoots()
        {
            List<string> roots = new List<string>();
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(appData, "Claude"));
            roots.Add(Path.Combine(local, "AnthropicClaude"));
            roots.Add(Path.Combine(local, "Claude"));
            roots.Add(Path.Combine(home, ".claude"));
            return roots;
        }

        // Everything the packaged app writes lands here, including the bundled CLI.
        private static string PackageDataRoot(Install info)
        {
            if (info.PackageFamilyName.Length == 0) return "";
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", info.PackageFamilyName);
        }

        private static List<Target> FindTargets(Install info)
        {
            List<Proc> procs = EnumerateProcesses();
            Dictionary<int, WmiInfo> meta = ProcessMetadata();
            List<string> roots = UserRoots();
            string dataRoot = PackageDataRoot(info);

            Dictionary<int, Target> found = new Dictionary<int, Target>();

            // Pass 1 - direct matches on where the executable actually lives.
            foreach (Proc p in procs)
            {
                if (IsSelf(p)) continue;
                string reason = DirectReason(p, info, roots, dataRoot, meta);
                if (reason.Length == 0) continue;
                Target t = new Target();
                t.Pid = p.Pid;
                t.Name = p.Name;
                t.Reason = reason;
                found[p.Pid] = t;
            }

            // Pass 2 - anything descended from a match: MCP servers, hook scripts,
            // shells the CLI spawned. Their own paths say nothing about Claude, so
            // the parent chain is the only honest signal.
            Dictionary<int, Proc> byPid = new Dictionary<int, Proc>();
            foreach (Proc p in procs) byPid[p.Pid] = p;

            foreach (Proc p in procs)
            {
                if (IsSelf(p) || found.ContainsKey(p.Pid)) continue;
                if (!IsDescendantOfMatch(p.Pid, found, meta, byPid)) continue;
                Target t = new Target();
                t.Pid = p.Pid;
                t.Name = p.Name;
                t.Reason = Msg.T("reason.child");
                found[p.Pid] = t;
            }

            return found.Values.ToList();
        }

        private static bool IsSelf(Proc p)
        {
            if (p.Pid == OwnPid) return true;
            if (OwnPath.Length > 0 && string.Equals(p.Path, OwnPath, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string DirectReason(Proc p, Install info, List<string> roots, string dataRoot,
                                           Dictionary<int, WmiInfo> meta)
        {
            if (string.Equals(p.Name, "cowork-svc", StringComparison.OrdinalIgnoreCase))
                return Msg.T("reason.svcproc");

            if (p.Path.Length > 0)
            {
                if (info.PackageDir.Length > 0 &&
                    p.Path.StartsWith(info.PackageDir + "\\", StringComparison.OrdinalIgnoreCase))
                    return Msg.T("reason.msix");

                if (PackageRootOf(p.Path).Length > 0)
                    return Msg.T("reason.msixalt");

                if (dataRoot.Length > 0 && p.Path.StartsWith(dataRoot + "\\", StringComparison.OrdinalIgnoreCase))
                    return Msg.T("reason.data");

                if (Regex.IsMatch(p.Path, @"\\Packages\\Claude_[^\\]+\\", RegexOptions.IgnoreCase))
                    return Msg.T("reason.data");

                if (roots.Any(r => p.Path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)))
                    return Msg.T("reason.cli");
            }

            if (string.Equals(p.Name, "node", StringComparison.OrdinalIgnoreCase))
            {
                WmiInfo w;
                if (meta.TryGetValue(p.Pid, out w) && IsClaudeNode(w.CommandLine))
                    return Msg.T("reason.node");
            }

            return "";
        }

        private static bool IsDescendantOfMatch(int pid, Dictionary<int, Target> matches,
                                                Dictionary<int, WmiInfo> meta, Dictionary<int, Proc> byPid)
        {
            int current = pid;
            for (int depth = 0; depth < 24; depth++)
            {
                WmiInfo w;
                if (!meta.TryGetValue(current, out w) || w.ParentPid == 0 || w.ParentPid == current) return false;

                // Guard against PID reuse: a real parent cannot be younger than its child.
                WmiInfo pw;
                if (meta.TryGetValue(w.ParentPid, out pw))
                {
                    if (pw.Created != DateTime.MinValue && w.Created != DateTime.MinValue && pw.Created > w.Created)
                        return false;
                }
                else return false;

                if (w.ParentPid == OwnPid) return false;
                if (matches.ContainsKey(w.ParentPid)) return true;
                if (!byPid.ContainsKey(w.ParentPid)) return false;
                current = w.ParentPid;
            }
            return false;
        }

        // Deliberately narrow: a bare "claude" substring would match any node
        // process merely running out of a folder with claude in its name.
        private static bool IsClaudeNode(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return false;
            if (cmd.IndexOf("claude-repair", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (cmd.IndexOf("claude-killer", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return Regex.IsMatch(cmd,
                @"claude-code|[\\/]\.claude[\\/]|[\\/]claude\.exe|@anthropic-ai[\\/]claude|CLAUDE_CONFIG_DIR|\\Packages\\Claude_",
                RegexOptions.IgnoreCase);
        }

        private static int KillAll(Install info)
        {
            int killed = 0;
            // Two passes: killing a parent can leave a child that only becomes
            // visible once the parent is gone.
            for (int pass = 0; pass < 2; pass++)
            {
                List<Target> targets = FindTargets(info);
                if (targets.Count == 0) break;

                List<Process> waiting = new List<Process>();
                foreach (Target t in targets)
                {
                    try
                    {
                        Process p = Process.GetProcessById(t.Pid);
                        p.Kill();
                        waiting.Add(p);
                        killed++;
                    }
                    catch (ArgumentException)
                    {
                        // Already gone - normally because stopping the service took
                        // it down first. Not a problem, not worth reporting.
                    }
                    catch (Exception ex)
                    {
                        // cowork-svc runs as LocalSystem; stopping the service is
                        // what clears it, so a denial here is expected.
                        Say(Msg.T("proc.failed", t.Pid, t.Name, ex.Message));
                    }
                }
                foreach (Process p in waiting)
                {
                    try { p.WaitForExit(ProcessExitWaitMs); }
                    catch (Exception) { }
                    try { p.Dispose(); }
                    catch (Exception) { }
                }
            }
            return killed;
        }

        // ---------------------------------------------------------------- locks

        private static readonly string[] LockNames =
        {
            "SingletonLock", "SingletonCookie", "SingletonSocket", "lockfile", ".lock", "update.lock"
        };

        private static int CleanLocks(Install info)
        {
            List<string> dirs = UserRoots();

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            dirs.Add(Path.Combine(appData, "Claude", "claude-code"));

            // MSIX redirects every per-user write under LocalAppData\Packages, so
            // this - not %APPDATA%\Claude - is where the real lock files sit.
            string pkgRoot = PackageDataRoot(info);
            if (pkgRoot.Length > 0)
            {
                string roaming = Path.Combine(pkgRoot, "LocalCache", "Roaming");
                string localCache = Path.Combine(pkgRoot, "LocalCache", "Local");
                dirs.Add(Path.Combine(roaming, "Claude"));
                dirs.Add(Path.Combine(roaming, "Claude", "claude-code"));
                dirs.Add(Path.Combine(localCache, "Claude"));
                dirs.Add(Path.Combine(localCache, "AnthropicClaude"));
                dirs.Add(Path.Combine(pkgRoot, "LocalState"));
                dirs.Add(Path.Combine(pkgRoot, "LocalCache"));
            }

            int removed = 0;
            foreach (string dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(dir)) continue;

                foreach (string name in LockNames)
                    removed += DeleteIfPresent(Path.Combine(dir, name));

                removed += DeleteMatching(dir, "*.lock");

                string locksDir = Path.Combine(dir, "locks");
                if (Directory.Exists(locksDir)) removed += DeleteMatching(locksDir, "*");
            }
            return removed;
        }

        private static int DeleteIfPresent(string path)
        {
            try
            {
                if (File.Exists(path)) { File.Delete(path); Say(Msg.T("locks.deleted", path)); return 1; }
                if (Directory.Exists(path)) { Directory.Delete(path, true); Say(Msg.T("locks.deleted", path)); return 1; }
            }
            catch (Exception ex) { Say(Msg.T("locks.locked", path, ex.Message)); }
            return 0;
        }

        private static int DeleteMatching(string dir, string pattern)
        {
            int n = 0;
            try
            {
                foreach (string f in Directory.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly))
                    n += DeleteIfPresent(f);
            }
            catch (Exception) { }
            return n;
        }

        // ---------------------------------------------------------------- launch

        private static bool Activate(Install info)
        {
            if (info.Aumid.Length == 0)
            {
                Say(Msg.T("act.nopackage"));
                return ActivateLegacy();
            }

            try
            {
                Native.ApplicationActivationManager mgr = new Native.ApplicationActivationManager();
                Native.IApplicationActivationManager aam = (Native.IApplicationActivationManager)mgr;
                uint pid;
                int hr = aam.ActivateApplication(info.Aumid, null, Native.ActivateOptions.None, out pid);
                if (hr == 0)
                {
                    Say(Msg.T("act.viaaumid", pid));
                    return true;
                }
                Say(Msg.T("act.hr", hr.ToString("X8")));
            }
            catch (Exception ex)
            {
                Say(Msg.T("act.exception", ex.Message));
            }

            // Shell fallback. explorer.exe runs unelevated, which is what a packaged
            // app needs anyway.
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + info.Aumid);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
                Say(Msg.T("act.viashell"));
                return true;
            }
            catch (Exception ex)
            {
                Say(Msg.T("act.shellfail", ex.Message));
                return false;
            }
        }

        // Squirrel-style install (the pre-MSIX build, still seen on managed
        // machines): a stub next to versioned app-<n> folders.
        private static bool ActivateLegacy()
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude");
            if (!Directory.Exists(root))
            {
                Say(Msg.T("act.legacynone", root));
                return false;
            }

            List<string> candidates = new List<string>();
            string stub = Path.Combine(root, "claude.exe");
            if (File.Exists(stub)) candidates.Add(stub);

            try
            {
                string[] versionDirs = Directory.GetDirectories(root, "app-*");
                Array.Sort(versionDirs, StringComparer.OrdinalIgnoreCase);
                for (int i = versionDirs.Length - 1; i >= 0; i--)
                {
                    string exe = Path.Combine(versionDirs[i], "claude.exe");
                    if (File.Exists(exe)) candidates.Add(exe);
                }
            }
            catch (Exception) { }

            foreach (string exe in candidates)
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(exe);
                    psi.UseShellExecute = true;
                    psi.WorkingDirectory = Path.GetDirectoryName(exe);
                    Process.Start(psi);
                    Say(Msg.T("act.legacyrun", exe));
                    return true;
                }
                catch (Exception ex) { Say(Msg.T("act.legacyfail", exe, ex.Message)); }
            }

            Say(Msg.T("act.noexe"));
            return false;
        }

        // Accepts any Claude package process, not just the version we expected - a
        // repaired registration may legitimately come back on a different one.
        private static bool WaitForApp(Install info)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < VerifyTimeoutMs)
            {
                foreach (Proc p in EnumerateProcesses())
                {
                    if (p.Pid == OwnPid) continue;

                    if (p.Path.Length > 0)
                    {
                        if (PackageRootOf(p.Path).Length > 0) return true;
                        if (info.PackageDir.Length > 0 &&
                            p.Path.StartsWith(info.PackageDir + "\\", StringComparison.OrdinalIgnoreCase))
                            return true;
                    }

                    // Legacy, unpackaged install: no package path to match on.
                    if (info.PackageDir.Length == 0 &&
                        string.Equals(p.Name, "claude", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                System.Threading.Thread.Sleep(750);
            }
            return false;
        }

        // ---------------------------------------------------------------- who holds the file

        private sealed class Holder
        {
            public int Pid;
            public string App = "";
            public string Service = "";
            public uint SessionId;
            public string Path = "";
        }

        // Asks Windows directly which processes hold the package files, instead of
        // assuming they must be Claude's.
        private static List<Holder> WhoHoldsFiles(List<string> files, out string error)
        {
            List<Holder> holders = new List<Holder>();
            error = "";

            if (files.Count == 0) { error = "no files to probe"; return holders; }

            uint session;
            StringBuilder key = new StringBuilder(Native.CCH_RM_SESSION_KEY + 1);
            int rc = Native.RmStartSession(out session, 0, key);
            if (rc != 0) { error = "RmStartSession error " + rc; return holders; }

            try
            {
                rc = Native.RmRegisterResources(session, (uint)files.Count, files.ToArray(), 0, null, 0, null);
                if (rc != 0) { error = "RmRegisterResources error " + rc; return holders; }

                uint needed = 0, count = 0, reasons = 0;
                rc = Native.RmGetList(session, out needed, ref count, null, out reasons);

                if (rc == Native.ERROR_MORE_DATA && needed > 0)
                {
                    Native.RM_PROCESS_INFO[] info = new Native.RM_PROCESS_INFO[needed];
                    count = needed;
                    rc = Native.RmGetList(session, out needed, ref count, info, out reasons);
                    if (rc == 0)
                    {
                        for (int i = 0; i < count; i++)
                        {
                            Holder h = new Holder();
                            h.Pid = info[i].Process.dwProcessId;
                            h.App = info[i].strAppName;
                            h.Service = info[i].strServiceShortName;
                            h.SessionId = info[i].TSSessionId;
                            h.Path = ImagePathOf(h.Pid);
                            holders.Add(h);
                        }
                    }
                    else error = "RmGetList error " + rc;
                }
                else if (rc != 0) error = "RmGetList error " + rc;
            }
            finally { Native.RmEndSession(session); }

            return holders;
        }

        private static List<string> PackageFilesToProbe(Install info)
        {
            List<string> files = new List<string>();
            if (info.PackageDir.Length == 0) return files;

            string app = Path.Combine(info.PackageDir, "app");
            files.Add(Path.Combine(app, "Claude.exe"));
            files.Add(Path.Combine(app, "resources", "cowork-svc.exe"));
            files.Add(Path.Combine(info.PackageDir, "AppxManifest.xml"));

            // WindowsApps normally denies directory listing to non-admins; if it
            // happens to work, probe a wider set.
            try
            {
                foreach (string f in Directory.GetFiles(app, "*.dll", SearchOption.TopDirectoryOnly))
                {
                    files.Add(f);
                    if (files.Count > 40) break;
                }
            }
            catch (Exception) { }

            return files.Where(f => { try { return File.Exists(f); } catch (Exception) { return false; } }).ToList();
        }

        private static string RunPowerShell(string command)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + command.Replace("\"", "\\\"") + "\"");
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    string outText = p.StandardOutput.ReadToEnd();
                    string errText = p.StandardError.ReadToEnd();
                    p.WaitForExit(60000);
                    return (outText + errText).Trim();
                }
            }
            catch (Exception ex) { return "(cannot run: " + ex.Message + ")"; }
        }

        private static void ReportEvidence(Install info, List<Target> targets)
        {
            Say("");
            Say(Msg.T("ev.identity"));
            Say(Msg.T("ev.source", info.IdentitySource));
            Say(Msg.T("ev.direxists", Directory.Exists(info.PackageDir) ? Msg.T("val.yes") : Msg.T("val.no")));
            Say(Msg.T("ev.registered", info.PackageRegistered ? Msg.T("val.yes") : Msg.T("val.no")));
            Say(Msg.T("ev.aumidsrc", info.AumidSource));
            if (info.AllAumids.Count > 0) Say(Msg.T("ev.allaumids", string.Join(", ", info.AllAumids.ToArray())));

            Say("");
            Say(Msg.T("ev.packages"));
            string pkgs = RunPowerShell(
                "Get-AppxPackage -Name Claude* | ForEach-Object { '  ' + $_.PackageFullName + ' | Status=' + $_.Status + ' | Install=' + $_.InstallLocation }");
            Say(pkgs.Length > 0 ? pkgs : Msg.T("ev.nopackages"));

            Say("");
            Say(Msg.T("dep.header"));
            List<Dependency> deps = GetDependencies();
            if (deps.Count == 0) Say(Msg.T("dep.nodeps"));
            foreach (Dependency d in deps) Say(Msg.T("dep.item", d.FullName + " | " + d.Status));

            // A Developer-signed package will not activate on a machine where
            // sideloading is disabled by policy - common on managed hardware, and
            // invisible from the package's own Status.
            Say("");
            Say(Msg.T("ev.policy"));
            Say(Msg.T("ev.sigkind", SignatureKind()));
            Say(Msg.T("ev.allowtrusted", ReadUnlockPolicy("AllowAllTrustedApps")));
            Say(Msg.T("ev.allowdev", ReadUnlockPolicy("AllowDevelopmentWithoutDevLicense")));

            Say("");
            Say(Msg.T("ev.holders"));
            List<string> probe = PackageFilesToProbe(info);
            Say(Msg.T("ev.probed", probe.Count));
            string rmError;
            List<Holder> holders = WhoHoldsFiles(probe, out rmError);
            if (rmError.Length > 0) Say("  " + rmError);
            if (holders.Count == 0) Say(Msg.T("ev.nobody"));
            foreach (Holder h in holders)
            {
                Say("  [" + h.Pid + "] " + h.App +
                    (h.Service.Length > 0 ? " (service: " + h.Service + ")" : "") +
                    " session=" + h.SessionId);
                if (h.Path.Length > 0) Say("        " + h.Path);
            }

            // The deployment log usually states outright why activation was refused.
            Say("");
            Say(Msg.T("ev.deploylog"));
            string appxLog = RunPowerShell(
                "try { Get-AppxLog -ErrorAction Stop | Where-Object { $_.Level -eq 'Error' } | " +
                "Select-Object -First 6 | ForEach-Object { '  ' + $_.Message } } catch { }");
            Say(appxLog.Length > 0 ? appxLog : Msg.T("ev.nolog"));

            Say("");
            Say(Msg.T("ev.session"));
            Say(Msg.T("ev.ownsession", Process.GetCurrentProcess().SessionId));
            Say(Msg.T("ev.admin", IsAdmin() ? Msg.T("val.yes") : Msg.T("val.no")));
        }

        private static string SignatureKind()
        {
            string s = RunPowerShell(
                "(Get-AppxPackage -Name Claude* | Sort-Object Version -Descending | Select-Object -First 1).SignatureKind");
            return s.Length > 0 ? s.Trim() : Msg.T("val.notfound");
        }

        // Both live under AppModelUnlock. Absent means "default", which on managed
        // machines is usually enforced to 0 by group policy.
        private static string ReadUnlockPolicy(string valueName)
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock"))
                {
                    if (k == null) return Msg.T("ev.notset");
                    object v = k.GetValue(valueName);
                    return v == null ? Msg.T("ev.notset") : v.ToString();
                }
            }
            catch (Exception) { return Msg.T("ev.notset"); }
        }

        private static bool SideloadingBlocked()
        {
            string kind = SignatureKind();
            if (kind.IndexOf("Developer", StringComparison.OrdinalIgnoreCase) < 0) return false;
            return ReadUnlockPolicy("AllowAllTrustedApps") == "0";
        }

        private static bool IsAdmin()
        {
            try
            {
                System.Security.Principal.WindowsPrincipal wp =
                    new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent());
                return wp.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch (Exception) { return false; }
        }

        // ---------------------------------------------------------------- output

        private static void Say(string line)
        {
            Log.Add(line);
        }

        private static void Report(string title, MessageBoxIcon icon)
        {
            string text = string.Join(Environment.NewLine, Log.ToArray());

            if (LogFile.Length > 0)
            {
                try { File.WriteAllText(LogFile, title + Environment.NewLine + text, Encoding.UTF8); return; }
                catch (Exception) { }
            }

            // Launched from a terminal? Print there instead of popping a dialog.
            if (WriteToParentConsole(title, text)) return;

            MessageBox.Show(text, title, MessageBoxButtons.OK, icon);
        }

        private static bool WriteToParentConsole(string title, string text)
        {
            try
            {
                if (!Native.AttachConsole(Native.ATTACH_PARENT_PROCESS)) return false;
                StreamWriter w = new StreamWriter(Console.OpenStandardOutput());
                w.AutoFlush = true;
                Console.SetOut(w);
                Console.WriteLine();
                Console.WriteLine("== " + title + " ==");
                Console.WriteLine(text);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static void ShowHelp()
        {
            MessageBox.Show(Msg.T("help.body").Replace("\n", Environment.NewLine),
                "Claude repair", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    internal static class Native
    {
        internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        internal const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool AttachConsole(uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder exeName, ref uint size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern int OpenPackageInfoByFullName(string packageFullName, uint reserved, out IntPtr packageInfoReference);

        [DllImport("kernel32.dll")]
        internal static extern int ClosePackageInfo(IntPtr packageInfoReference);

        [DllImport("kernel32.dll")]
        internal static extern int GetPackageApplicationIds(IntPtr packageInfoReference, ref uint bufferLength, IntPtr buffer, out uint count);

        // Lists the packages actually REGISTERED for this user - as opposed to
        // whatever the service's ImagePath still points at, which during an update
        // is the new version that is not registered yet.
        internal const uint PACKAGE_FILTER_HEAD = 0x00000010;
        internal const uint PACKAGE_FILTER_DIRECT = 0x00000020;
        internal const int ERROR_INSUFFICIENT_BUFFER = 122;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern int FindPackagesByPackageFamily(string packageFamilyName, uint packageFilters,
            ref uint count, IntPtr packageFullNames, ref uint bufferLength, IntPtr buffer, IntPtr packageProperties);

        // Restart Manager - the API installers use to answer "which programs are
        // using these files". Works without admin for same-session processes and
        // reports services too, which is the only honest way to find a lock holder
        // that is not a Claude process at all.
        internal const int CCH_RM_SESSION_KEY = 32;
        internal const int CCH_RM_MAX_APP_NAME = 255;
        internal const int CCH_RM_MAX_SVC_NAME = 63;
        internal const int ERROR_MORE_DATA = 234;

        [StructLayout(LayoutKind.Sequential)]
        internal struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
            public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        internal static extern int RmStartSession(out uint sessionHandle, int sessionFlags, StringBuilder sessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        internal static extern int RmRegisterResources(uint sessionHandle,
            uint nFiles, string[] rgsFilenames,
            uint nApplications, RM_UNIQUE_PROCESS[] rgApplications,
            uint nServices, string[] rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        internal static extern int RmGetList(uint sessionHandle, out uint procInfoNeeded,
            ref uint procInfo, [In, Out] RM_PROCESS_INFO[] processInfo, out uint rebootReasons);

        [DllImport("rstrtmgr.dll")]
        internal static extern int RmEndSession(uint sessionHandle);

        internal enum ActivateOptions
        {
            None = 0,
            DesignMode = 1,
            NoErrorUI = 2,
            NoSplashScreen = 4
        }

        // Only ActivateApplication is declared; it is first in the vtable after
        // IUnknown, so the remaining methods never need to be described.
        [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IApplicationActivationManager
        {
            [PreserveSig]
            int ActivateApplication(
                [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
                [MarshalAs(UnmanagedType.LPWStr)] string arguments,
                ActivateOptions options,
                out uint processId);
        }

        [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
        internal class ApplicationActivationManager
        {
        }
    }
}
