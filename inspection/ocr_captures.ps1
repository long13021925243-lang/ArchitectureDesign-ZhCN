[CmdletBinding()]
param([string]$InputPath = '', [string]$OutputPath = '')
if (!$InputPath) { $InputPath = Join-Path $PSScriptRoot 'results' }
$InputPath = [IO.Path]::GetFullPath($InputPath)
if (!$OutputPath) {
    if (Test-Path -LiteralPath $InputPath -PathType Container) { $OutputPath = Join-Path $InputPath 'ocr-captures.json' }
    else { $OutputPath = Join-Path ([IO.Path]::GetDirectoryName($InputPath)) 'ocr-captures.json' }
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null=[Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
$null=[Windows.Storage.Streams.IRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime]
$null=[Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$null=[Windows.Graphics.Imaging.SoftwareBitmap,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$null=[Windows.Media.Ocr.OcrEngine,Windows.Media.Ocr,ContentType=WindowsRuntime]
$null=[Windows.Media.Ocr.OcrResult,Windows.Media.Ocr,ContentType=WindowsRuntime]
$null=[Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime]
$awaiter=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetGenericArguments().Length -eq 1 -and $_.GetParameters().Length -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await-Result($Operation,[Type]$Type) {
    $task=$awaiter.MakeGenericMethod($Type).Invoke($null,@($Operation))
    $task.Wait()
    return $task.Result
}
$engine=[Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if (!$engine) { throw 'No installed Windows OCR language is available' }
$item=Get-Item -LiteralPath $InputPath
$files=if ($item.PSIsContainer) { @(Get-ChildItem -LiteralPath $InputPath -Recurse -Filter '*.png' -File) } else { @($item) }
$results=@()
foreach ($file in $files) {
    $storage=Await-Result ([Windows.Storage.StorageFile]::GetFileFromPathAsync($file.FullName)) ([Windows.Storage.StorageFile])
    $stream=Await-Result ($storage.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
    try {
        $decoder=Await-Result ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap=Await-Result ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        try {
            $ocr=Await-Result ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
            $english=@()
            $lines=@()
            foreach ($line in $ocr.Lines) {
                $words=@()
                foreach ($word in $line.Words) {
                    $w=@{text=$word.Text;x=$word.BoundingRect.X;y=$word.BoundingRect.Y;width=$word.BoundingRect.Width;height=$word.BoundingRect.Height}
                    $words+=$w
                    if ($word.Text -match '[A-Za-z]{3,}|^(On|No)$') { $english+=$w }
                }
                $lines+=@{text=$line.Text;words=$words}
            }
            $results+=@{file=$file.FullName;sha256=(Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant();language=$engine.RecognizerLanguage.LanguageTag;lines=$lines;latinCandidates=$english}
            Write-Output ($file.Name+' : '+$english.Count+' Latin candidates')
        } finally { if ($bitmap) { $bitmap.Dispose() } }
    } finally { $stream.Dispose() }
}
$json=$results | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($OutputPath,$json,(New-Object Text.UTF8Encoding($false)))
