<#
  Builds dist\DrawingStudio-Setup-<version>.exe on a Windows PC.

      .\build-installer.ps1
      .\build-installer.ps1 -Version 1.1.0
      .\build-installer.ps1 -RevitVersions 2024,2025      # smaller installer

  Needs: .NET 8 SDK (.NET 10 SDK too for the Revit 2027 bridge) and Inno Setup 6.
  Inno Setup is installed automatically with winget if it is missing.
#>
param(
    [string]$Version = '1.0.0',
    [int[]]$RevitVersions = @(2022, 2023, 2024, 2025, 2026, 2027)
)

$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent $MyInvocation.MyCommand.Path
$build = Join-Path $root 'build'
$dist  = Join-Path $root 'dist'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET SDK was not found. Install the .NET 8 SDK from https://dotnet.microsoft.com/download'
}
$sdks = (& dotnet --list-sdks) -join "`n"

Remove-Item $build, $dist -Recurse -Force -ErrorAction SilentlyContinue

# ------------------------------------------------------------ bridge, once per Revit version
foreach ($v in $RevitVersions) {
    if ($v -ge 2027 -and $sdks -notmatch '(?m)^10\.') {
        Write-Warning "Skipping Revit $v - it needs the .NET 10 SDK."
        continue
    }
    Write-Host "`n== Bridge for Revit $v" -ForegroundColor Cyan
    dotnet build "$root\src\DrawingStudio.Bridge\DrawingStudio.Bridge.csproj" -c Release `
        -p:RevitVersion=$v -p:Version=$Version -o "$build\Bridge\R$v"
    if ($LASTEXITCODE -ne 0) { throw "Bridge build for Revit $v failed." }
}

# ------------------------------------------------------------ bundle the bridges into the app
$bundle = Join-Path $build 'bridges.zip'
$stage  = Join-Path $build 'bundle'
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Get-ChildItem "$build\Bridge" -Directory | ForEach-Object {
    New-Item -ItemType Directory -Force -Path "$stage\$($_.Name)" | Out-Null
    Copy-Item "$($_.FullName)\*.dll" "$stage\$($_.Name)\"
}
Compress-Archive -Path "$stage\*" -DestinationPath $bundle -Force

# ------------------------------------------------------------ app (self-contained single exe: no .NET needed on user PCs)
Write-Host "`n== Drawing Studio app" -ForegroundColor Cyan
dotnet publish "$root\src\DrawingStudio.App\DrawingStudio.App.csproj" -c Release -r win-x64 `
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version -p:BridgeBundle=$bundle -o "$build\App"
if ($LASTEXITCODE -ne 0) { throw 'App build failed.' }
New-Item -ItemType Directory -Force -Path $dist | Out-Null
Copy-Item "$build\App\DrawingStudio.exe" "$dist\DrawingStudio.exe"

# portable package: the exe plus a one-click way to take the add-in out of Revit again
$portable = Join-Path $build 'Portable\DrawingStudio'
New-Item -ItemType Directory -Force -Path $portable | Out-Null
Copy-Item "$build\App\DrawingStudio.exe" $portable
Set-Content -Path "$portable\Remove from Revit.bat" -Encoding ascii -Value '@"%~dp0DrawingStudio.exe" --remove-addin'
Set-Content -Path "$portable\README.txt" -Encoding ascii -Value @(
    'Drawing Studio for Revit - portable',
    '',
    '1. Put this folder anywhere (Desktop, D:\Tools, a USB drive). No installation or admin rights needed.',
    '2. Double-click DrawingStudio.exe. It adds itself to every Revit 2022-2027 on this PC.',
    '3. Start Revit and click "Always Load" for Drawing Studio Bridge.',
    '4. The app connects to Revit automatically (or use the Drawing Studio button on the Revit ribbon).',
    '',
    'Settings are kept in the DrawingStudio-Data folder created next to the exe.',
    'If you move the folder, run DrawingStudio.exe once from the new place.',
    'To take it out of Revit: double-click "Remove from Revit.bat".')
Compress-Archive -Path $portable -DestinationPath "$dist\DrawingStudio-Portable-$Version.zip" -Force

# ------------------------------------------------------------ installer
$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if (-not $iscc) {
    Write-Host 'Inno Setup not found - installing it with winget...' -ForegroundColor Yellow
    winget install --id JRSoftware.InnoSetup -e --accept-source-agreements --accept-package-agreements
    $iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Install Inno Setup 6 from https://jrsoftware.org/isdl.php and run this again.' }
}

Write-Host "`n== Installer" -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" "$root\installer\DrawingStudio.iss"
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

Write-Host "`nReady in $dist :" -ForegroundColor Green
Get-ChildItem $dist | ForEach-Object { Write-Host "  $($_.Name)" }
