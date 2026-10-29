[CmdletBinding()]
param(
    [string]$GameRoot = 'D:\Program Files (x86)\Steam\steamapps\common\ArchitectureDesign',
    [string]$ProfileRoot = '',
    [string]$OutputRoot = '',
    [string]$CasesFile = ''
)
$ErrorActionPreference = 'Stop'
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
if (!$ProfileRoot) { $ProfileRoot = Join-Path ([IO.Path]::GetTempPath()) ('ArchitectureDesign_ZhCN_Check\' + $runId) }
if (!$OutputRoot) { $OutputRoot = Join-Path $PSScriptRoot 'results' }
if (!$CasesFile) { $CasesFile = Join-Path $PSScriptRoot 'capture-cases.json' }

function Get-FullPath([string]$Value) {
    if ($Value.IndexOfAny([char[]]('"' + "`r`n")) -ge 0) { throw '路径不能包含双引号或换行。' }
    $full = [IO.Path]::GetFullPath($Value)
    if ($full.Length -gt [IO.Path]::GetPathRoot($full).Length) { $full = $full.TrimEnd('\', '/') }
    return $full
}
function Test-Within([string]$Child, [string]$Parent) {
    return $Child.Equals($Parent, [StringComparison]::OrdinalIgnoreCase) -or $Child.StartsWith($Parent + '\', [StringComparison]::OrdinalIgnoreCase)
}
function Assert-NoReparse([string]$Path) {
    $probe = $Path
    while ($probe) {
        if (Test-Path -LiteralPath $probe) {
            $entry = Get-Item -LiteralPath $probe -Force
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('巡检目录不能经过链接或联接：' + $probe) }
        }
        $parent = [IO.Directory]::GetParent($probe)
        if (!$parent) { break }
        $probe = $parent.FullName
    }
}
function Assert-ProfileTreeNoReparse([string]$Path) {
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    $pending.Push($Path)
    while ($pending.Count -gt 0) {
        foreach ($entry in Get-ChildItem -LiteralPath ($pending.Pop()) -Force) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw ('巡检存档内部不能包含链接或联接：' + $entry.FullName) }
            if ($entry.PSIsContainer) { $pending.Push($entry.FullName) }
        }
    }
}
$GameRoot = Get-FullPath $GameRoot
$ProfileRoot = Get-FullPath $ProfileRoot
$OutputRoot = Get-FullPath $OutputRoot
$CasesFile = Get-FullPath $CasesFile
$gameExe = Join-Path $GameRoot 'ArchitectureDesign.exe'
if (!(Test-Path -LiteralPath $gameExe -PathType Leaf)) { throw ('未找到游戏：' + $gameExe) }
if (Get-Process -Name 'ArchitectureDesign', 'ArchitectureDesign-Win64-Shipping' -ErrorAction SilentlyContinue) { throw '请先退出游戏，再运行巡检。' }
foreach ($path in @($ProfileRoot, $OutputRoot)) {
    if ($path.Equals([IO.Path]::GetPathRoot($path), [StringComparison]::OrdinalIgnoreCase)) { throw '巡检目录不能是磁盘根目录。' }
    Assert-NoReparse $path
    if (Test-Within $path $GameRoot) { throw '巡检存档和输出目录必须位于游戏目录以外。' }
    $realProfile = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'ArchitectureDesign'
    if (Test-Within $path (Get-FullPath $realProfile)) { throw '不能把巡检文件写入玩家默认存档目录。' }
}
if ((Test-Within $ProfileRoot $OutputRoot) -or (Test-Within $OutputRoot $ProfileRoot)) { throw 'ProfileRoot 与 OutputRoot 不能相同或互相嵌套。' }

# Only a fresh directory, or one created and marked by this script, may be used.
$marker = Join-Path $ProfileRoot '.ArchitectureDesign-ZhCN-inspection-profile.json'
if (Test-Path -LiteralPath $ProfileRoot) {
    if (!(Test-Path -LiteralPath $ProfileRoot -PathType Container)) { throw 'ProfileRoot 必须是目录。' }
    Assert-ProfileTreeNoReparse $ProfileRoot
    $items = @(Get-ChildItem -LiteralPath $ProfileRoot -Force)
    if ($items.Count -gt 0) {
        if (!(Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'ProfileRoot 已有文件且不是本脚本建立的巡检目录；已停止以保护存档。' }
        $tag = [IO.File]::ReadAllText($marker, [Text.Encoding]::UTF8) | ConvertFrom-Json
        if ($tag.purpose -ne 'ArchitectureDesign-ZhCN-isolated-inspection' -or $tag.profileRoot -ne $ProfileRoot) { throw '巡检目录标记无效。' }
    }
}
$cases = [IO.File]::ReadAllText($CasesFile, [Text.Encoding]::UTF8) | ConvertFrom-Json
if ($cases.Count -eq 0) { throw '案例文件为空。' }
$seenNames = @{}
foreach ($case in $cases) {
    if ($case.name -notmatch '^[a-z0-9_]+$' -or $seenNames.ContainsKey($case.name)) { throw '案例名无效或重复。' }
    $seenNames[$case.name] = $true
    if ($case.args -match '(?i)(^|\s)-userdir\b' -or $case.args -match '[\r\n]') { throw '案例不能改写隔离存档参数。' }
    foreach ($name in $case.files) {
        if ($name -notmatch '^[a-zA-Z0-9_-]+\.png$') { throw ('截图文件名无效：' + $name) }
    }
    if ($case.rundgang) {
        if ($case.rundgang -notmatch '^[a-zA-Z0-9_-]+\.txt$') { throw '巡游脚本必须是本目录中的 TXT 文件。' }
        if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot $case.rundgang) -PathType Leaf)) { throw ('缺少巡游脚本：' + $case.rundgang) }
    }
}

