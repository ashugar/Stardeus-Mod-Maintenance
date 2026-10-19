[CmdletBinding()]
param(
    [string]$RepoRoot = (Join-Path $PSScriptRoot '../..')
)

$ErrorActionPreference = 'Stop'

function Invoke-RepositoryGit {
    param([string]$Root, [string[]]$GitArguments)
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    foreach ($argument in @('--no-replace-objects', '-C', $Root) + $GitArguments) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $errorRead = $process.StandardError.ReadToEndAsync()
        $output = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
        $null = $errorRead.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'git-operation-failed' }
        return $output
    } finally {
        $process.Dispose()
    }
}

function Get-SafeFileIdentity {
    param([string]$RelativePath)
    # Filenames can contain private text; identify failures without echoing them.
    $bytes = [Text.Encoding]::UTF8.GetBytes($RelativePath)
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).Substring(0, 12).ToLowerInvariant()
}

try {
    $root = [IO.Path]::GetFullPath($RepoRoot).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $actual = (Invoke-RepositoryGit -Root $root -GitArguments @('rev-parse', '--show-toplevel')).Trim()
    $actual = [IO.Path]::GetFullPath($actual).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $pathComparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (-not [string]::Equals($root, $actual, $pathComparison)) { throw 'repository-root-required' }
    $tracked = Invoke-RepositoryGit -Root $root -GitArguments @('ls-files', '--cached', '-z')
    $paths = @($tracked.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
    $failures = [Collections.Generic.List[string]]::new()
    $counts = @{ Json = 0; Manifests = 0; Xml = 0; PowerShell = 0 }
    $jsonOptions = [System.Text.Json.JsonDocumentOptions]::new()
    $jsonOptions.AllowTrailingCommas = $true
    $jsonOptions.CommentHandling = [System.Text.Json.JsonCommentHandling]::Skip
    $utf8 = [Text.UTF8Encoding]::new($false, $true)

    foreach ($relative in $paths) {
        $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
        if ($extension -notin @('.json', '.csproj', '.props', '.targets', '.xml', '.resx', '.ps1', '.psm1', '.psd1')) { continue }
        $identity = Get-SafeFileIdentity $relative
        $full = [IO.Path]::GetFullPath((Join-Path $root $relative))
        if (-not $full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, $pathComparison)) {
            $failures.Add("unsafe-path file=$identity")
            continue
        }
        $cursor = $full
        $unsafe = $false
        while ($cursor -and -not [string]::Equals($cursor, $root, $pathComparison)) {
            if (([IO.File]::GetAttributes($cursor) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { $unsafe = $true; break }
            $cursor = [IO.Path]::GetDirectoryName($cursor)
        }
        if ($unsafe) { $failures.Add("reparse-point file=$identity"); continue }

        try {
            if ($extension -eq '.json') {
                $counts.Json++
                $text = [IO.File]::ReadAllText($full, $utf8)
                $isManifest = $relative -match '^[^/\\]+[/\\]ModInfo\.json$'
                $document = if ($isManifest) {
                    [System.Text.Json.JsonDocument]::Parse($text)
                } else {
                    [System.Text.Json.JsonDocument]::Parse($text, $jsonOptions)
                }
                try {
                    if ($isManifest) {
                        $counts.Manifests++
                        if ($document.RootElement.ValueKind -ne [System.Text.Json.JsonValueKind]::Object) { throw 'manifest-object-required' }
                        foreach ($field in @('Id', 'Name', 'ModVersion', 'GameVersion')) {
                            $value = [System.Text.Json.JsonElement]::new()
                            if (-not $document.RootElement.TryGetProperty($field, [ref]$value) -or
                                $value.ValueKind -ne [System.Text.Json.JsonValueKind]::String -or
                                [string]::IsNullOrWhiteSpace($value.GetString())) { throw 'manifest-field-required' }
                        }
                    }
                } finally {
                    $document.Dispose()
                }
            } elseif ($extension -in @('.csproj', '.props', '.targets', '.xml', '.resx')) {
                $counts.Xml++
                $settings = [Xml.XmlReaderSettings]::new()
                $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
                $settings.XmlResolver = $null
                $reader = [Xml.XmlReader]::Create($full, $settings)
                try { while ($reader.Read()) {} } finally { $reader.Dispose() }
            } else {
                $counts.PowerShell++
                $tokens = $null
                $parseErrors = $null
                $null = [Management.Automation.Language.Parser]::ParseFile($full, [ref]$tokens, [ref]$parseErrors)
                if ($parseErrors.Count -ne 0) { throw 'powershell-syntax-invalid' }
            }
        } catch {
            $category = if ($extension -eq '.json') { 'json-or-manifest-invalid' } elseif ($extension -in @('.ps1', '.psm1', '.psd1')) { 'powershell-syntax-invalid' } else { 'xml-invalid-or-dtd-refused' }
            $failures.Add("$category file=$identity")
        }
    }
    if ($counts.Manifests -eq 0) { $failures.Add('no-mod-manifests') }
    foreach ($failure in $failures) { [Console]::Error.WriteLine("FAIL $failure") }
    if ($failures.Count -ne 0) { exit 1 }
    [Console]::WriteLine("PASS repository metadata: $($counts.Json) JSON, $($counts.Manifests) manifests, $($counts.Xml) XML, $($counts.PowerShell) PowerShell scripts")
    exit 0
} catch {
    # Parser and I/O exception messages can include private values or absolute paths.
    [Console]::Error.WriteLine('ERROR repository-validation-operation-failed')
    exit 2
}
