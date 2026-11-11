param(
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
# Old layout (all samples in one folder) must not survive in the publish dir.
Remove-Item -Recurse -Force dist\plugins\samples -ErrorAction SilentlyContinue

Write-Host "Publish -> dist"
dotnet publish $main -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host "ISCC -> $Isc"
$p = Start-Process -FilePath $Isc -ArgumentList @('installer\PhotographerEye.iss') -NoNewWindow -Wait -PassThru
if ($p.ExitCode -ne 0) { throw "ISCC failed" }

Get-ChildItem dist\installer -ErrorAction SilentlyContinue | Select-Object FullName, @{n = "MB"; e = { [math]::Round($_.Length / 1MB) }}
