using System.Runtime.Versioning;

// The shared sources this tool recompiles use System.Drawing to measure the embedded sprite sheet, which
// the platform-compatibility analyzer (CA1416) only permits from Windows-annotated call sites. The app is
// implicitly annotated by being a WinExe; a console tool is not, so it says so here. CoreTests solves the
// same problem by generating this attribute from MSBuild -- one line of source is the same thing, visible.
[assembly: SupportedOSPlatform("windows")]
