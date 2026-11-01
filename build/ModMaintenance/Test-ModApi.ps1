#requires -Version 7.2
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProfilePath,[switch]$ReportOnly)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'MetadataRunner.ps1')
Invoke-MetadataDiagnostic -Mode api -ProfilePath $ProfilePath -ReportOnly:$ReportOnly
