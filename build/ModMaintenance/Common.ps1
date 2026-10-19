#requires -Version 7.0
# Shared release operations read committed objects and never install or run a mod.
Set-StrictMode -Version Latest
$script:ModCommonSourcePath = [IO.Path]::GetFullPath($PSCommandPath)
$script:ModUtf8 = [Text.UTF8Encoding]::new($false, $true)
if (-not (Get-Variable -Name ModPrivacyCache -Scope Script -ErrorAction SilentlyContinue)) { $script:ModPrivacyCache = @{} }

function Get-ModSha256 {
    param([Parameter(Mandatory)][AllowEmptyCollection()][byte[]]$Bytes)
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

function Invoke-ModProcess {
    param([Parameter(Mandatory)][string]$Program, [string[]]$Arguments, [string]$WorkingDirectory)
    $start = [Diagnostics.ProcessStartInfo]::new($Program)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($WorkingDirectory) { $start.WorkingDirectory = $WorkingDirectory }
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment['GIT_NO_LAZY_FETCH'] = '1'
    $start.Environment['GIT_OPTIONAL_LOCKS'] = '0'
    $process = [Diagnostics.Process]::Start($start)
    try {
        $errorRead = $process.StandardError.ReadToEndAsync()
        $memory = [IO.MemoryStream]::new()
        try {
            $process.StandardOutput.BaseStream.CopyTo($memory)
            $process.WaitForExit()
            $null = $errorRead.GetAwaiter().GetResult()
            if ($process.ExitCode -ne 0) { throw 'release-child-operation-failed' }
            return ,$memory.ToArray()
        } finally { $memory.Dispose() }
    } finally { $process.Dispose() }
}

function Invoke-ModGitBytes {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string[]]$Arguments)
    return ,(Invoke-ModProcess -Program 'git' -Arguments (@('--no-replace-objects', '-C', $RepoRoot) + $Arguments))
}

function Get-ModGitText {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string[]]$Arguments)
    $script:ModUtf8.GetString((Invoke-ModGitBytes -RepoRoot $RepoRoot -Arguments $Arguments))
}

