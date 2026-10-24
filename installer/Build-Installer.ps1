#Requires -Version 5.1
<#
.SYNOPSIS
  Publica WG-RDP (autocontenido) y genera WG-RDP-Setup-<versión>.exe
  con WireGuard incluido.
#>
$ErrorActionPreference = "Stop"

$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Dist = Join-Path $Root "dist"
$AppOut = Join-Path $Dist "app"
$Redist = Join-Path $Dist "redist"
$Tools = Join-Path $Root ".tools"
$Version = "1.0.0"
$WgUrl = "https://download.wireguard.com/windows-client/wireguard-amd64-1.1.1.msi"
$InnoPkgVersion = "6.4.3"

function Write-Step([string]$msg) {
    Write-Host "`n==> $msg" -ForegroundColor Cyan
}

New-Item -ItemType Directory -Force -Path $AppOut, $Redist, $Tools | Out-Null

Write-Step "Publicando WG-RDP autocontenido (win-x64)"
# Carpeta (no un solo exe): el control RDP ActiveX es más fiable así.
dotnet publish (Join-Path $Root "src\WgRdp\WgRdp.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o $AppOut `
    -p:PublishSingleFile=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falló." }

Write-Step "Descargando MSI oficial de WireGuard"
$wgMsi = Join-Path $Redist "wireguard-amd64.msi"
$cached = Join-Path $env:ProgramData "WgRdp\setup\wireguard-amd64-1.1.1.msi"
if (Test-Path $cached) {
    Copy-Item $cached $wgMsi -Force
    Write-Host "Usando MSI en caché: $cached"
}
elseif (-not (Test-Path $wgMsi) -or (Get-Item $wgMsi).Length -lt 100000) {
    Invoke-WebRequest -Uri $WgUrl -OutFile $wgMsi -UseBasicParsing
}
if (-not (Test-Path $wgMsi) -or (Get-Item $wgMsi).Length -lt 100000) {
    throw "No se pudo obtener el MSI de WireGuard."
}
# También junto al exe, por si la app lo necesita más tarde (asistente / reinstalación).
$bundledDir = Join-Path $AppOut "Setup\wireguard"
New-Item -ItemType Directory -Force -Path $bundledDir | Out-Null
Copy-Item $wgMsi (Join-Path $bundledDir "wireguard-amd64-1.1.1.msi") -Force
Write-Host ("WireGuard MSI: {0:N0} bytes" -f (Get-Item $wgMsi).Length)

function Find-Iscc {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        (Join-Path $Tools "innosetup\tools\ISCC.exe"),
        (Join-Path $Tools "innosetup\ISCC.exe")
    )
    Get-ChildItem -Path (Join-Path $Tools "innosetup") -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue |
        ForEach-Object { $candidates += $_.FullName }
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    return $null
}

$iscc = Find-Iscc
if (-not $iscc) {
    Write-Step "Descargando compilador Inno Setup (NuGet Tools.InnoSetup)"
    try {
        $info = Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/tools.innosetup/index.json"
        $InnoPkgVersion = @($info.versions)[-1]
    } catch { }
    $nupkgUrl = "https://api.nuget.org/v3-flatcontainer/tools.innosetup/$InnoPkgVersion/tools.innosetup.$InnoPkgVersion.nupkg"
    Write-Host "Paquete: $nupkgUrl"
    $zip = Join-Path $Tools "innosetup.zip"
    Invoke-WebRequest -Uri $nupkgUrl -OutFile $zip -UseBasicParsing
    $extract = Join-Path $Tools "innosetup"
    if (Test-Path $extract) { Remove-Item $extract -Recurse -Force }
    Expand-Archive -Path $zip -DestinationPath $extract -Force
    $iscc = Find-Iscc
}
if (-not $iscc) { throw "No se encontró ISCC.exe (compilador de Inno Setup)." }
Write-Host "ISCC: $iscc"

Write-Step "Compilando instalador"
$iss = Join-Path $PSScriptRoot "wg-rdp.iss"
& $iscc $iss
if ($LASTEXITCODE -ne 0) { throw "Inno Setup falló." }

$setup = Join-Path $Dist "WG-RDP-Setup-$Version.exe"
if (-not (Test-Path $setup)) { throw "No se generó $setup" }

Write-Host "`nInstalador listo:" -ForegroundColor Green
Write-Host "  $setup"
Write-Host ("  {0:N1} MB" -f ((Get-Item $setup).Length / 1MB))
