<#
.SYNOPSIS
    Compila el plugin en Release y prepara la carpeta lista para desplegar.

.DESCRIPTION
    Genera dist/Jellyfin.Plugin.ScheduledAccess/ con solo lo que el servidor
    necesita: la DLL y el meta.json.

    Deliberadamente NO incluye:
      - .pdb   simbolos de depuracion, no van a produccion
      - .xml   documentacion XML, solo util al compilar contra el plugin
      - .deps.json  lo genera el SDK, Jellyfin no lo usa para cargar plugins

    El meta.json se escribe a mano en vez de dejar que Jellyfin lo genere,
    porque el generado deja version "0.0.0.0" y los campos descriptivos vacios.

.PARAMETER Version
    Version del plugin. Debe coincidir con Directory.Build.props y build.yaml.

.PARAMETER TargetAbi
    ABI minima de Jellyfin. Se usa 10.11.0.0 (no 10.11.11.0) para que valga
    en toda la serie 10.11.x en lugar de un unico parche.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0.0',
    [string]$TargetAbi = '10.11.0.0'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$name = 'Jellyfin.Plugin.ScheduledAccess'
$dist = Join-Path $root "dist/$name"

Write-Host "==> Compilando en Release" -ForegroundColor Cyan
dotnet publish --configuration Release (Join-Path $root "$name.sln") `
    /consoleloggerparameters:NoSummary --nologo
if ($LASTEXITCODE -ne 0) {
    throw "La compilacion fallo con codigo $LASTEXITCODE"
}

Write-Host "==> Preparando $dist" -ForegroundColor Cyan
if (Test-Path $dist) {
    Remove-Item $dist -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Copy-Item -Force `
    -Path (Join-Path $root "$name/bin/Release/net9.0/publish/$name.dll") `
    -Destination $dist

$meta = [ordered]@{
    category    = 'General'
    changelog   = 'Version inicial.'
    description = 'Restringe que contenido ve cada usuario segun el dia de la semana, usando las etiquetas de la biblioteca.'
    guid        = '65e8ae1e-ea44-4b8c-a2c7-16f46a158eb4'
    name        = 'Scheduled Access'
    overview    = 'Restricciones de contenido por dia de la semana'
    owner       = 'brucers1234'
    targetAbi   = $TargetAbi
    timestamp   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffffff') + 'Z'
    version     = $Version
    status      = 'Active'
    autoUpdate  = $false
    assemblies  = @("$name.dll")
}

$meta | ConvertTo-Json -Depth 4 |
    Out-File -FilePath (Join-Path $dist 'meta.json') -Encoding utf8

Write-Host ""
Write-Host "Listo: $dist" -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize
