// User-facing strings. English is the default; Hungarian is used only when the
// operating system's UI language is Hungarian. Add a language by appending a
// column here - nothing else needs to change.
//
// Keep code, identifiers and comments English-only. This file is the single
// place where localised text belongs.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudeKiller
{
    internal static class Msg
    {
        private enum Lang { En = 0, Hu = 1 }

        private static Lang _lang = DetectLanguage();

        private static Lang DetectLanguage()
        {
            try
            {
                string iso = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                if (string.Equals(iso, "hu", StringComparison.OrdinalIgnoreCase)) return Lang.Hu;
            }
            catch (Exception) { }
            return Lang.En;
        }

        // Lets --lang override the detected language, mainly so the output can be
        // checked in both without changing Windows settings.
        internal static void Override(string code)
        {
            if (string.IsNullOrEmpty(code)) return;
            if (code.StartsWith("hu", StringComparison.OrdinalIgnoreCase)) _lang = Lang.Hu;
            else if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase)) _lang = Lang.En;
        }

        internal static string CurrentLanguage
        {
            get { return _lang == Lang.Hu ? "hu" : "en"; }
        }

        internal static string T(string key)
        {
            string[] row;
            if (!Table.TryGetValue(key, out row)) return key;
            int i = (int)_lang;
            if (i < row.Length && !string.IsNullOrEmpty(row[i])) return row[i];
            return row[0];
        }

        internal static string T(string key, params object[] args)
        {
            string fmt = T(key);
            try { return string.Format(CultureInfo.CurrentCulture, fmt, args); }
            catch (FormatException) { return fmt; }
        }

        //                                   English                                              Hungarian
        private static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>
        {
            { "title.diagnose", new[] { "Claude repair - diagnostics",                            "Claude javito - diagnosztika" } },
            { "title.failed",   new[] { "Claude repair - failed",                                 "Claude javito - nem sikerult" } },
            { "title.done",     new[] { "Claude repair - done",                                   "Claude javito - kesz" } },
            { "title.error",    new[] { "Claude repair - error",                                  "Claude javito - hiba" } },
            { "title.launch",   new[] { "Claude repair - launch",                                 "Claude javito - inditas" } },

            { "hdr.package",    new[] { "Package:      {0}",                                      "Csomag:         {0}" } },
            { "hdr.aumid",      new[] { "AUMID:        {0}",                                      "AUMID:          {0}" } },
            { "hdr.service",    new[] { "Service:      {0}",                                      "Szolgaltatas:   {0}" } },
            { "val.notfound",   new[] { "(not found)",                                            "(nem talalhato)" } },
            { "val.absent",     new[] { "absent",                                                 "hianyzik" } },
            { "val.yes",        new[] { "yes",                                                    "igen" } },
            { "val.no",         new[] { "no",                                                     "nem" } },

            { "proc.targets",   new[] { "Processes to stop: {0}",                                  "Leallitando folyamatok: {0}" } },
            { "proc.killed",    new[] { "Processes stopped: {0}",                                  "Leallitott folyamatok: {0}" } },
            { "proc.failed",    new[] { "  [{0}] {1} could not be stopped: {2}",                   "  [{0}] {1} nem allithato le: {2}" } },
            { "locks.removed",  new[] { "Stale lock files removed: {0}",                           "Torolt beragadt lock fajlok: {0}" } },
            { "locks.deleted",  new[] { "  deleted: {0}",                                          "  torolve: {0}" } },
            { "locks.locked",   new[] { "  cannot delete: {0} ({1})",                              "  nem torolheto: {0} ({1})" } },

            { "reason.msix",    new[] { "MSIX package",                                            "MSIX csomag" } },
            { "reason.msixalt", new[] { "MSIX package (other version)",                             "MSIX csomag (masik verzio)" } },
            { "reason.data",    new[] { "MSIX data directory (CLI)",                                "MSIX adatmappa (CLI)" } },
            { "reason.cli",     new[] { "Claude Code CLI",                                          "Claude Code CLI" } },
            { "reason.svcproc", new[] { "cowork service process",                                   "cowork szolgaltatas folyamat" } },
            { "reason.node",    new[] { "Claude node process",                                      "Claude node folyamat" } },
            { "reason.child",   new[] { "Claude child process",                                     "Claude gyerekfolyamat" } },

            { "svc.stopped",    new[] { "Service stopped.",                                         "Szolgaltatas leallitva." } },
            { "svc.already",    new[] { "Service was already stopped.",                              "A szolgaltatas mar allt." } },
            { "svc.started",    new[] { "Service restarted.",                                        "Szolgaltatas ujrainditva." } },
            { "svc.stopfail",   new[] { "Could not stop the service: {0}",                          "A szolgaltatast nem sikerult leallitani: {0}" } },
            { "svc.startfail",  new[] { "Could not restart the service: {0}",                        "A szolgaltatast nem sikerult visszainditani: {0}" } },
            { "svc.continue",   new[] { "WARNING: continuing without stopping the service.",         "FIGYELEM: a szolgaltatas leallitasa nelkul folytatom." } },
            { "svc.retry",      new[] { "Re-registration restarted the service - stopping it again.", "Az ujraregisztralas visszainditotta a szolgaltatast - ujra leallitom." } },
            { "svc.elevate",    new[] { "Retrying as administrator...",                              "Ujraprobalom rendszergazdakent..." } },

            { "try.attempt",    new[] { "-- attempt: {0} --",                                        "-- probalkozas: {0} --" } },
            { "try.baseline",   new[] { "baseline",                                                  "alaphelyzet" } },
            { "try.deps",       new[] { "after repairing dependencies",                              "fuggosegek javitasa utan" } },
            { "try.reregister", new[] { "after re-registration",                                     "ujraregisztralas utan" } },
            { "try.holders",    new[] { "after releasing lock holders",                              "zarolok elengedese utan" } },
            { "try.ok",         new[] { "  succeeded",                                              "  sikerult" } },
            { "try.timeout",    new[] { "  did not come up within {0} seconds",                      "  nem jott fel {0} masodperc alatt" } },

            { "act.viaaumid",   new[] { "Started via AUMID (pid {0}).",                             "Elinditva AUMID-del (pid {0})." } },
            { "act.hr",         new[] { "ActivateApplication hr=0x{0}, trying the shell path.",      "ActivateApplication hr=0x{0}, probalom a shell utat." } },
            { "act.exception",  new[] { "ActivateApplication error: {0}, trying the shell path.",    "ActivateApplication hiba: {0}, probalom a shell utat." } },
            { "act.viashell",   new[] { "Started via shell:AppsFolder.",                             "Elinditva shell:AppsFolder-rel." } },
            { "act.shellfail",  new[] { "The shell path failed too: {0}",                            "A shell ut is elbukott: {0}" } },
            { "act.nopackage",  new[] { "No MSIX package - looking for a classic install.",          "Nincs MSIX csomag, a klasszikus telepitest keresem." } },
            { "act.legacynone", new[] { "No classic install either: {0}",                            "Klasszikus telepites sem talalhato: {0}" } },
            { "act.legacyrun",  new[] { "Started: {0}",                                              "Elinditva: {0}" } },
            { "act.legacyfail", new[] { "Did not start: {0} ({1})",                                  "Nem indult: {0} ({1})" } },
            { "act.noexe",      new[] { "No runnable claude.exe found.",                             "Nem talaltam futtathato claude.exe-t." } },
            { "act.noaumid",    new[] { "No AUMID - cannot activate the packaged app.",              "Nincs AUMID, a csomagolt appot nem tudom inditani." } },
            { "act.launchok",   new[] { "Launch OK.",                                                "Inditas rendben." } },
            { "act.launchfail", new[] { "Launch failed.",                                            "Az inditas nem sikerult." } },

            { "dep.header",     new[] { "-- package dependencies --",                                "-- csomag fuggosegek --" } },
            { "dep.item",       new[] { "  {0}",                                                     "  {0}" } },
            { "dep.allok",      new[] { "  all dependencies report Ok",                              "  minden fuggoseg Ok" } },
            { "dep.repairing",  new[] { "  repairing {0} dependency/dependencies",                    "  {0} fuggoseg javitasa" } },
            { "dep.none",       new[] { "  could not read the dependency list",                      "  a fuggosegi listat nem tudtam kiolvasni" } },
            { "dep.nodeps",     new[] { "  the package declares no dependencies - not the cause here",
                                        "  a csomagnak nincs fuggosege - itt nem ez az ok" } },

            { "ev.policy",      new[] { "== SIGNATURE AND SIDELOADING POLICY ==",                     "== ALAIRAS ES OLDALTELEPITESI HAZIREND ==" } },
            { "ev.sigkind",     new[] { "  signature kind:      {0}",                                 "  alairas tipusa:      {0}" } },
            { "ev.allowtrusted",new[] { "  AllowAllTrustedApps: {0}",                                 "  AllowAllTrustedApps: {0}" } },
            { "ev.allowdev",    new[] { "  AllowDevelopmentWithoutDevLicense: {0}",                   "  AllowDevelopmentWithoutDevLicense: {0}" } },
            { "ev.notset",      new[] { "(not set)",                                                  "(nincs beallitva)" } },
            { "adv.sideload",   new[] { "  The package is Developer-signed and sideloading is disabled by policy.",
                                        "  A csomag Developer-alairasu, es az oldaltelepites hazirenddel tiltva van." } },
            { "adv.sideload2",  new[] { "  That alone prevents activation. An administrator has to allow trusted apps,",
                                        "  Mar ez megakadalyozza az inditast. A rendszergazdanak engedelyeznie kell a" } },
            { "adv.sideload3",  new[] { "  or Claude has to be installed from the Store build instead.",
                                        "  megbizhato appokat, vagy a Claude Store-os valtozatat kell telepiteni." } },

            { "reg.header",     new[] { "-- re-registering the package --",                           "-- csomag ujraregisztralasa --" } },
            { "reg.identity",   new[] { "  identity now: {0} | AUMID source: {1}",                    "  azonossag most: {0} | AUMID forrasa: {1}" } },

            { "hold.header",    new[] { "-- what still holds the files --",                            "-- mi fogja meg mindig a fajlokat --" } },
            { "hold.none",      new[] { "  (nobody visible)",                                          "  (senki lathato)" } },
            { "hold.skip",      new[] { "  [{0}] {1} - left alone: {2}",                                "  [{0}] {1} - nem bantom: {2}" } },
            { "hold.killed",    new[] { "  [{0}] {1} stopped",                                          "  [{0}] {1} leallitva" } },
            { "hold.count",     new[] { "  lock holders released: {0}",                                 "  elengedett zarolok: {0}" } },
            { "hold.ourservice",new[] { "our own service - stopping it instead of killing it",           "a sajat szolgaltatasunk - leallitom, nem kilovom" } },
            { "safe.service",   new[] { "a Windows service",                                            "windows szolgaltatas" } },
            { "safe.session",   new[] { "another session (session {0})",                                 "masik munkamenet (session {0})" } },
            { "safe.security",  new[] { "security software",                                            "biztonsagi szoftver" } },
            { "safe.system",    new[] { "a system process",                                             "rendszerfolyamat" } },
            { "safe.nopath",    new[] { "its path cannot be read",                                      "az utvonala nem olvashato" } },

            { "res.done",       new[] { "DONE - Claude is running again.",                              "KESZ - a Claude ujraindult." } },
            { "res.failed",     new[] { "FAILED: Claude did not start after any repair step.",           "HIBA: a Claude egyik javitasi lepes utan sem indult el." } },

            { "ev.identity",    new[] { "== PACKAGE IDENTITY ==",                                        "== CSOMAG AZONOSSAG ==" } },
            { "ev.source",      new[] { "  source:              {0}",                                   "  honnan:              {0}" } },
            { "ev.direxists",   new[] { "  package dir exists:  {0}",                                   "  csomagmappa letezik: {0}" } },
            { "ev.registered",  new[] { "  package registered:  {0}",                                   "  csomag regisztralt:  {0}" } },
            { "ev.aumidsrc",    new[] { "  AUMID source:        {0}",                                   "  AUMID forrasa:       {0}" } },
            { "ev.allaumids",   new[] { "  all AUMIDs:          {0}",                                   "  osszes AUMID:        {0}" } },
            { "ev.packages",    new[] { "== REGISTERED CLAUDE PACKAGES ==",                              "== REGISZTRALT CLAUDE CSOMAGOK ==" } },
            { "ev.nopackages",  new[] { "  (none - the package is not registered for this user)",        "  (nincs - a csomag nincs regisztralva ehhez a felhasznalohoz)" } },
            { "ev.holders",     new[] { "== WHO HOLDS THE PACKAGE FILES (Restart Manager) ==",           "== KI FOGJA A CSOMAG FAJLJAIT (Restart Manager) ==" } },
            { "ev.probed",      new[] { "  files probed: {0}",                                           "  vizsgalt fajlok: {0}" } },
            { "ev.nobody",      new[] { "  (no visible process holds them - another user or a kernel-level lock)",
                                        "  (egyetlen lathato folyamat sem fogja - mas felhasznalo vagy kernel szintu zar)" } },
            { "ev.deploylog",   new[] { "== LAST PACKAGE DEPLOYMENT ERRORS (Get-AppxLog) ==",            "== UTOLSO CSOMAGTELEPITESI HIBAK (Get-AppxLog) ==" } },
            { "ev.nolog",       new[] { "  (no deployment errors recorded)",                             "  (nincs rogzitett telepitesi hiba)" } },
            { "ev.session",     new[] { "== SESSION ==",                                                 "== MUNKAMENET ==" } },
            { "ev.ownsession",  new[] { "  own session: {0}",                                            "  sajat session: {0}" } },
            { "ev.admin",       new[] { "  administrator: {0}",                                          "  rendszergazda: {0}" } },
            { "ev.dryrun",      new[] { "DIAGNOSTIC MODE - nothing was changed.",                        "DIAGNOSZTIKA MOD - semmi nem lett modositva." } },
            { "ev.allprocs",    new[] { "-- all claude/node/cowork processes --",                         "-- osszes claude/node/cowork folyamat --" } },
            { "ev.target",      new[] { "TARGET",                                                        "CEL" } },
            { "ev.skipped",     new[] { "skipped",                                                       "kihagyva" } },

            { "adv.header",     new[] { "== WHAT TO DO ==",                                              "== MI A TEENDO ==" } },
            { "adv.notreg",     new[] { "  The package is not validly registered for this user.",         "  A csomag nincs ervenyesen regisztralva ehhez a felhasznalohoz." } },
            { "adv.reinstall",  new[] { "  Reinstall Claude Desktop from claude.ai/download.",            "  Telepitsd ujra a Claude Desktopot a claude.ai/download oldalrol." } },
            { "adv.reset",      new[] { "  Last resort, LOSES APP DATA: Get-AppxPackage -Name Claude* | Reset-AppxPackage",
                                        "  Vegso esetben, ADATVESZTESSEL: Get-AppxPackage -Name Claude* | Reset-AppxPackage" } },
            { "adv.blocked",    new[] { "  The package is held by something this tool will not touch:",   "  A csomagot olyan program fogja, amihez nem nyulok:" } },
            { "adv.blockitem",  new[] { "    - {0} ({1})",                                               "    - {0} ({1})" } },
            { "adv.exclusion",  new[] { "  If that is security software, an administrator can add an exclusion",
                                        "  Ha ez biztonsagi szoftver, a rendszergazda kivetelt tud felvenni a" } },
            { "adv.exclusion2", new[] { "  for the WindowsApps\\Claude_* directory. A reboot clears it meanwhile.",
                                        "  WindowsApps\\Claude_* mappara. Addig a gep ujrainditasa segit." } },
            { "adv.deps",       new[] { "  These package dependencies are not healthy - that alone stops activation:",
                                        "  Ezek a csomagfuggosegek nincsenek rendben - mar ez is megakadalyozza az inditast:" } },
            { "adv.nothing",    new[] { "  No blocking process found, yet it still will not start. The Windows",
                                        "  Nem talaltam blokkolo folyamatot, megsem indul. A Windows" } },
            { "adv.nothing2",   new[] { "  package deployment is most likely in a suspended state; a reboot clears",
                                        "  csomagtelepitoje valoszinuleg felfuggesztett allapotban van; a gep" } },
            { "adv.nothing3",   new[] { "  that. If it still fails after a reboot, reinstall.",
                                        "  ujrainditasa ezt tisztitja. Ha utana sem megy, telepitsd ujra." } },
            { "adv.log",        new[] { "  The deployment log above usually names the real reason.",
                                        "  A fenti telepitesi naplo altalaban megnevezi a valodi okot." } },

            { "help.body",      new[] {
                "Claude repair\n\n" +
                "Stops the stuck Claude processes and the CoworkVMService service,\n" +
                "clears stale lock files, then restarts Claude Desktop.\n" +
                "Runs silently by default.\n\n" +
                "  (no switch)   repair; only speaks up on failure\n" +
                "  --diagnose    report findings only, change nothing\n" +
                "  --verbose     repair, then show a summary\n" +
                "  --launch      only start Claude Desktop\n" +
                "  --log <file>  write the log to a file instead of a dialog\n" +
                "  --lang en|hu  force the output language\n" +
                "  --help        this window",

                "Claude javito\n\n" +
                "Leallitja a beragadt Claude folyamatokat es a CoworkVMService\n" +
                "szolgaltatast, kitakaritja a lock fajlokat, majd ujrainditja a\n" +
                "Claude Desktopot. Alapesetben csendben fut.\n\n" +
                "  (nincs kapcsolo)  javitas, csak hiba eseten szol\n" +
                "  --diagnose        csak kiirja mit talalt, nem modosit\n" +
                "  --verbose         javitas + osszegzes a vegen\n" +
                "  --launch          csak elinditja a Claude Desktopot\n" +
                "  --log <fajl>      ablak helyett fajlba irja a naplot\n" +
                "  --lang en|hu      kenyszeriti a kimenet nyelvet\n" +
                "  --help            ez az ablak" } },
        };
    }
}
