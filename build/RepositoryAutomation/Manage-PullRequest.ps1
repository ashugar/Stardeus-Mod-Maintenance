#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Update','EnableAutoMerge')][string]$Action,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$PullRequest,
    [ValidatePattern('^[0-9a-fA-F]{40,64}$')][string]$ReviewedCommit
)
$ErrorActionPreference = 'Stop'
$repository = 'ashugar/Stardeus-Mod-Maintenance'
$credential = @{}
$headers = @{}
$previousTerminalPrompt = $env:GIT_TERMINAL_PROMPT
$previousGcmInteractive = $env:GCM_INTERACTIVE
function Git-Value([string[]]$Arguments) {
    $value = & git --no-replace-objects @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'git-operation-failed' }
    return ($value -join "`n").Trim()
}
function GitHub([string]$Path,[string]$Method='GET',$Body=$null) {
    if ($Path -ne '/graphql' -and -not $Path.StartsWith('/repos/' + $repository + '/', [StringComparison]::Ordinal)) { throw 'api-scope-refused' }
    $request = @{Uri=('https://api.github.com' + $Path); Method=$Method; Headers=$headers; MaximumRedirection=0}
    if ($null -ne $Body) { $request.ContentType='application/json; charset=utf-8'; $request.Body=[Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Depth 12 -Compress)) }
    try { $result=Invoke-RestMethod @request } catch { throw 'github-request-failed' }
    if ($result.errors) { throw 'github-graphql-refused' }
    return $result
}
try {
    $repo = Git-Value @('rev-parse','--show-toplevel')
    if ($repo.Replace('\','/').Contains('/AppData/LocalLow/Kodo Linija/Stardeus/Mods',[StringComparison]::OrdinalIgnoreCase)) { throw 'live-mods-root-refused' }
    $origin = Git-Value @('-C',$repo,'remote','get-url','origin')
    if ($origin -notmatch '^(https://github\.com/|git@github\.com:)ashugar/Stardeus-Mod-Maintenance(?:\.git)?/?$') { throw 'unexpected-repository' }
    $pushUrls = @(& git -C $repo remote get-url --push --all origin 2>$null)
    if ($LASTEXITCODE -ne 0 -or $pushUrls.Count -ne 1 -or $pushUrls[0] -notmatch '^(https://github\.com/|git@github\.com:)ashugar/Stardeus-Mod-Maintenance(?:\.git)?/?$') { throw 'unexpected-push-destination' }
    $branch = Git-Value @('-C',$repo,'symbolic-ref','--short','HEAD')
    $head = Git-Value @('-C',$repo,'rev-parse','HEAD')
    if ($Action -eq 'EnableAutoMerge' -and (-not $ReviewedCommit -or $head -cne $ReviewedCommit.ToLowerInvariant())) { throw 'reviewed-head-required' }
    if (Git-Value @('-C',$repo,'status','--porcelain','--untracked-files=all')) { throw 'clean-checkout-required' }
    $gitDir = Git-Value @('-C',$repo,'rev-parse','--absolute-git-dir')
    $install = Join-Path $gitDir 'repository-automation'
    $armedPath = Join-Path $install 'armed.json'
    $env:GIT_TERMINAL_PROMPT='0'
    $env:GCM_INTERACTIVE='Never'
    $lines = @('protocol=https','host=github.com','username=ashugar','') | git -c credential.interactive=never credential fill 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'existing-github-sign-in-required' }
    foreach ($line in $lines) { if ($line -match '^([^=]+)=(.*)$') { $credential[$matches[1]]=$matches[2] } }
    if (-not $credential['password']) { throw 'existing-github-sign-in-required' }
    $headers=@{Authorization=('Bearer ' + $credential['password']);Accept='application/vnd.github+json';'User-Agent'='Stardeus-Reviewed-PR-Workflow'}
    $pr = GitHub ('/repos/' + $repository + '/pulls/' + $PullRequest)
    if ($pr.state -cne 'open' -or $pr.draft -or $pr.base.ref -cne 'main' -or $pr.base.repo.full_name -cne $repository -or $pr.head.repo.full_name -cne $repository -or $pr.head.ref -cne $branch) { throw 'unexpected-pull-request-state' }
    $id=$pr.node_id
    if ($Action -eq 'Update') {
        if ($null -ne $pr.auto_merge) {
            [void](GitHub '/graphql' 'POST' @{query='mutation($id:ID!){disablePullRequestAutoMerge(input:{pullRequestId:$id}){pullRequest{autoMergeRequest{enabledAt}}}}';variables=@{id=$id}})
        }
        $confirmed=GitHub '/graphql' 'POST' @{query='query($id:ID!){node(id:$id){... on PullRequest{state autoMergeRequest{enabledAt}}}}';variables=@{id=$id}}
        if ($confirmed.data.node.state -cne 'OPEN' -or $null -ne $confirmed.data.node.autoMergeRequest) { throw 'auto-merge-not-confirmed-off' }
        if (Test-Path -LiteralPath $armedPath) { Remove-Item -LiteralPath $armedPath }
        & git -C $repo push origin ('HEAD:refs/heads/' + $branch)
        if ($LASTEXITCODE -ne 0) { throw 'push-failed' }
        Write-Output 'Updated the PR with auto-merge disabled. Review the new head before enabling it again.'
    } else {
        if ($pr.head.sha -cne $head) { throw 'remote-head-does-not-match-review' }
        $manifest = Get-Content -LiteralPath (Join-Path $install 'installed.json') -Raw | ConvertFrom-Json
        $fingerprint = @('build/RepositoryPrivacy','build/RepositoryAutomation') | ForEach-Object { Git-Value @('-C',$repo,'rev-parse',($head + ':' + $_)) }
        if (($fingerprint -join ':') -cne $manifest.Fingerprint) { throw 'installed-guard-outdated' }
        & dotnet $manifest.Checker scan-history --repo $repo --ref $head
        if ($LASTEXITCODE -ne 0) { throw 'privacy-check-failed' }
        # This local marker also blocks accidental raw pushes from this checkout while armed.
        [IO.File]::WriteAllText($armedPath,(@{PullRequest=$PullRequest;Branch=$branch;Head=$head} | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
        function Merge-ReviewedHead {
            $merged=GitHub '/graphql' 'POST' @{query='mutation($id:ID!,$head:GitObjectID!){mergePullRequest(input:{pullRequestId:$id,expectedHeadOid:$head,mergeMethod:SQUASH}){pullRequest{merged headRefOid mergeCommit{oid}}}}';variables=@{id=$id;head=$head}}
            $result=$merged.data.mergePullRequest.pullRequest
            if (-not $result.merged -or $result.headRefOid -cne $head) { throw 'reviewed-merge-not-confirmed' }
            Write-Output ('Merged reviewed PR #' + $PullRequest + ' through normal GitHub protection checks at ' + $result.mergeCommit.oid + '.')
        }
        $ready=GitHub ('/repos/' + $repository + '/pulls/' + $PullRequest)
        if ($ready.head.sha -cne $head -or $ready.state -cne 'open' -or $ready.base.ref -cne 'main' -or $ready.head.ref -cne $branch) { throw 'reviewed-head-changed' }
        if ($ready.mergeable_state -ceq 'clean') {
            # Native auto-merge can refuse an already mergeable PR; the normal mutation still enforces rules.
            Merge-ReviewedHead
        } else {
            try {
                $response=GitHub '/graphql' 'POST' @{query='mutation($id:ID!,$head:GitObjectID!){enablePullRequestAutoMerge(input:{pullRequestId:$id,expectedHeadOid:$head,mergeMethod:SQUASH}){pullRequest{headRefOid autoMergeRequest{mergeMethod}}}}';variables=@{id=$id;head=$head}}
                if ($response.data.enablePullRequestAutoMerge.pullRequest.headRefOid -cne $head) { throw 'reviewed-head-changed' }
                Write-Output ('Enabled reviewed auto-merge for PR #' + $PullRequest + ' at ' + $head + '. Required checks still govern merging.')
            } catch {
                # Checks may complete between the readiness read and activation. Recheck before a guarded merge.
                $latest=GitHub ('/repos/' + $repository + '/pulls/' + $PullRequest)
                if ($latest.head.sha -cne $head -or $latest.base.ref -cne 'main' -or $latest.head.ref -cne $branch) { throw 'reviewed-head-changed' }
                if ($latest.merged) { Write-Output ('Observed reviewed PR #' + $PullRequest + ' merged.') }
                elseif ($latest.state -ceq 'open' -and $latest.mergeable_state -ceq 'clean') { Merge-ReviewedHead }
                else { throw 'auto-merge-activation-failed' }
            }
        }
    }
} catch { [Console]::Error.WriteLine('PR workflow refused: ' + $(if ($_.Exception.Message -match '^[a-z0-9-]+$') { $_.Exception.Message } else { 'operation-failed' })); exit 2
} finally { $headers.Clear(); $credential.Clear(); $lines=$null; $env:GIT_TERMINAL_PROMPT=$previousTerminalPrompt; $env:GCM_INTERACTIVE=$previousGcmInteractive }