// ClaudeKiller - unsticks Claude on Windows after a failed update.
//
// Root cause (anthropics/claude-code issues #76357, #42776, #42897, #41743):
// Claude Desktop ships as an MSIX package. Two things keep file locks on the
// package after the window closes, so the updater cannot swap the files and the
// app refuses to relaunch ("another program is currently using this file"):
//   1. CoworkVMService - a LocalSystem Windows service displayed simply as
//      "Claude", running <package>\app\resources\cowork-svc.exe. It is
//      AUTO_START and independent of the app window, which is why it never
//      shows up on the Processes tab of Task Manager.
//   2. Orphaned Claude.exe helper processes from the WindowsApps package.
//
// Everyone online reboots. That is unnecessary: the service SDDL grants
// Authenticated Users SERVICE_START|SERVICE_STOP (the RP/WP rights in
// "D:(A;;CCLCSWRPWPDTLOCRRC;;;AU)"), so a plain user can cycle it without UAC.
//
// Targets .NET Framework 4.8 and is compiled with the in-box csc.exe, so the
// output is a ~30 KB exe with zero install footprint. Keep the syntax C# 5
// compatible: no string interpolation, no ?., no nameof, no out-var.

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
        private static string LogFile = "";
        private static bool LaunchOnly;
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
                else if (a == "log" && i + 1 < argv.Length) LogFile = argv[++i];
                else if (a == "launch" || a == "start") LaunchOnly = true;
                else if (a == "h" || a == "help" || a == "?") { ShowHelp(); return 0; }
            }

            try
            {
                return Run();
            }
            catch (Exception ex)
            {
                Say("FATAL: " + ex.GetType().Name + ": " + ex.Message);
                Report("Claude Killer - hiba", MessageBoxIcon.Error);
                return 2;
            }
        }

        private static int Run()
        {
            Install info = Discover();

            Say("Csomag:      " + (info.PackageFullName.Length > 0 ? info.PackageFullName : "(nem talalhato)"));
            Say("AUMID:       " + (info.Aumid.Length > 0 ? info.Aumid : "(nem talalhato)"));
            Say("Szolgaltatas: " + info.ServiceState);

            if (LaunchOnly)
            {
                bool started = Activate(info);
                Say(started ? "Inditas rendben." : "Az inditas nem sikerult.");
                Report("Claude Killer - inditas", started ? MessageBoxIcon.Information : MessageBoxIcon.Error);
                return started ? 0 : 1;
            }

            List<Target> targets = FindTargets(info);
            Say("Celpont folyamatok: " + targets.Count);
            foreach (Target t in targets) Say("  [" + t.Pid + "] " + t.Name + " - " + t.Reason);

            if (DryRun)
            {
                Say("");
                Say("-- osszes claude/node/cowork folyamat --");
                foreach (Proc p in EnumerateProcesses())
                {
                    if (!Regex.IsMatch(p.Name, @"^(claude|node|cowork-svc)$", RegexOptions.IgnoreCase)) continue;
                    Say("  [" + p.Pid + "] " + p.Name + " path='" + p.Path + "'" +
                        (targets.Any(t => t.Pid == p.Pid) ? " -> CEL" : " -> kihagyva"));
                }
                ReportEvidence(info, targets);
                Say("");
                Say("DIAGNOSZTIKA MOD - semmi nem lett modositva.");

                // This report is far too long for a dialog. Put it in a file and
                // open it, so it can be read, saved and pasted somewhere.
                bool opened = false;
                if (LogFile.Length == 0)
                {
                    LogFile = Path.Combine(Path.GetTempPath(), "claude-killer-diagnose.txt");
                    opened = true;
                }
                Report("Claude Killer - diagnosztika", MessageBoxIcon.Information);
                if (opened)
                {
                    try { Process.Start("notepad.exe", "\"" + LogFile + "\""); }
                    catch (Exception) { }
                }
                return 0;
            }

            // 1. Drop the service first. It is the lock nobody can see, and it
            //    respawns cowork-svc.exe if we only kill the process.
            bool serviceWasPresent = info.ServiceState != "hianyzik";
            if (serviceWasPresent && !StopService())
            {
                if (!AlreadyElevated && TryRelaunchElevated()) return 0;
                Say("FIGYELEM: a szolgaltatast nem sikerult leallitani, folytatom.");
            }

            // 2. Kill everything Claude-owned, re-scanning so children spawned
            //    during the service stop are caught too.
            Say("Kilott folyamatok: " + KillAll(info));

            // 3. Stale single-instance and update locks are what produce the
            //    "already running" lie once the processes are actually gone.
            Say("Torolt lock fajlok: " + CleanLocks(info));

            // 4. Escalating repair. Each rung is verified by actually watching for
            //    a new process; the first one that works wins and the rest never
            //    run. Nothing here assumes why the previous rung failed.
            if (TryActivateAndVerify(info, "alaphelyzet"))
                return Finish(serviceWasPresent, true);

            if (RepairRegistration(ref info) && TryActivateAndVerify(info, "ujraregisztralas utan"))
                return Finish(serviceWasPresent, true);

            if (KillSafeHolders(info) > 0 && TryActivateAndVerify(info, "zarolok kilovese utan"))
                return Finish(serviceWasPresent, true);

            Say("");
            Say("HIBA: a Claude egyik javitasi lepes utan sem indult el.");
            ReportEvidence(info, FindTargets(info));
            AdviseFinalSteps(info);
            return Finish(serviceWasPresent, false);
        }

        // ---------------------------------------------------------------- repair ladder

        private static int Finish(bool serviceWasPresent, bool ok)
        {
            if (serviceWasPresent) StartService();

            if (ok)
            {
                Say("KESZ - a Claude ujraindult.");
                if (Verbose) Report("Claude Killer - kesz", MessageBoxIcon.Information);
                return 0;
            }

            Report("Claude Killer - nem sikerult", MessageBoxIcon.Error);
            return 1;
        }

        private static bool TryActivateAndVerify(Install info, string label)
        {
            Say("");
            Say("-- probalkozas: " + label + " --");
            if (!Activate(info)) return false;
            bool up = WaitForApp(info);
            Say(up ? "  sikerult" : "  nem jott fel " + (VerifyTimeoutMs / 1000) + " masodperc alatt");
            return up;
        }

        // Re-registers the package for the current user. This is the repair for a
        // half-applied update: the files are on disk but the registration is
        // broken or points at a version that is not really there. No admin needed
        // - a user may re-register a package that is already installed for them.
        private static bool RepairRegistration(ref Install info)
        {
            Say("");
            Say("-- csomag ujraregisztralasa --");

            string result = RunPowerShell(
                "$p = Get-AppxPackage -Name Claude*; " +
                "if (-not $p) { 'ERR: nincs regisztralt Claude csomag' } else { " +
                "foreach ($x in $p) { try { " +
                "Add-AppxPackage -DisableDevelopmentMode -Register ($x.InstallLocation + '\\AppxManifest.xml') -ForceApplicationShutdown; " +
                "'OK ' + $x.PackageFullName } catch { 'ERR: ' + $_.Exception.Message } } }");

            foreach (string line in result.Split('\n'))
            {
                string s = line.Trim();
                if (s.Length > 0) Say("  " + s);
            }

            // Registration may now resolve to a different version, so the identity
            // has to be re-derived before the next activation attempt.
            info = Discover();
            Say("  azonossag most: " + (info.PackageFullName.Length > 0 ? info.PackageFullName : "(nincs)") +
                " | AUMID forrasa: " + info.AumidSource);

            return info.Aumid.Length > 0;
        }

        private static readonly Regex SecuritySoftware = new Regex(
            @"defender|msmpeng|mssense|sense(ir|ndr|cn)|falcon|crowdstrike|sentinel|carbonblack|cbdefense|" +
            @"cylance|sophos|symantec|mcafee|mfe|trendmicro|tmccsf|kaspersky|avp\b|eset|ekrn|bitdefender|" +
            @"norton|webroot|tanium|qualys|nessus|forcepoint|netskope|zscaler|xagt|fireeye",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Only touches things that are plainly ours to touch. Security agents,
        // system services and other users' sessions are named, never killed -
        // terminating those would be both wrong and useless.
        private static string WhyNotSafeToKill(Holder h, int mySession)
        {
            if (h.Service.Length > 0 && !string.Equals(h.Service, ServiceName, StringComparison.OrdinalIgnoreCase))
                return "windows szolgaltatas";

            if (h.SessionId != (uint)mySession)
                return "masik munkamenet (session " + h.SessionId + ")";

            if (SecuritySoftware.IsMatch(h.App) || (h.Path.Length > 0 && SecuritySoftware.IsMatch(h.Path)))
                return "biztonsagi szoftver";

            if (h.Path.Length > 0)
            {
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (h.Path.StartsWith(win + "\\", StringComparison.OrdinalIgnoreCase))
                    return "rendszerfolyamat";
            }
            else return "az utvonala nem olvashato";

            return "";
        }

        private static int KillSafeHolders(Install info)
        {
            Say("");
            Say("-- ki fogja meg mindig a fajlokat --");

            string err;
            List<Holder> holders = WhoHoldsFiles(PackageFilesToProbe(info), out err);
            if (err.Length > 0) Say("  " + err);
            if (holders.Count == 0) { Say("  (senki lathato)"); return 0; }

            int mySession = Process.GetCurrentProcess().SessionId;
            int killed = 0;

            foreach (Holder h in holders)
            {
                if (h.Pid == OwnPid) continue;

                string why = WhyNotSafeToKill(h, mySession);
                if (why.Length > 0)
                {
                    Say("  [" + h.Pid + "] " + h.App + " - NEM bantom: " + why);
                    continue;
                }

                try
                {
                    Process p = Process.GetProcessById(h.Pid);
                    p.Kill();
                    p.WaitForExit(ProcessExitWaitMs);
                    killed++;
                    Say("  [" + h.Pid + "] " + h.App + " kilove");
                }
                catch (ArgumentException) { }
                catch (Exception ex) { Say("  [" + h.Pid + "] " + h.App + " nem lott ki: " + ex.Message); }
            }

            Say("  kilott zarolok: " + killed);
            return killed;
        }

        // When every rung failed, say what specifically is in the way rather than
        // offering a generic "try rebooting".
        private static void AdviseFinalSteps(Install info)
        {
            Say("");
            Say("== MI A TEENDO ==");

            if (!info.PackageRegistered || info.Aumid.Length == 0)
            {
                Say("  A csomag nincs ervenyesen regisztralva ehhez a felhasznalohoz.");
                Say("  Telepitsd ujra a Claude Desktopot a claude.ai/download oldalrol.");
                Say("  (Vegso esetben, ADATVESZTESSEL: Get-AppxPackage -Name Claude* | Reset-AppxPackage)");
                return;
            }

            string err;
            List<Holder> holders = WhoHoldsFiles(PackageFilesToProbe(info), out err);
            int mySession = Process.GetCurrentProcess().SessionId;
            List<Holder> blocked = holders.Where(h => WhyNotSafeToKill(h, mySession).Length > 0).ToList();

            if (blocked.Count > 0)
            {
                Say("  A csomagot olyan program fogja, amihez nem nyulok:");
                foreach (Holder h in blocked)
                    Say("    - " + h.App + " (" + WhyNotSafeToKill(h, mySession) + ")");
                Say("  Ha ez biztonsagi szoftver, a rendszergazda tud ra kivetelt tenni a");
                Say("  WindowsApps\\Claude_* mappara. Addig a gep ujrainditasa segit.");
                return;
            }

            Say("  Nem talaltam blokkolo folyamatot, megsem indul. Valoszinuleg a");
            Say("  Windows csomagtelepitoje van felfuggesztett allapotban - a gep");
            Say("  ujrainditasa ezt tisztitja. Ha ujraindulas utan is all, telepitsd ujra.");
        }

        // ---------------------------------------------------------------- discovery

        private sealed class Install
        {
            public string PackageDir = "";
            public string PackageFullName = "";
            public string PackageFamilyName = "";
            public string Aumid = "";
            public string ServiceState = "hianyzik";

            // Provenance, so a failure can be told apart from a lucky guess.
            public string IdentitySource = "ismeretlen";
            public readonly List<string> AllAumids = new List<string>();
            public bool PackageRegistered;      // OpenPackageInfoByFullName succeeded
            public bool AumidResolved;          // read from the package, not constructed
            public string AumidSource = "nincs";
        }

        private static Install Discover()
        {
            Install info = new Install();

            // The service's ImagePath is readable by every user and survives even
            // when no Claude process is alive, so it is the most reliable anchor.
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
            // only the old one is still registered; activating the new identity
            // then fails with ERROR_CANCELLED. Registration is the authority.
            if (family.Length > 0)
            {
                info.PackageFamilyName = family;
                List<string> registered = RegisteredPackages(family);

                foreach (string fullName in registered)
                {
                    info.PackageFullName = fullName;
                    info.Aumid = ResolveAumid(info);
                    if (info.AumidResolved)
                    {
                        info.IdentitySource = "regisztralt csomag (FindPackagesByPackageFamily)";
                        info.PackageDir = InstallDirOf(fullName, hintDir);
                        break;
                    }
                }
            }

            // Step 3 - nothing registered resolved; fall back to the hint so the
            // process-killing and lock-clearing work still runs.
            if (!info.AumidResolved && hintDir.Length > 0)
            {
                info.PackageDir = hintDir;
                info.PackageFullName = Path.GetFileName(hintDir);
                info.PackageFamilyName = FamilyFromFullName(info.PackageFullName);
                info.IdentitySource = "szolgaltatas ImagePath (nincs regisztralt csomag)";
                info.Aumid = ResolveAumid(info);
            }

            if (info.PackageDir.Length == 0 && hintDir.Length > 0) info.PackageDir = hintDir;

            info.ServiceState = QueryServiceState();
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

        // Family name without any running process or service to go on.
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

            // Preferred: ask the package model itself. Works without admin.
            // If this fails the identity is stale - the registration points at a
            // package that is no longer really there, which is exactly what a
            // half-applied update looks like.
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
                                        info.AumidSource = "csomagbol kiolvasva";
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

            // Fallback: the shell's own app list.
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Classes\ActivatableClasses\Package\" + fullName + @"\Server"))
                {
                    if (k != null)
                    {
                        foreach (string sub in k.GetSubKeyNames())
                        {
                            if (sub.IndexOf("App", StringComparison.OrdinalIgnoreCase) >= 0)
                                return familyName + "!App";
                        }
                    }
                }
            }
            catch (Exception) { }

            // Last resort: the conventional application id. This is a GUESS - if the
            // package is not properly registered, activating it yields ERROR_CANCELLED.
            info.AumidSource = "TALALGATAS (a csomagot nem lehetett megnyitni)";
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
            catch (Exception) { return "hianyzik"; }
        }

        private static bool StopService()
        {
            try
            {
                using (ServiceController sc = new ServiceController(ServiceName))
                {
                    if (sc.Status == ServiceControllerStatus.Stopped)
                    {
                        Say("A szolgaltatas mar allt.");
                        return true;
                    }
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(ServiceWaitSeconds));
                    Say("Szolgaltatas leallitva.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Say("A szolgaltatast nem sikerult leallitani: " + ex.Message);
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
                    Say("Szolgaltatas ujrainditva.");
                }
            }
            catch (Exception ex)
            {
                Say("A szolgaltatast nem sikerult visszainditani: " + ex.Message);
            }
        }

        private static bool TryRelaunchElevated()
        {
            if (OwnPath.Length == 0) return false;
            try
            {
                Say("Ujraprobalom rendszergazdakent...");
                ProcessStartInfo psi = new ProcessStartInfo(OwnPath);
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.Arguments = "--elevated" + (Verbose ? " --verbose" : "");
                Process p = Process.Start(psi);
                if (p == null) return false;
                p.WaitForExit();
                return true;
            }
            catch (Exception)
            {
                return false; // user declined UAC, or has no admin rights at all
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

        private sealed class WmiInfo
        {
            public int ParentPid;
            public string CommandLine = "";
            public DateTime Created = DateTime.MinValue;
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
        // these under LocalAppData\Packages\<family>\LocalCache, which is handled
        // separately via PackageDataRoots.
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

        // C:\Users\<u>\AppData\Local\Packages\Claude_pzs8sxrjxfjjc - everything the
        // packaged app writes lands here, including the bundled claude-code CLI.
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
                t.Reason = "Claude gyerekfolyamat";
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
                return "cowork szolgaltatas folyamat";

            if (p.Path.Length > 0)
            {
                if (info.PackageDir.Length > 0 &&
                    p.Path.StartsWith(info.PackageDir + "\\", StringComparison.OrdinalIgnoreCase))
                    return "MSIX csomag";

                if (PackageRootOf(p.Path).Length > 0)
                    return "MSIX csomag (masik verzio)";

                if (dataRoot.Length > 0 && p.Path.StartsWith(dataRoot + "\\", StringComparison.OrdinalIgnoreCase))
                    return "MSIX adatmappa (CLI)";

                // Same idea without a known family name, e.g. if the service is gone.
                if (Regex.IsMatch(p.Path, @"\\Packages\\Claude_[^\\]+\\", RegexOptions.IgnoreCase))
                    return "MSIX adatmappa (CLI)";

                if (roots.Any(r => p.Path.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase)))
                    return "Claude Code CLI";
            }

            if (string.Equals(p.Name, "node", StringComparison.OrdinalIgnoreCase))
            {
                WmiInfo w;
                if (meta.TryGetValue(p.Pid, out w) && IsClaudeNode(w.CommandLine))
                    return "Claude node folyamat";
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
                        // what actually clears it, so a denial here is expected.
                        Say("  [" + t.Pid + "] " + t.Name + " nem lott ki: " + ex.Message);
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
                if (File.Exists(path)) { File.Delete(path); Say("  torolve: " + path); return 1; }
                if (Directory.Exists(path)) { Directory.Delete(path, true); Say("  torolve: " + path); return 1; }
            }
            catch (Exception ex) { Say("  nem torolheto: " + path + " (" + ex.Message + ")"); }
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
                Say("Nincs MSIX csomag, a klasszikus telepitest keresem.");
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
                    Say("Elinditva (AUMID, pid " + pid + ").");
                    return true;
                }
                Say("ActivateApplication hr=0x" + hr.ToString("X8") + ", probalom a shell utat.");
            }
            catch (Exception ex)
            {
                Say("ActivateApplication hiba: " + ex.Message + ", probalom a shell utat.");
            }

            // Shell fallback. explorer.exe runs unelevated, which is what a
            // packaged app needs anyway.
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + info.Aumid);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
                Say("Elinditva (shell:AppsFolder).");
                return true;
            }
            catch (Exception ex)
            {
                Say("A shell inditas is elbukott: " + ex.Message);
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
                Say("Klasszikus telepites sem talalhato: " + root);
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
                    Say("Elinditva: " + exe);
                    return true;
                }
                catch (Exception ex) { Say("Nem indult: " + exe + " (" + ex.Message + ")"); }
            }

            Say("Nem talaltam inditható claude.exe-t.");
            return false;
        }

        // Accepts any Claude package process, not just the version we expected -
        // a repaired registration may legitimately come back on a different one.
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

            if (files.Count == 0) { error = "nincs vizsgalhato fajl"; return holders; }

            uint session;
            StringBuilder key = new StringBuilder(Native.CCH_RM_SESSION_KEY + 1);
            int rc = Native.RmStartSession(out session, 0, key);
            if (rc != 0) { error = "RmStartSession hiba " + rc; return holders; }

            try
            {
                rc = Native.RmRegisterResources(session, (uint)files.Count, files.ToArray(), 0, null, 0, null);
                if (rc != 0) { error = "RmRegisterResources hiba " + rc; return holders; }

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
                    else error = "RmGetList hiba " + rc;
                }
                else if (rc != 0) error = "RmGetList hiba " + rc;
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
                    p.WaitForExit(30000);
                    return (outText + errText).Trim();
                }
            }
            catch (Exception ex) { return "(nem futtathato: " + ex.Message + ")"; }
        }

        private static void ReportEvidence(Install info, List<Target> targets)
        {
            Say("");
            Say("== CSOMAG AZONOSSAG ==");
            Say("  honnan:            " + info.IdentitySource);
            Say("  csomagmappa letezik: " + Directory.Exists(info.PackageDir));
            Say("  csomag regisztralt: " + (info.PackageRegistered ? "IGEN" : "NEM  <-- gyanus"));
            Say("  AUMID forrasa:     " + info.AumidSource);
            if (info.AllAumids.Count > 0) Say("  osszes AUMID:      " + string.Join(", ", info.AllAumids.ToArray()));

            Say("");
            Say("== REGISZTRALT CLAUDE CSOMAGOK (Get-AppxPackage) ==");
            string pkgs = RunPowerShell(
                "Get-AppxPackage -Name Claude* | ForEach-Object { '  ' + $_.PackageFullName + ' | Status=' + $_.Status + ' | Install=' + $_.InstallLocation }");
            Say(pkgs.Length > 0 ? pkgs : "  (nincs talalat - a csomag nincs regisztralva ehhez a felhasznalohoz)");

            Say("");
            Say("== KI FOGJA A CSOMAG FAJLJAIT (Restart Manager) ==");
            List<string> probe = PackageFilesToProbe(info);
            Say("  vizsgalt fajlok: " + probe.Count);
            string rmError;
            List<Holder> holders = WhoHoldsFiles(probe, out rmError);
            if (rmError.Length > 0) Say("  " + rmError);
            if (holders.Count == 0) Say("  (egyetlen lathato folyamat sem fogja - lehet mas felhasznalo vagy kernel szintu zar)");
            foreach (Holder h in holders)
            {
                Say("  [" + h.Pid + "] " + h.App +
                    (h.Service.Length > 0 ? " (szolgaltatas: " + h.Service + ")" : "") +
                    " session=" + h.SessionId);
                if (h.Path.Length > 0) Say("        " + h.Path);
            }

            Say("");
            Say("== MUNKAMENET ==");
            Say("  sajat session: " + Process.GetCurrentProcess().SessionId);
            Say("  admin: " + IsAdmin());
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
            MessageBox.Show(
                "Claude Killer" + Environment.NewLine + Environment.NewLine +
                "Leallitja a beragadt Claude folyamatokat es a CoworkVMService" + Environment.NewLine +
                "szolgaltatast, kitakaritja a lock fajlokat, majd ujrainditja a" + Environment.NewLine +
                "Claude Desktopot. Alapesetben nemaan fut." + Environment.NewLine + Environment.NewLine +
                "  (nincs kapcsolo)  javitas, csak hiba eseten szol" + Environment.NewLine +
                "  --diagnose        csak kiirja mit talalt, nem modosit" + Environment.NewLine +
                "  --verbose         javitas + osszegzes a vegen" + Environment.NewLine +
                "  --help            ez az ablak",
                "Claude Killer", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        // reports services too, which is the only honest way to find a lock
        // holder that is not a Claude process at all.
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