function Assert-ModSafeRelativePath {
    param([Parameter(Mandatory)][string]$Path)
    if (-not $Path -or $Path.Contains('\') -or $Path -match '[\x00-\x1f\x7f:*?"<>|]' -or $Path.StartsWith('/')) { throw 'unsafe-release-path' }
    foreach ($part in $Path.Split('/')) {
        if (-not $part -or $part -in @('.', '..') -or $part -match '[. ]$' -or $part -ieq '.git' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|CONIN\$|CONOUT\$|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)') { throw 'unsafe-release-path' }
    }
}

function Assert-ModNoReparsePath {
    param([Parameter(Mandatory)][string]$Path)
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if ([IO.File]::Exists($cursor) -or [IO.Directory]::Exists($cursor)) {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'release-reparse-point-refused' }
        }
        # GetAttributes also recognizes dangling links which Exists deliberately ignores.
        try { $attributes = [IO.File]::GetAttributes($cursor) } catch [IO.FileNotFoundException] { $attributes = $null } catch [IO.DirectoryNotFoundException] { $attributes = $null }
        if ($null -ne $attributes -and ($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'release-reparse-point-refused' }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -ceq $cursor) { break }
        $cursor = $parent
    }
}

function Test-ModPathOverlap {
    param([Parameter(Mandatory)][string]$First, [Parameter(Mandatory)][string]$Second)
    $a = [IO.Path]::GetFullPath($First).TrimEnd([char[]]@('/', '\'))
    $b = [IO.Path]::GetFullPath($Second).TrimEnd([char[]]@('/', '\'))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    [string]::Equals($a, $b, $comparison) -or $a.StartsWith($b + [IO.Path]::DirectorySeparatorChar, $comparison) -or $b.StartsWith($a + [IO.Path]::DirectorySeparatorChar, $comparison)
}

function Assert-ModExternalPath {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'absolute-private-path-required' }
    Assert-ModNoReparsePath $Path
    if (Test-ModPathOverlap $RepoRoot $Path) { throw 'private-source-overlap-refused' }
    $playerProfile = [Environment]::GetFolderPath('UserProfile')
    if (-not $playerProfile) { throw 'local-profile-resolution-required' }
    $forbidden = @((Join-Path $playerProfile 'AppData/LocalLow/Kodo Linija/Stardeus'))
    foreach ($localPath in @($env:STARDEUS_USER_ROOT, $env:STARDEUS_GAME_ROOT, $env:STARDEUS_MANAGED_PATH)) {
        if ($localPath) {
            if (-not [IO.Path]::IsPathFullyQualified($localPath)) { throw 'absolute-local-override-required' }
            $forbidden += [IO.Path]::GetFullPath($localPath)
        }
    }
    foreach ($forbiddenRoot in $forbidden) { if (Test-ModPathOverlap $Path $forbiddenRoot) { throw 'private-live-or-game-overlap-refused' } }
    if ($Path.Replace('\', '/') -match '(?i)(?:^|/)(?:steamapps|Stardeus_Data)(?:/|$)') { throw 'private-steam-or-game-directory-refused' }
}

function New-ModPrivateDirectory {
    param([Parameter(Mandatory)][string]$RepoRoot)
    $path = Join-Path ([IO.Path]::GetTempPath()) ('StardeusModMaintenance-' + [Guid]::NewGuid().ToString('N'))
    Assert-ModExternalPath $RepoRoot $path
    if ([IO.File]::Exists($path) -or [IO.Directory]::Exists($path)) { throw 'new-private-directory-required' }
    $null = [IO.Directory]::CreateDirectory($path)
    $path
}

function Assert-ModReviewedRepository {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$ReviewedCommit)
    if ($ReviewedCommit -cnotmatch '^[0-9a-f]{40}(?:[0-9a-f]{24})?$') { throw 'full-reviewed-commit-required' }
    $root = [IO.Path]::GetFullPath($RepoRoot).TrimEnd([char[]]@('/', '\'))
    Assert-ModNoReparsePath $root
    $actual = (Get-ModGitText $root @('rev-parse', '--show-toplevel')).Trim()
    if (-not [string]::Equals($root, [IO.Path]::GetFullPath($actual).TrimEnd([char[]]@('/', '\')), $(if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }))) { throw 'repository-root-required' }
    if ($root.Replace('\', '/').Contains('/AppData/LocalLow/Kodo Linija/Stardeus/Mods', [StringComparison]::OrdinalIgnoreCase)) { throw 'live-source-refused' }
    foreach ($localPath in @($env:STARDEUS_USER_ROOT, $env:STARDEUS_GAME_ROOT, $env:STARDEUS_MANAGED_PATH)) {
        if ($localPath -and (Test-ModPathOverlap $root $localPath)) { throw 'live-or-game-source-refused' }
    }
    $origin = (Get-ModGitText $root @('remote', 'get-url', 'origin')).Trim()
    if ($origin -cnotmatch '^(https://github\.com/|git@github\.com:)ashugar/Stardeus-Mod-Maintenance(?:\.git)?/?$') { throw 'unexpected-release-repository' }
    $head = (Get-ModGitText $root @('rev-parse', '--verify', 'HEAD^{commit}')).Trim()
    if ($head -cne $ReviewedCommit) { throw 'reviewed-head-required' }
    if ((Get-ModGitText $root @('status', '--porcelain=v1', '--untracked-files=no', '--ignore-submodules=none')).Length -ne 0) { throw 'clean-tracked-checkout-required' }
    # Prevent executing edited or untracked tooling while reading a different reviewed policy.
    $common = Join-Path $root 'build/ModMaintenance/Common.ps1'
    $reviewedCommonSha256 = Get-ModSha256 (Invoke-ModGitBytes $root @('show', ($head + ':build/ModMaintenance/Common.ps1')))
    Assert-ModNoReparsePath $script:ModCommonSourcePath
    if (-not [IO.File]::Exists($common) -or -not [IO.File]::Exists($script:ModCommonSourcePath) -or
        (Get-ModSha256 ([IO.File]::ReadAllBytes($common))) -cne $reviewedCommonSha256 -or
        (Get-ModSha256 ([IO.File]::ReadAllBytes($script:ModCommonSourcePath))) -cne $reviewedCommonSha256) { throw 'reviewed-release-tooling-required' }
    $root
}

function Get-ModCommitTree {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$ReviewedCommit)
    $entries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    $caseNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $exactNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($record in (Get-ModGitText $RepoRoot @('ls-tree', '-r', '-z', '--full-tree', $ReviewedCommit)).Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($record -cnotmatch '^([0-9]{6}) (blob|commit) ([0-9a-f]{40}(?:[0-9a-f]{24})?)\t(.+)$') { throw 'unsupported-release-tree' }
        $path = $Matches[4]
        Assert-ModSafeRelativePath $path
        # Also reject directory case aliases (a/file and A/other) on Windows.
        $parts = $path.Split('/')
        for ($i = 1; $i -le $parts.Length; $i++) {
            $name = $parts[0..($i - 1)] -join '/'
            if (-not $caseNames.Add($name)) {
                if (-not $exactNames.Contains($name)) { throw 'release-case-collision' }
            } else { $null = $exactNames.Add($name) }
        }
        $entries.Add($path, [pscustomobject]@{ Mode = $Matches[1]; Type = $Matches[2]; ObjectId = $Matches[3] })
    }
    $entries
}

function Get-ModBlob {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)]$Tree, [Parameter(Mandatory)][string]$Path)
    Assert-ModSafeRelativePath $Path
    if (-not $Tree.ContainsKey($Path) -or $null -eq $Tree[$Path] -or $Tree[$Path].Mode -notin @('100644', '100755') -or $Tree[$Path].Type -cne 'blob') { throw 'release-regular-blob-required' }
    Assert-ModNoReparsePath (Join-Path $RepoRoot $Path)
    return ,(Invoke-ModGitBytes $RepoRoot @('cat-file', 'blob', $Tree[$Path].ObjectId))
}

function ConvertFrom-ModStrictJson {
    param([Parameter(Mandatory)][byte[]]$Bytes)
    $document = [System.Text.Json.JsonDocument]::Parse($script:ModUtf8.GetString($Bytes))
    try {
        function Assert-NoDuplicateJsonProperty($Element) {
            if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
                $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
                foreach ($property in $Element.EnumerateObject()) {
                    if (-not $seen.Add($property.Name)) { throw 'duplicate-release-json-property' }
                    Assert-NoDuplicateJsonProperty $property.Value
                }
            } elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) {
                foreach ($child in $Element.EnumerateArray()) { Assert-NoDuplicateJsonProperty $child }
            }
        }
        Assert-NoDuplicateJsonProperty $document.RootElement
        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'release-json-object-required' }
        return ($script:ModUtf8.GetString($Bytes) | ConvertFrom-Json -AsHashtable -Depth 50)
    } finally { $document.Dispose() }
}

function Assert-ModObjectKeys {
    param([Parameter(Mandatory)]$Value, [Parameter(Mandatory)][string[]]$Keys)
    if ($Value -isnot [Collections.IDictionary] -or $Value.Count -ne $Keys.Count) { throw 'invalid-release-recipe-schema' }
    foreach ($key in $Value.Keys) { if ($key -cnotin $Keys) { throw 'unknown-release-recipe-field' } }
}

function Get-ModNormalizedVersion {
    param([Parameter(Mandatory)][string]$Value, [switch]$Informational)
    $pattern = if ($Informational) { '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:[-+][A-Za-z0-9][A-Za-z0-9.+-]*)?$' } else { '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$' }
    if ($Value -cnotmatch $pattern) { throw 'unsupported-release-version' }
    $parts = @($Matches[1], $Matches[2], $Matches[3], $(if ($Matches.ContainsKey(4)) { $Matches[4] } else { '0' }))
    foreach ($part in $parts) { if ($part.Length -gt 5 -or [int]$part -gt 65535 -or ($part.Length -gt 1 -and $part.StartsWith('0'))) { throw 'unsupported-release-version' } }
    ($parts | ForEach-Object { [int]$_ }) -join '.'
}

function Assert-ModVersionValue {
    param([string]$Expected, [string]$Value, [string]$Surface, [Collections.Generic.List[object]]$Evidence, [switch]$Informational)
    if ((Get-ModNormalizedVersion $Value -Informational:$Informational) -cne $Expected) { throw 'mod-release-version-mismatch' }
    $Evidence.Add([pscustomobject]@{ Surface = $Surface; Status = 'observed'; Value = $Value })
}

function Invoke-ModReleasePrivacy {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$ReviewedCommit, [Parameter(Mandatory)]$Tree)
    $key = $RepoRoot + '|' + $ReviewedCommit
    if ($script:ModPrivacyCache.ContainsKey($key)) { return }
    $privateRoot = New-ModPrivateDirectory $RepoRoot
    $source = Join-Path $privateRoot 'source'
    $null = [IO.Directory]::CreateDirectory($source)
    $projectName = 'build/RepositoryPrivacy/RepositoryPrivacy.csproj'
    foreach ($path in $Tree.Keys) {
        if ($path.StartsWith('build/RepositoryPrivacy/', [StringComparison]::Ordinal) -and [IO.Path]::GetExtension($path) -cin @('.cs', '.csproj')) {
            $relative = $path.Substring('build/RepositoryPrivacy/'.Length)
            if ($relative.Contains('/')) { throw 'unsupported-privacy-source-layout' }
            [IO.File]::WriteAllBytes((Join-Path $source $relative), (Get-ModBlob $RepoRoot $Tree $path))
        }
    }
    if (-not $Tree.ContainsKey($projectName)) { throw 'committed-privacy-checker-required' }
    $config = Join-Path $privateRoot 'NuGet.Config'
    [IO.File]::WriteAllText($config, '<configuration><packageSources><clear /></packageSources></configuration>', $script:ModUtf8)
    $run = Join-Path $privateRoot 'run'
    $arguments = @('build', (Join-Path $source 'RepositoryPrivacy.csproj'), '--configuration', 'Release', '--output', $run,
        ('-p:BaseIntermediateOutputPath=' + (Join-Path $privateRoot 'obj') + '/'), '-p:ImportDirectoryBuildProps=false', '-p:ImportDirectoryBuildTargets=false', '-p:ImportDirectoryPackagesProps=false', '-noAutoResponse', ('-p:RestoreConfigFile=' + $config))
    $null = Invoke-ModProcess -Program 'dotnet' -Arguments $arguments -WorkingDirectory $privateRoot
    $checker = Join-Path $run 'RepositoryPrivacy.dll'
    $null = Invoke-ModProcess -Program 'dotnet' -Arguments @($checker, 'scan-history', '--repo', $RepoRoot, '--ref', $ReviewedCommit) -WorkingDirectory $privateRoot
    # The complete selected source graph includes every payload blob; no generated ZIP exception is added.
    $script:ModPrivacyCache[$key] = $true
}

function Get-ModReleaseSnapshot {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$ReviewedCommit, [Parameter(Mandatory)][string]$Mod)
    $root = Assert-ModReviewedRepository $RepoRoot $ReviewedCommit
    Assert-ModSafeRelativePath $Mod
    if ($Mod.Contains('/')) { throw 'exact-mod-folder-required' }
    $tree = Get-ModCommitTree $root $ReviewedCommit
    $recipeBytes = Get-ModBlob $root $tree 'build/ModMaintenance/ModRecipes.json'
    $catalog = ConvertFrom-ModStrictJson $recipeBytes
    Assert-ModObjectKeys $catalog @('SchemaVersion', 'Mods')
    if (($catalog.SchemaVersion -isnot [long] -and $catalog.SchemaVersion -isnot [int]) -or $catalog.SchemaVersion -ne 1 -or $catalog.Mods -isnot [array] -or $catalog.Mods.Count -eq 0) { throw 'invalid-release-recipe-schema' }
    $folders = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $identities = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $selected = $null
    foreach ($recipe in $catalog.Mods) {
        Assert-ModObjectKeys $recipe @('Folder', 'Id', 'Project', 'Dlls', 'IncludeFiles', 'IncludeDirectories', 'CreateOnlyFiles', 'VersionSources')
        Assert-ModSafeRelativePath $recipe.Folder
        if ($recipe.Folder.Contains('/') -or -not $folders.Add($recipe.Folder) -or $recipe.Id -isnot [string] -or -not $identities.Add($recipe.Id)) { throw 'invalid-release-recipe-identity' }
        if ($recipe.Folder -ceq $Mod) { $selected = $recipe }
    }
    if ($null -eq $selected) { throw 'exact-mod-folder-required' }
    # Recipes and root manifests must account for each other, including data-only mods.
    $manifestFolders = @($tree.Keys | Where-Object { $_ -cmatch '^[^/]+/ModInfo\.json$' } | ForEach-Object { $_.Substring(0, $_.IndexOf('/')) })
    if ($manifestFolders.Count -ne $catalog.Mods.Count) { throw 'release-recipe-catalog-incomplete' }
    foreach ($folder in $manifestFolders) { if ($folder -cnotin @($catalog.Mods.Folder)) { throw 'release-recipe-catalog-incomplete' } }
    Invoke-ModReleasePrivacy $root $ReviewedCommit $tree
    $prefix = $Mod + '/'
    $manifestBytes = Get-ModBlob $root $tree ($prefix + 'ModInfo.json')
    $manifest = ConvertFrom-ModStrictJson $manifestBytes
    foreach ($name in @('Id', 'Name', 'ModVersion', 'GameVersion')) { if (-not $manifest.Contains($name) -or $manifest[$name] -isnot [string] -or [string]::IsNullOrWhiteSpace($manifest[$name])) { throw 'invalid-mod-manifest' } }
    if ($manifest.Id -cne $selected.Id) { throw 'mod-release-identity-mismatch' }
    $expected = Get-ModNormalizedVersion $manifest.ModVersion
    $evidence = [Collections.Generic.List[object]]::new()
    $evidence.Add([pscustomobject]@{ Surface = 'ModInfo.json:ModVersion'; Status = 'observed'; Value = $manifest.ModVersion })
    foreach ($arrayName in @('Dlls', 'IncludeFiles', 'IncludeDirectories', 'CreateOnlyFiles', 'VersionSources')) { if ($selected[$arrayName] -isnot [array]) { throw 'invalid-release-recipe-array' } }
    if ($manifest.Contains('DLLFiles')) {
        if ($manifest.DLLFiles -isnot [array] -or $manifest.DLLFiles.Count -ne $selected.Dlls.Count) { throw 'manifest-dll-selection-mismatch' }
        foreach ($dll in $manifest.DLLFiles) { if ($dll -cnotin $selected.Dlls) { throw 'manifest-dll-selection-mismatch' } }
    }
    if ($selected.Project) {
        Assert-ModSafeRelativePath $selected.Project
        $xmlSettings = [Xml.XmlReaderSettings]::new()
        $xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $xmlSettings.XmlResolver = $null
        $projectStream = [IO.MemoryStream]::new((Get-ModBlob $root $tree ($prefix + $selected.Project)), $false)
        try {
            $reader = [Xml.XmlReader]::Create($projectStream, $xmlSettings)
            try { $xml = [Xml.XmlDocument]::new(); $xml.Load($reader) } finally { $reader.Dispose() }
        } finally { $projectStream.Dispose() }
        $found = 0
        $projectSurfaces = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($node in $xml.SelectNodes('/Project/PropertyGroup/*')) {
            if ($node.Name -cin @('VersionPrefix', 'Version', 'AssemblyVersion', 'FileVersion', 'InformationalVersion')) {
                Assert-ModVersionValue $expected $node.InnerText ($selected.Project + ':' + $node.Name) $evidence -Informational:($node.Name -ceq 'InformationalVersion')
                $null = $projectSurfaces.Add($node.Name)
                $found++
            } elseif ($node.Name -ceq 'VersionSuffix' -and $node.InnerText) { throw 'explicit-project-suffix-review-required' }
        }
        if ($found -eq 0) { throw 'project-version-required' }
        foreach ($surface in @('VersionPrefix', 'Version', 'AssemblyVersion', 'FileVersion', 'InformationalVersion')) {
            if (-not $projectSurfaces.Contains($surface)) { $evidence.Add([pscustomobject]@{ Surface = $selected.Project + ':' + $surface; Status = 'not-present'; Value = $null }) }
        }
    } else {
        $evidence.Add([pscustomobject]@{ Surface = 'ProductionProject'; Status = 'no-project'; Value = $null })
    }
    foreach ($source in $selected.VersionSources) {
        Assert-ModObjectKeys $source @('Path', 'Kind', 'Name')
        Assert-ModSafeRelativePath $source.Path
        $text = $script:ModUtf8.GetString((Get-ModBlob $root $tree ($prefix + $source.Path)))
        if ($source.Kind -ceq 'constant') {
            if ($source.Name -cnotmatch '^[A-Za-z_][A-Za-z0-9_]*$') { throw 'invalid-version-source-name' }
            $matchesFound = [regex]::Matches($text, '(?m)^\s*(?:(?:public|private|internal|protected)\s+)?const\s+string\s+' + [regex]::Escape($source.Name) + '\s*=\s*"([^"\r\n]+)"\s*;')
            if ($matchesFound.Count -ne 1) { throw 'ambiguous-version-source' }
            Assert-ModVersionValue $expected $matchesFound[0].Groups[1].Value ($source.Path + ':' + $source.Name) $evidence
        } elseif ($source.Kind -ceq 'assembly-attributes' -and $source.Name -ceq '') {
            foreach ($attribute in @('AssemblyVersion', 'AssemblyFileVersion', 'AssemblyInformationalVersion')) {
                $matchesFound = [regex]::Matches($text, '(?m)^\s*\[assembly:\s*' + $attribute + '(?:Attribute)?\s*\(\s*"([^"\r\n]+)"\s*\)\s*\]')
                if ($matchesFound.Count -gt 1 -or ($matchesFound.Count -eq 0 -and $attribute -cne 'AssemblyInformationalVersion')) { throw 'ambiguous-version-source' }
                if ($matchesFound.Count -eq 1) { Assert-ModVersionValue $expected $matchesFound[0].Groups[1].Value ($source.Path + ':' + $attribute) $evidence -Informational:($attribute -ceq 'AssemblyInformationalVersion') }
                else { $evidence.Add([pscustomobject]@{ Surface = $source.Path + ':' + $attribute; Status = 'not-present'; Value = $null }) }
            }
        } else { throw 'unsupported-version-source' }
    }
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $selected.IncludeFiles) { Assert-ModSafeRelativePath $path; if (-not $paths.Add($path)) { throw 'duplicate-payload-selection' } }
    foreach ($directory in $selected.IncludeDirectories) {
        Assert-ModSafeRelativePath $directory
        if ($directory -cnotin @('Config', 'Definitions', 'Graphics', 'Patches', 'Translations')) { throw 'unsupported-runtime-directory' }
        $found = 0
        foreach ($path in $tree.Keys) { if ($path.StartsWith($prefix + $directory + '/', [StringComparison]::Ordinal)) { $null = $paths.Add($path.Substring($prefix.Length)); $found++ } }
        if ($found -eq 0) { throw 'runtime-directory-empty-or-missing' }
    }
    if (-not $paths.Contains('ModInfo.json')) { throw 'payload-manifest-required' }
    foreach ($dll in $selected.Dlls) {
        Assert-ModSafeRelativePath $dll
        if ($dll.Contains('/') -or $dll -cnotmatch '^[A-Za-z0-9_.-]+\.dll$') { throw 'unsafe-own-dll-name' }
        if ($dll -match '^(?i:Game(?:\..*)?|Unity.*|0?Harmony.*|System(?:\..*)?|mscorlib|netstandard|Mono\..*|Newtonsoft\.Json|MessagePack.*|KLUnityUtils)\.dll$') { throw 'bundled-game-dependency-refused' }
        if (-not $paths.Add('Libraries/' + $dll)) { throw 'duplicate-payload-selection' }
        if ($dll -ceq 'AutoUtilitiesModFramework.dll' -and $manifest.Id -cne 'AutoUtilitiesModFramework') { throw 'bundled-framework-refused' }
    }
    $createOnly = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $selected.CreateOnlyFiles) { Assert-ModSafeRelativePath $path; if (-not $paths.Contains($path) -or -not $createOnly.Add($path)) { throw 'invalid-create-only-path' } }
    $files = [Collections.Generic.List[object]]::new()
    $ordered = [string[]]@($paths)
    [Array]::Sort($ordered, [StringComparer]::Ordinal)
    foreach ($path in $ordered) {
        if ($path -cmatch '(^|/)(?:Code|Tests|tools|bin|obj|\.vscode|\.git|\.release[^/]*)(?:/|$)' -or
            $path -match '(?i)\.(?:cs|csproj|sln|ps1|pdb|log|bak|tmp|exe|zip|7z|rar)$' -or
            ($path.StartsWith('Libraries/', [StringComparison]::Ordinal) -and $path.Substring(10) -cnotin $selected.Dlls)) { throw 'development-or-unowned-payload-refused' }
        $bytes = Get-ModBlob $root $tree ($prefix + $path)
        $files.Add([pscustomobject]@{ RelativePath = $path; Bytes = $bytes; Length = $bytes.LongLength; Sha256 = Get-ModSha256 $bytes; InstallPolicy = $(if ($createOnly.Contains($path)) { 'create-only' } else { 'replace' }) })
    }
    foreach ($dll in $selected.Dlls) {
        $file = @($files | Where-Object { $_.RelativePath -ceq ('Libraries/' + $dll) })[0]
        $temp = Join-Path (New-ModPrivateDirectory $root) 'metadata.dll'
        try {
            [IO.File]::WriteAllBytes($temp, $file.Bytes)
            $assembly = [Reflection.AssemblyName]::GetAssemblyName($temp)
            $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($temp)
            if ($assembly.Name -cne [IO.Path]::GetFileNameWithoutExtension($dll)) { throw 'own-assembly-name-mismatch' }
            Assert-ModVersionValue $expected $assembly.Version.ToString() ('Libraries/' + $dll + ':AssemblyVersion') $evidence
            Assert-ModVersionValue $expected $version.FileVersion ('Libraries/' + $dll + ':FileVersion') $evidence
            if ($version.ProductVersion) { Assert-ModVersionValue $expected $version.ProductVersion ('Libraries/' + $dll + ':ProductVersion') $evidence -Informational }
            else { $evidence.Add([pscustomobject]@{ Surface = 'Libraries/' + $dll + ':ProductVersion'; Status = 'not-present'; Value = $null }) }
        } finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
    }
    $dependencies = @()
    if ($manifest.Contains('Dependencies')) {
        if ($manifest.Dependencies -isnot [array]) { throw 'invalid-manifest-dependencies' }
        foreach ($dependency in $manifest.Dependencies) { if ($dependency -isnot [string] -or [string]::IsNullOrWhiteSpace($dependency)) { throw 'invalid-manifest-dependencies' } }
        $dependencies = @($manifest.Dependencies)
    }
    $workshop = $null
    if ($paths.Contains('SteamWorkshopItemId')) {
        $workshop = $script:ModUtf8.GetString((Get-ModBlob $root $tree ($prefix + 'SteamWorkshopItemId'))).Trim()
        if ($workshop -cnotmatch '^[1-9][0-9]{0,19}$') { throw 'invalid-workshop-identity' }
    }
    # Repeat clean/head check after all reads; a changing checkout never yields a ready snapshot.
    $null = Assert-ModReviewedRepository $root $ReviewedCommit
    [pscustomobject]@{ SchemaVersion = 1; Repository = 'ashugar/Stardeus-Mod-Maintenance'; ReviewedCommit = $ReviewedCommit; Folder = $Mod; Id = $manifest.Id; Name = $manifest.Name; ModVersion = $manifest.ModVersion; GameVersion = $manifest.GameVersion; Dependencies = $dependencies; WorkshopItemId = $workshop; RecipeSha256 = Get-ModSha256 $recipeBytes; VersionEvidence = $evidence.ToArray(); Files = $files.ToArray() }
}
