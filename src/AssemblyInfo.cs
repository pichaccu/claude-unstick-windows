// Version resource for the executable.
//
// This is not cosmetic. An unsigned, few-tens-of-kilobytes .NET binary with no
// company, no description and FileVersion 0.0.0.0 that terminates processes and
// stops a service is close to a textbook machine-learning false positive for
// Windows Defender. Real metadata plus an Authenticode signature is what tells
// the heuristics this is shipped software rather than something dropped on the
// machine, so keep these attributes populated and in step with the release tag.

using System.Reflection;
using System.Runtime.InteropServices;

[assembly: AssemblyTitle("Claude Unstick for Windows")]
[assembly: AssemblyDescription("Recovers Claude Desktop after a failed MSIX update, without a reboot or admin rights.")]
[assembly: AssemblyCompany("pichaccu")]
[assembly: AssemblyProduct("Claude Unstick for Windows")]
[assembly: AssemblyCopyright("Copyright (c) 2026 pichaccu - MIT License")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]

[assembly: ComVisible(false)]

[assembly: AssemblyVersion("2.1.0.0")]
[assembly: AssemblyFileVersion("2.1.0.0")]
[assembly: AssemblyInformationalVersion("2.1.0")]
