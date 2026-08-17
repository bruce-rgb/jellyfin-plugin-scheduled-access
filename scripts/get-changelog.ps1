<#
.SYNOPSIS
    Extrae de CHANGELOG.md la seccion de una version.

.DESCRIPTION
    El texto resultante alimenta dos sitios a la vez: el campo changelog del
    manifiesto -- que es lo que Jellyfin muestra en el historial de versiones
    de la pagina del plugin -- y el cuerpo del release de GitHub.

    Una sola fuente para ambos, escrita a mano cuando se hace el cambio, en
    vez de dos textos generados por separado que acaban diciendo "ver el
    release para los detalles".

.PARAMETER Version
    Version de cuatro partes, tal como aparece en el encabezado y en el tag.

.PARAMETER Path
    Ruta del CHANGELOG.md. Por defecto, el de la raiz del repositorio.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$Path
)

$ErrorActionPreference = 'Stop'

if (-not $Path) {
    $Path = Join-Path (Split-Path -Parent $PSScriptRoot) 'CHANGELOG.md'
}

if (-not (Test-Path $Path)) {
    throw "No se encontro el changelog en $Path"
}

$lines = [System.IO.File]::ReadAllText($Path) -split "\r?\n"

# El encabezado se busca de forma exacta. Fallar aqui es preferible a
# publicar una version con notas de otra, o vacias.
$heading = "## $Version"
$start = -1

for ($i = 0; $i -lt $lines.Length; $i++) {
    if ($lines[$i].Trim() -eq $heading) {
        $start = $i + 1
        break
    }
}

if ($start -lt 0) {
    throw "CHANGELOG.md no tiene una seccion '$heading'. Anadela antes de publicar $Version."
}

$body = New-Object System.Collections.Generic.List[string]

for ($i = $start; $i -lt $lines.Length; $i++) {
    # La siguiente version cierra la seccion.
    if ($lines[$i] -match '^##\s') {
        break
    }

    $body.Add($lines[$i])
}

$text = ($body -join "`n").Trim()

if (-not $text) {
    throw "La seccion '$heading' del changelog esta vacia."
}

$text
