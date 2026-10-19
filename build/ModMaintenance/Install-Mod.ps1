[CmdletBinding(DefaultParameterSetName = 'Prepare')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Prepare')][switch]$Prepare,
    [Parameter(Mandatory, ParameterSetName = 'Apply')][switch]$Apply,
    [Parameter(Mandatory, ParameterSetName = 'Recover')][switch]$Recover,
    [Parameter(ParameterSetName = 'Prepare')][Parameter(ParameterSetName = 'Apply')][string]$RepoRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))),
    [Parameter(Mandatory, ParameterSetName = 'Prepare')][string]$Mod,
    [Parameter(Mandatory, ParameterSetName = 'Prepare')][Parameter(Mandatory, ParameterSetName = 'Apply')][string]$ReviewedCommit,
    [Parameter(Mandatory, ParameterSetName = 'Prepare')][string]$ModsRoot,
    [Parameter(Mandatory, ParameterSetName = 'Prepare')][string]$RollbackRoot,
    [Parameter(Mandatory, ParameterSetName = 'Prepare')][Parameter(Mandatory, ParameterSetName = 'Apply')][string]$PlanPath,
    [Parameter(Mandatory, ParameterSetName = 'Apply')][Parameter(Mandatory, ParameterSetName = 'Recover')][string]$PlanSha256,
    [Parameter(Mandatory, ParameterSetName = 'Recover')][string]$JournalPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'Install-Internal.ps1')

try {
    if ($Recover) {
        Assert-InstallRecoveryTooling -EntryPoint $PSCommandPath
        Invoke-ModInstallRecovery -JournalPath $JournalPath -PlanSha256 $PlanSha256
    } elseif ($Apply) {
        $plan = Read-InstallJson (Resolve-InstallPath $PlanPath)
        if ($plan.ReviewedCommit -cne $ReviewedCommit) { throw 'install-reviewed-commit-mismatch' }
        $snapshot = Get-ModReleaseSnapshot -RepoRoot $RepoRoot -ReviewedCommit $ReviewedCommit -Mod $plan.Folder
        Assert-InstallReviewedTooling -RepoRoot $RepoRoot -ReviewedCommit $ReviewedCommit -EntryPoint $PSCommandPath
        Invoke-ModInstallApply -Snapshot $snapshot -RepoRoot $RepoRoot -PlanPath $PlanPath -PlanSha256 $PlanSha256
    } else {
        Assert-InstallGameClosed
        $snapshot = Get-ModReleaseSnapshot -RepoRoot $RepoRoot -ReviewedCommit $ReviewedCommit -Mod $Mod
        Assert-InstallReviewedTooling -RepoRoot $RepoRoot -ReviewedCommit $ReviewedCommit -EntryPoint $PSCommandPath
        $modRoot = Join-Path (Resolve-InstallPath $ModsRoot) $snapshot.Folder
        $lock = $null
        try {
            $lock = Enter-InstallLock $modRoot
            $plan = New-ModInstallPlan -Snapshot $snapshot -RepoRoot $RepoRoot -ModsRoot $ModsRoot -RollbackRoot $RollbackRoot
            $output = Resolve-InstallPath $PlanPath
            Assert-InstallNoReparse $output
            if ((Test-InstallWithin $plan.RepoRoot $output) -or (Test-InstallWithin $plan.ModsRoot $output) -or -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($output))) { throw 'install-plan-private-existing-parent-required' }
            Assert-InstallPrivatePath $plan.RepoRoot $plan.ModsRoot $output
            Assert-InstallGameClosed
            $bytes = Get-InstallJsonBytes $plan
            Write-InstallNewFile $output $bytes
            [pscustomobject]@{ Status = 'prepared-review-plan-before-applying'; PlanPath = $output; PlanSha256 = (Get-InstallSha256 $bytes); Files = $plan.Files }
        } finally { Exit-InstallLock $lock }
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
