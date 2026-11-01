<#
  Drawing Studio for Revit - build and install

  Builds the standalone app once and the Revit bridge once per Revit version,
  then registers the bridge with each Revit through an .addin manifest.

  Usage (PowerShell, from this folder):
      .\install.ps1                         # every Revit 2022-2027 found on this PC
      .\install.ps1 -Versions 2024,2026     # only these
      .\install.ps1 -Uninstall

  Needs the .NET 8 SDK (plus the .NET 10 SDK for Revit 2027).
  Close Revit before installing.
#>
param(
    [int[]]$Versions,
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$installTo = Join-Path $env:LOCALAPPDATA 'DrawingStudio'
$appDir    = Join-Path $installTo 'App'
$addinName = 'DrawingStudio.Bridge.addin'
$supported = 2022..2027

function Get-AddinFolder([int]$v) { Join-Path $env:APPDATA "Autodesk\Revit\Addins\$v" }

if ($Uninstall) {
    foreach ($v in $supported) {
        $manifest = Join-Path (Get-AddinFolder $v) $addinName
        if (Test-Path $manifest) { Remove-Item $manifest -Force; Write-Host "Removed add-in for Revit $v" }
    }
    if (Test-Path $installTo) { Remove-Item $installTo -Recurse -Force }
    Write-Host 'Drawing Studio uninstalled.' -ForegroundColor Green
    return
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found. Install .NET 8 SDK from https://dotnet.microsoft.com/download'
}

if (-not $Versions) {
    $Versions = $supported | Where-Object { Test-Path "C:\Program Files\Autodesk\Revit $_\Revit.exe" }
    if (-not $Versions) { throw 'No Revit 2022-2027 installation found. Pass -Versions explicitly.' }
}

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Write-Warning 'Revit is running. Close it so the add-in files can be replaced.'
}

# ---------------------------------------------------------------- app
Write-Host "`nPublishing Drawing Studio app..." -ForegroundColor Cyan
dotnet publish "$root\src\DrawingStudio.App\DrawingStudio.App.csproj" -c Release -r win-x64 --self-contained false -o $appDir
if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }

# ---------------------------------------------------------------- bridge per Revit version
foreach ($v in $Versions) {
    Write-Host "`nBuilding bridge for Revit $v..." -ForegroundColor Cyan
    $out = Join-Path $installTo "Bridge\R$v"
    dotnet build "$root\src\DrawingStudio.Bridge\DrawingStudio.Bridge.csproj" -c Release -p:RevitVersion=$v -o $out
    if ($LASTEXITCODE -ne 0) { throw "Bridge build for Revit $v failed." }

    $dll = Join-Path $out 'DrawingStudio.Bridge.dll'
    [xml]$manifest = Get-Content "$root\src\DrawingStudio.Bridge\$addinName"
    $manifest.RevitAddIns.AddIn.Assembly = $dll

    $addinFolder = Get-AddinFolder $v
    New-Item -ItemType Directory -Force -Path $addinFolder | Out-Null
    $manifest.Save((Join-Path $addinFolder $addinName))
    Write-Host "Registered with Revit $v" -ForegroundColor Green
}

Write-Host "`nDone. Start Revit, open a project and click Drawing Studio on the 'Drawing Studio' ribbon tab." -ForegroundColor Green
Write-Host "App: $appDir\DrawingStudio.exe"
