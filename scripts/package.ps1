<#
.SYNOPSIS
    Compila el plugin en Release, lo empaqueta y actualiza el manifiesto.

.DESCRIPTION
    Produce todo lo necesario para publicar una version:

      dist/Jellyfin.Plugin.ScheduledAccess/   carpeta suelta (instalacion manual)
      dist/scheduled-access_<version>.zip     paquete para GitHub Releases
      manifest.json                           manifiesto del repositorio

    La carpeta del plugin contiene solo la DLL y el meta.json. Deliberadamente
    NO incluye:
      - .pdb          simbolos de depuracion, no van a produccion
      - .xml          documentacion XML, solo util al compilar contra el plugin
      - .deps.json    lo genera el SDK, Jellyfin no lo usa para cargar plugins

    El meta.json se escribe a mano en vez de dejar que Jellyfin lo genere,
    porque el generado deja version "0.0.0.0" y los campos descriptivos vacios.

.PARAMETER Version
    Version del plugin. Debe coincidir con Directory.Build.props y build.yaml.

.PARAMETER TargetAbi
    ABI minima de Jellyfin. Se usa 10.11.0.0 (no 10.11.11.0) para que valga
    en toda la serie 10.11.x en lugar de un unico parche.

.PARAMETER Repo
    Repositorio de GitHub "owner/nombre". De aqui sale la sourceUrl del
    manifiesto, que apunta al zip publicado en Releases.

.PARAMETER Changelog
    Texto del changelog para esta version.
#>
[CmdletBinding()]
param(
    [string]$Version = '1.0.0.0',
    [string]$TargetAbi = '10.11.0.0',
    [string]$Repo = 'bruce-rgb/jellyfin-plugin-scheduled-access',
    [string]$Changelog = 'Version inicial.'
)

$ErrorActionPreference = 'Stop'

# Out-File -Encoding utf8 escribe BOM en Windows PowerShell 5.1, y un JSON con
# BOM rompe a los consumidores: ConvertFrom-Json lo malinterpreta al releerlo, y
# Jellyfin tiene que parsear el meta.json del zip. Se escribe siempre sin BOM.
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)

function Write-JsonFile {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Json
    )
    [System.IO.File]::WriteAllText($Path, $Json, $utf8NoBom)
}

$root         = Split-Path -Parent $PSScriptRoot
$name         = 'Jellyfin.Plugin.ScheduledAccess'
$pluginGuid   = '65e8ae1e-ea44-4b8c-a2c7-16f46a158eb4'
$dist         = Join-Path $root 'dist'
$stage        = Join-Path $dist $name
$zipName      = "scheduled-access_$Version.zip"
$zipPath      = Join-Path $dist $zipName
$manifestPath = Join-Path $root 'manifest.json'

Write-Host "==> Compilando en Release (version $Version)" -ForegroundColor Cyan

# La version se pasa al compilador en vez de depender de Directory.Build.props:
# asi el ensamblado, el meta.json y el manifiesto no pueden desincronizarse al
# publicar una version nueva.
dotnet publish --configuration Release (Join-Path $root "$name.sln") `
    -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version `
    /consoleloggerparameters:NoSummary --nologo
if ($LASTEXITCODE -ne 0) {
    throw "La compilacion fallo con codigo $LASTEXITCODE"
}