$shippingExe = Join-Path $GameRoot 'ArchitectureDesign\Binaries\Win64\ArchitectureDesign-Win64-Shipping.exe'
$buildHash = (Get-FileHash -LiteralPath $shippingExe -Algorithm SHA256).Hash.ToLowerInvariant()
New-Item -ItemType Directory -Path $ProfileRoot -Force | Out-Null
@{ purpose = 'ArchitectureDesign-ZhCN-isolated-inspection'; profileRoot = $ProfileRoot; gameExeSha256 = $buildHash } | ConvertTo-Json | Set-Content -LiteralPath $marker -Encoding UTF8
$config = Join-Path $ProfileRoot 'Saved\Config\Windows'
New-Item -ItemType Directory -Path $config -Force | Out-Null
$settings = Join-Path $config 'GameUserSettings.ini'
if (!(Test-Path -LiteralPath $settings)) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'GameUserSettings.ini') -Destination $settings }
$runOutput = Join-Path $OutputRoot $runId
New-Item -ItemType Directory -Path $runOutput -Force | Out-Null
$report = @()
Write-Output ('独立巡检存档：' + $ProfileRoot)
Write-Output ('本次截图输出：' + $runOutput)
foreach ($case in $cases) {
    if (Get-Process -Name 'ArchitectureDesign', 'ArchitectureDesign-Win64-Shipping' -ErrorAction SilentlyContinue) { throw '检测到游戏仍在运行，已停止后续案例。' }
    if ((Get-FileHash -LiteralPath $shippingExe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $buildHash) { throw '游戏主程序在巡检期间发生变化，已停止。' }
    $caseArgs = [string]$case.args
    if ($case.rundgang) {
        $scriptPath = (Join-Path $PSScriptRoot $case.rundgang).Replace('\', '/')
        $caseArgs += ' -rundgang="' + $scriptPath + '"'
    }
    $argsString = '-userdir="' + $ProfileRoot.Replace('\', '/') + '/" ' + $caseArgs.Trim()
    $started = Get-Date
    Write-Output ('开始 ' + $case.name + ' ' + $started.ToString('HH:mm:ss'))
    $run = Start-Process -FilePath $gameExe -ArgumentList $argsString -WorkingDirectory $GameRoot -WindowStyle Hidden -PassThru -Wait
    $destination = Join-Path $runOutput $case.name
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $records = @()
    foreach ($name in $case.files) {
        $source = Join-Path $ProfileRoot ('Saved\Screenshots\' + $name)
        $file = Get-Item -LiteralPath $source
        if ($file.LastWriteTime -lt $started) { throw ('截图没有更新，已停止：' + $case.name + '/' + $name) }
        Copy-Item -LiteralPath $source -Destination (Join-Path $destination $name)
        $records += @{ name = $name; length = $file.Length; sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $record = @{ case = $case.name; arguments = $caseArgs.Trim(); started = $started.ToString('o'); exitCode = $run.ExitCode; files = $records; profileRoot = $ProfileRoot; gameExeSha256 = $buildHash }
    $report += $record
    $record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'capture.json') -Encoding UTF8
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $runOutput 'runtime-captures.json') -Encoding UTF8
    if ($run.ExitCode -ne 0) { throw ('游戏异常退出：' + $case.name + '，退出码 ' + $run.ExitCode) }
    Write-Output ('完成 ' + $case.name + '，' + $records.Count + ' 张截图')
}
Write-Output ('巡检完成：' + $runOutput)
