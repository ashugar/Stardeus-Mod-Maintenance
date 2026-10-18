# Run in its own pwsh process. This hosts the installed Mono DLL, not Unity/the game.
param(
    [Parameter(Mandatory=$true)][string]$AssemblyPath,
    [string]$Options = '--optimize=-float32,-inline',
    [string]$GameRoot = $env:STARDEUS_GAME_ROOT,
    [string]$ManagedPath = $env:STARDEUS_MANAGED_PATH,
    [string]$MonoRuntimePath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../build/LocalPaths.ps1')
$ManagedPath = Resolve-StardeusManagedPath -ManagedPath $ManagedPath -GameRoot $GameRoot
if (-not $MonoRuntimePath) {
    $MonoRuntimePath = Join-Path (Resolve-StardeusGameRoot -GameRoot $GameRoot) 'MonoBleedingEdge/EmbedRuntime/mono-2.0-bdwgc.dll'
}
if (-not (Test-Path -LiteralPath $MonoRuntimePath -PathType Leaf)) { throw 'Provide the installed Mono runtime with -MonoRuntimePath or configure STARDEUS_GAME_ROOT.' }
if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) { throw 'The isolated probe assembly was not found.' }
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class InvestorQuoteMonoHost {
    const string Dll = "stardeus-isolated-quote-mono";
    static IntPtr library;
    public static void Configure(string path) {
        library = NativeLibrary.Load(path);
        NativeLibrary.SetDllImportResolver(typeof(InvestorQuoteMonoHost).Assembly,
            (name, assembly, searchPath) => name == Dll ? library : IntPtr.Zero);
    }
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] public static extern void mono_set_assemblies_path(string path);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] public static extern void mono_jit_parse_options(int argc, [In, MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.LPStr)] string[] argv);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr mono_jit_init_version(string name, string version);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] public static extern IntPtr mono_domain_assembly_open(IntPtr domain, string name);
    [DllImport(Dll, CallingConvention=CallingConvention.Cdecl)] public static extern int mono_jit_exec(IntPtr domain, IntPtr assembly, int argc, [In, MarshalAs(UnmanagedType.LPArray, ArraySubType=UnmanagedType.LPStr)] string[] argv);
}
'@
[InvestorQuoteMonoHost]::Configure([IO.Path]::GetFullPath($MonoRuntimePath))
[InvestorQuoteMonoHost]::mono_set_assemblies_path($ManagedPath)
if ($Options) { [InvestorQuoteMonoHost]::mono_jit_parse_options(1, [string[]]@($Options)) }
$domain = [InvestorQuoteMonoHost]::mono_jit_init_version('IsolatedInvestorQuoteProbe', 'v4.0.30319')
if ($domain -eq [IntPtr]::Zero) { throw 'Mono initialization failed' }
$assembly = [InvestorQuoteMonoHost]::mono_domain_assembly_open($domain, $AssemblyPath)
if ($assembly -eq [IntPtr]::Zero) { throw 'Probe assembly load failed' }
Write-Output ('MONO_OPTIONS=' + $Options)
$result = [InvestorQuoteMonoHost]::mono_jit_exec($domain, $assembly, 1, [string[]]@($AssemblyPath))
exit $result
