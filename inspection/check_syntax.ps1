[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$results = @()
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File) {
    $tokens = $null
    $errors = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    $details = @($errors | ForEach-Object { @{ message = $_.Message; line = $_.Extent.StartLineNumber; column = $_.Extent.StartColumnNumber } })
    $results += @{ file = $file.Name; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant(); pass = $details.Count -eq 0; errors = $details }
}
$cases = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'capture-cases.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
$manual = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'capture-audio-exit-manual.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
$allCases = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'capture-all.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
$allImageCount = 0
foreach ($case in $allCases) { $allImageCount += @($case.files).Count }
$imageCount = 0
foreach ($case in $cases) { $imageCount += @($case.files).Count }
$missing = @()
foreach ($case in @($cases) + @($manual) + @($allCases)) {
    if ($case.rundgang -and !(Test-Path -LiteralPath (Join-Path $PSScriptRoot $case.rundgang) -PathType Leaf)) { $missing += $case.rundgang }
}
if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'GameUserSettings.ini') -PathType Leaf)) { $missing += 'GameUserSettings.ini' }
$pass = @($results | Where-Object { !$_.pass }).Count -eq 0 -and $cases.Count -eq 9 -and $imageCount -eq 18 -and $manual.Count -eq 1 -and @($manual[0].files).Count -eq 3 -and $allCases.Count -eq 10 -and $allImageCount -eq 21 -and $missing.Count -eq 0
$report = @{ all_pass = $pass; powershell_version = $PSVersionTable.PSVersion.ToString(); checked_at = (Get-Date).ToString('o'); scripts = $results; default_cases = $cases.Count; default_expected_pngs = $imageCount; optional_manual_cases = $manual.Count; optional_expected_pngs = @($manual[0].files).Count; all_cases = $allCases.Count; all_expected_pngs = $allImageCount; missing_dependencies = $missing; scope = 'Parser and bundled-case checks only. No game process started by this syntax check. R4 runtime evidence is in docs/runtime-captures.json.' }
$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'syntax-check.json') -Encoding UTF8
if (!$pass) { throw 'Syntax or bundled-file check failed; see syntax-check.json.' }
Write-Output ('PASS: ' + $results.Count + ' PowerShell scripts; 10 full cases / 21 PNGs. No game started by syntax check.')