Write-Host "==> Preparando $stage" -ForegroundColor Cyan
if (Test-Path $stage) {
    Remove-Item $stage -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item -Force `
    -Path (Join-Path $root "$name/bin/Release/net9.0/publish/$name.dll") `
    -Destination $stage

$meta = [ordered]@{
    category    = 'General'
    changelog   = $Changelog
    description = 'Restringe que contenido ve cada usuario segun el dia de la semana, usando las etiquetas de la biblioteca.'
    guid        = $pluginGuid
    name        = 'Scheduled Access'
    overview    = 'Restricciones de contenido por dia de la semana'
    owner       = 'bruce-rgb'
    targetAbi   = $TargetAbi
    timestamp   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffffff') + 'Z'
    version     = $Version
    status      = 'Active'
    autoUpdate  = $false
    assemblies  = @("$name.dll")
}

Write-JsonFile -Path (Join-Path $stage 'meta.json') -Json ($meta | ConvertTo-Json -Depth 4)

# El zip lleva los archivos en la RAIZ, no dentro de una subcarpeta:
# Jellyfin lo extrae directamente sobre el directorio del plugin.
Write-Host "==> Empaquetando $zipName" -ForegroundColor Cyan
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath

# El servidor valida la descarga contra este hash. Si no coincide con el
# binario publicado, rechaza la instalacion con un error poco claro: es el
# paso mas facil de romper al hacer releases a mano.
$checksum = (Get-FileHash -Path $zipPath -Algorithm MD5).Hash.ToLowerInvariant()
Write-Host "    MD5: $checksum"

Write-Host "==> Actualizando manifest.json" -ForegroundColor Cyan

$entry = [ordered]@{
    version    = $Version
    changelog  = $Changelog
    targetAbi  = $TargetAbi
    sourceUrl  = "https://github.com/$Repo/releases/download/v$Version/$zipName"
    checksum   = $checksum
    timestamp  = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm:ss')
}

if (Test-Path $manifestPath) {
    # Se limpia un BOM heredado por si el archivo lo trae de una version
    # anterior del script: con el delante, ConvertFrom-Json devuelve basura.
    $raw = [System.IO.File]::ReadAllText($manifestPath, $utf8NoBom).TrimStart([char]0xFEFF)

    # Los dos pasos son necesarios, no es rodeo. En Windows PowerShell 5.1
    # ConvertFrom-Json emite el array SIN desenrollar, asi que envolver la
    # llamada directamente -- @(ConvertFrom-Json ...) -- mete la coleccion
    # entera como UN elemento y $manifest[0] acaba siendo un Object[] en vez
    # del objeto del plugin. Asignando primero a variable, @() ya recibe un
    # array y lo deja intacto.
    $parsed = ConvertFrom-Json -InputObject $raw
    $manifest = @($parsed)
} else {
    $manifest = @()
}

$plugin = $manifest | Where-Object { $_.guid -eq $pluginGuid } | Select-Object -First 1

if ($null -eq $plugin) {
    $plugin = [PSCustomObject][ordered]@{
        guid        = $pluginGuid
        name        = 'Scheduled Access'
        description = 'Restringe que contenido ve cada usuario segun el dia de la semana, usando las etiquetas de la biblioteca.'
        overview    = 'Restricciones de contenido por dia de la semana'
        owner       = 'bruce-rgb'
        category    = 'General'
        imageUrl    = ''
        versions    = @()
    }
    $manifest += $plugin
}

# Reemplaza la entrada si esa version ya existia, para que reejecutar el
# script no duplique. Las versiones van de mas nueva a mas antigua.
$others = @($plugin.versions | Where-Object { $_.version -ne $Version })
$plugin.versions = @([PSCustomObject]$entry) + $others

# -Depth 6: el anidamiento es manifest > plugin > versions > entrada.
# Con la profundidad por defecto (2) las versiones saldrian serializadas
# como texto en vez de como objetos.
#
# El array se fuerza con notacion de coma: ConvertTo-Json sobre una coleccion
# de un solo elemento la desenvolveria en un objeto suelto, y el manifiesto
# debe ser un array siempre, incluso con un unico plugin.
Write-JsonFile -Path $manifestPath -Json (ConvertTo-Json -InputObject ([array]$manifest) -Depth 6)

Write-Host ""
Write-Host "Listo." -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize
Write-Host "Manifiesto: $manifestPath"
Write-Host ""
Write-Host "Siguiente paso: publica $zipName en el release v$Version de $Repo" -ForegroundColor Yellow
