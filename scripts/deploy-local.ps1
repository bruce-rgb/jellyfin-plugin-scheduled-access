<#
.SYNOPSIS
    Compila en Debug y despliega el plugin en el Jellyfin local de desarrollo.

.DESCRIPTION
    Pensado para iterar mientras se desarrolla, no para publicar: usa Debug e
    incluye el .pdb para poder poner breakpoints. Para releases usa package.ps1.

    Tres cosas que hace y que son necesarias aunque no lo parezcan:

    1. Escribe un meta.json con la version REAL del ensamblado recien compilado.
       Jellyfin registra el plugin por la version del manifiesto, pero el panel
       muestra y envia la del ensamblado. Si divergen, desinstalar o actualizar
       desde el panel devuelve 404: busca una version que no tiene registrada.
       Es un fallo silencioso, porque el plugin carga con normalidad.

    2. Detiene el servicio antes de copiar. Jellyfin mantiene la DLL bloqueada
       mientras corre y la copia falla con IOException.

    3. Concede permisos a NT AUTHORITY\NETWORK SERVICE sobre la carpeta. Por la
       regla CREATOR OWNER de Windows la carpeta queda en poder de quien la
       crea -- tu -- y el servicio solo hereda lectura, asi que no puede borrar
       sus propios archivos al desinstalar.

    Parar el servicio y ajustar ACL exigen privilegios de administrador: el
    script agrupa esas operaciones en UNA sola elevacion para no encadenar
    varios avisos de UAC.

.PARAMETER DataDir
    Carpeta de datos del servidor. Por defecto la de una instalacion como
    servicio de Windows; para instalacion de usuario seria $env:LOCALAPPDATA\jellyfin.

.PARAMETER Configuration
    Configuracion de compilacion. Debug por defecto para poder depurar.
#>
[CmdletBinding()]
param(
    [string]$DataDir = "$env:ProgramData\Jellyfin\Server",
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'

$root  = Split-Path -Parent $PSScriptRoot
$name  = 'Jellyfin.Plugin.ScheduledAccess'
$guid  = '65e8ae1e-ea44-4b8c-a2c7-16f46a158eb4'
$build = Join-Path $root "$name/bin/$Configuration/net9.0/publish"
$dest  = Join-Path $DataDir "plugins/$name"

Write-Host "==> Compilando ($Configuration)" -ForegroundColor Cyan
dotnet publish --configuration $Configuration (Join-Path $root "$name.sln") `
    /consoleloggerparameters:NoSummary --nologo
if ($LASTEXITCODE -ne 0) {
    throw "La compilacion fallo con codigo $LASTEXITCODE"
}

# La version sale del ensamblado recien construido, no de un valor escrito a
# mano: es la unica forma de garantizar que manifiesto y DLL no divergan.
$dll = Join-Path $build "$name.dll"
$version = [System.Reflection.AssemblyName]::GetAssemblyName($dll).Version.ToString()
Write-Host "    Version del ensamblado: $version"

$meta = [ordered]@{
    category    = 'General'
    changelog   = ''
    description = 'Restringe que contenido ve cada usuario segun el dia de la semana, usando las etiquetas de la biblioteca.'
    guid        = $guid
    name        = 'Scheduled Access'
    overview    = 'Restricciones de contenido por dia de la semana'
    owner       = 'bruce-rgb'
    targetAbi   = '10.11.0.0'
    timestamp   = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffffff') + 'Z'
    version     = $version
    status      = 'Active'
    autoUpdate  = $false
    assemblies  = @("$name.dll")
}

# Sin BOM: Out-File -Encoding utf8 lo añade en Windows PowerShell 5.1 y
# Jellyfin tiene que parsear este archivo.
$metaPath = Join-Path $build 'meta.json'
[System.IO.File]::WriteAllText(
    $metaPath,
    ($meta | ConvertTo-Json -Depth 4),
    [System.Text.UTF8Encoding]::new($false))

Write-Host "==> Desplegando en $dest" -ForegroundColor Cyan
Write-Host "    (requiere elevacion: acepta el aviso de UAC)" -ForegroundColor Yellow

$inner = @"
New-Item -ItemType Directory -Force -Path '$dest' | Out-Null
Stop-Service JellyfinServer
Start-Sleep -Seconds 3
Copy-Item -Force -Path (Join-Path '$build' '*') -Destination '$dest'
icacls '$dest' /grant 'NT AUTHORITY\NETWORK SERVICE:(OI)(CI)F' /T | Out-Null
Start-Service JellyfinServer
"@

Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile', '-Command', $inner -Wait

Write-Host ""
Write-Host "Servicio: $((Get-Service JellyfinServer).Status)" -ForegroundColor Green
Write-Host "Desplegada la version $version"
