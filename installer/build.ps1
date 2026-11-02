param(
    [switch]$Win7,
    [switch]$Win10,
    [string]$Isc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)
$main = "ImgViewer.csproj"

Write-Host "Build -> plugins-samples"
dotnet build plugins-samples\ImgViewer.Plugins.Samples.csproj -c Release --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "build plugins-samples failed" }
if (-not (Test-Path "plugins-samples\bin\Release\netstandard2.0\ImgViewer.Plugins.Samples.dll")) { throw "sample plugin dll missing" }

function Publish([string]$out, [string[]]$tfmProps) {
    Write-Host "Publish -> $out"
    dotnet publish $main -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        @tfmProps -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish $out failed" }
}

function BuildSetup([switch]$IsWin7) {
    Write-Host "ISCC -> $Isc (Win7=$IsWin7)"
    $argList = if ($IsWin7) { @('/DWin7', 'installer\PhotographerEye.iss') } else { @('installer\PhotographerEye.iss') }
    $p = Start-Process -FilePath $Isc -ArgumentList $argList -NoNewWindow -Wait -PassThru
    if ($p.ExitCode -ne 0) { throw "ISCC failed" }
}

if (-not $Win7 -and -not $Win10 -or ($Win7 -and $Win10)) {
    $Win10 = $true; $Win7 = $true
}

if ($Win10) {
    Publish "dist" @()
    BuildSetup
}
if ($Win7) {
    Publish "dist-win7" @("-p:Win7=true")
    BuildSetup -IsWin7
}

Get-ChildItem dist\installer, dist-win7\installer -ErrorAction SilentlyContinue | Select-Object FullName, @{n = "MB"; e = { [math]::Round($_.Length / 1MB) }}