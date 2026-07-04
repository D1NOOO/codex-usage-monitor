[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

$textExtensions = @(
    '.cs', '.ps1', '.md', '.json', '.yml', '.yaml', '.txt',
    '.gitignore', '.gitattributes', '.editorconfig'
)
$textFiles = Get-ChildItem -LiteralPath $repoRoot -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '\\(\.git|artifacts|\.build)\\' -and
        $_.Extension -in $textExtensions
    }

$privacyPatterns = @(
    [regex]::Escape(('C:' + '\Users\')),
    ('ADMINI' + '~1'),
    [regex]::Escape(('AppData' + '\Local\Temp')),
    ('codex-' + 'clipboard'),
    '@gmail\.com',
    '@qq\.com',
    '@163\.com'
)
$secretPatterns = @(
    '-----BEGIN (RSA|OPENSSH|EC|DSA) PRIVATE KEY-----',
    'github_pat_[A-Za-z0-9_]{20,}',
    'gh[pousr]_[A-Za-z0-9]{20,}',
    'sk-[A-Za-z0-9_-]{20,}',
    'Bearer\s+[A-Za-z0-9._-]{20,}'
)

$privacyMatches = @($textFiles | Select-String -Pattern $privacyPatterns -AllMatches)
$secretMatches = @($textFiles | Select-String -Pattern $secretPatterns -AllMatches)
if ($privacyMatches.Count -gt 0) {
    $privacyMatches | Select-Object Path, LineNumber, Line | Format-List
    throw 'Potential personal path or identity data found.'
}
if ($secretMatches.Count -gt 0) {
    $secretMatches | Select-Object Path, LineNumber, Line | Format-List
    throw 'Potential credential or secret found.'
}

foreach ($forbidden in @('settings.json', 'auth.json')) {
    if (Test-Path -LiteralPath (Join-Path $repoRoot $forbidden)) {
        throw "Runtime/private file '$forbidden' must not be committed at repository root."
    }
}

$localizationPath = Join-Path $repoRoot 'src\Localization.cs'
$localization = Get-Content -Raw -LiteralPath $localizationPath
$definedKeys = [regex]::Matches($localization, '\{"([A-Za-z0-9]+)",') |
    ForEach-Object { $_.Groups[1].Value }
$badCounts = @($definedKeys | Group-Object | Where-Object { $_.Count -ne 3 })
if ($badCounts.Count -gt 0) {
    $badCounts | Select-Object Name, Count | Format-Table
    throw 'Every localization key must exist exactly once in zh-CN, zh-TW, and en.'
}

$source = (Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'src\CodexRateMonitor.cs')) +
    (Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'src\AppearanceSettingsForm.cs'))
$usedKeys = [regex]::Matches($source, 'I18n\.(?:T|F|Translate)\("([A-Za-z0-9]+)"') |
    ForEach-Object { $_.Groups[1].Value } |
    Sort-Object -Unique
$missing = @($usedKeys | Where-Object { $_ -notin $definedKeys })
if ($missing.Count -gt 0) {
    throw "Missing localization keys: $($missing -join ', ')"
}

$tokens = $null
$parseErrors = $null
[Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repoRoot 'scripts\build.ps1'),
    [ref]$tokens,
    [ref]$parseErrors
) | Out-Null
if ($parseErrors.Count -gt 0) {
    $parseErrors | Format-List
    throw 'scripts/build.ps1 has syntax errors.'
}

Get-ChildItem -LiteralPath $repoRoot -Recurse -Filter '*.json' |
    Where-Object { $_.FullName -notmatch '\\(artifacts|\.build)\\' } |
    ForEach-Object {
        Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json | Out-Null
    }

Write-Host "Verified $($textFiles.Count) text files."
Write-Host "Localization keys: $(($definedKeys | Sort-Object -Unique).Count)"
Write-Host 'Privacy/secret scan: clean'
