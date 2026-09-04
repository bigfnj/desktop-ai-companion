using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// Product identity and version attributes are generated from the repository-root
// ProductVersion.props by DesktopAICompanion_Portable.csproj. Keep only assembly behavior
// attributes in this hand-authored file so release metadata has one source of truth.

[assembly: ComVisible(false)]
[assembly: Guid("f90a0241-18eb-4728-9e35-b8f705485cb6")]

// GenerateAssemblyInfo is off (the product-info target owns version metadata), which also
// suppresses the SDK's auto-generated platform attribute for the net*-windows TFM. Restore it
// so the CA1416 platform-compatibility analyzer knows this whole app is Windows-only (it is:
// x64 WinForms + user32/shcore/registry P/Invoke) instead of flagging every Win32 call site.
[assembly: SupportedOSPlatform("windows7.0")]
