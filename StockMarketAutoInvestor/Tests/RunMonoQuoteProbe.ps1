# Run in its own pwsh process. This hosts the installed Mono DLL, not Unity/the game.
param([Parameter(Mandatory=$true)][string]$AssemblyPath,
      [string]$Options = '--optimize=-float32,-inline')
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class InvestorQuoteMonoHost {
    const string Dll = @"D:\Program Files (x86)\Steam\steamapps\common\Stardeus\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll";
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] public static extern void mono_set_assemblies_path(string path);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] public static extern void mono_jit_parse_options(int argc, [In, MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.LPStr)] string[] argv);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr mono_jit_init_version(string name, string version);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr mono_domain_assembly_open(IntPtr domain, string name);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] public static extern int mono_jit_exec(IntPtr domain, IntPtr assembly, int argc, [In, MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.LPStr)] string[] argv);
}
'@
[InvestorQuoteMonoHost]::mono_set_assemblies_path('D:\Program Files (x86)\Steam\steamapps\common\Stardeus\Stardeus_Data\Managed')
if ($Options) { [InvestorQuoteMonoHost]::mono_jit_parse_options(1, [string[]]@($Options)) }
$domain = [InvestorQuoteMonoHost]::mono_jit_init_version('IsolatedInvestorQuoteProbe', 'v4.0.30319')
if ($domain -eq [IntPtr]::Zero) { throw 'Mono initialization failed' }
$assembly = [InvestorQuoteMonoHost]::mono_domain_assembly_open($domain, $AssemblyPath)
if ($assembly -eq [IntPtr]::Zero) { throw 'Probe assembly load failed' }
Write-Output ('MONO_OPTIONS=' + $Options)
$result = [InvestorQuoteMonoHost]::mono_jit_exec($domain, $assembly, 1, [string[]]@($AssemblyPath))
exit $result
