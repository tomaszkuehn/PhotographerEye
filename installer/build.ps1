param(
    [switch]$Win7,
    [switch]$Win10,
    [string]$Isc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"
Set-Location (Split-Path -Parent $PSScriptRoot)
$main = "ImgViewer.csproj"

# Bundled plugins: one project per folder eyePlugins\<name>, each shipped as plugins\<folder>.
foreach ($proj in Get-ChildItem -Path "eyePlugins\*" -Directory | ForEach-Object { Get-ChildItem $_.FullName -Filter "*.csproj" }) {
    Write-Host "Build -> $($proj.Directory.Name)"
    dotnet build $proj.FullName -c Release --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build $($proj.Directory.Name) failed" }
}
# Old layout (all samples in one folder) must not survive in the publish dirs.
Remove-Item -Recurse -Force dist\plugins\samples, dist-win7\plugins\samples -ErrorAction SilentlyContinue

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