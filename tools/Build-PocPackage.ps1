<#
.SYNOPSIS
    Arma el paquete de la POC SQL -> UA para PI System: publica el
    gateway, limpia la salida (docs/operacion.md, seccion "Armar el
    paquete") y la reemplaza por las versiones especificas de esta POC
    (CSV, appsettings.Local de ejemplo, LEEME). No toca appsettings.json
    del repositorio: solo edita la copia publicada.
.EXAMPLE
    .\tools\Build-PocPackage.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\publish\poc'),
    [string]$ZipDir = (Join-Path $PSScriptRoot '..\dist')
)

$ErrorActionPreference = 'Stop'

# --- Tiene que correr parado en la raiz del repositorio ---
# No alcanza con la ubicacion del script (tools/): dotnet publish y las
# rutas de origen (config/, docs/, tools/package/) se resuelven contra
# el directorio actual, asi que si alguien lo corre desde otro lado hay
# que frenarlo antes de tocar nada.
$repoRoot = (Get-Location).ProviderPath
$gitMarker = Join-Path $repoRoot '.git'
$csprojMarker = Join-Path $repoRoot 'src\Gateway.Host\Gateway.Host.csproj'
if (-not (Test-Path $gitMarker) -or -not (Test-Path $csprojMarker)) {
    throw "Este script tiene que correrse parado en la raiz del repositorio (no se encontro .git y/o src\Gateway.Host\Gateway.Host.csproj bajo '$repoRoot')."
}

$resolvedOutputDir = [System.IO.Path]::Combine($repoRoot, $OutputDir)
$resolvedZipDir = [System.IO.Path]::Combine($repoRoot, $ZipDir)

# --- Borrar la salida anterior ---
# dotnet publish -o no limpia el destino (operacion.md, "Publicar"): un
# publish anterior con otra configuracion sobrevive mezclado y puede
# hacer que el paquete arranque en esta maquina por un archivo que en
# la maquina destino no va a estar.
if (Test-Path $resolvedOutputDir) {
    Remove-Item -Recurse -Force $resolvedOutputDir
}

# --- Publicar ---
# win-x86 porque el ejecutable manda sobre todas las bibliotecas que
# carga (el mismo binario que el paquete de desarrollo, aunque esta POC
# no use DA). Self-contained: la maquina destino no tiene por que tener
# el runtime x86 instalado. Sin PublishTrimmed: el stack de la OPC
# Foundation y el interop COM resuelven tipos por reflection.
dotnet publish (Join-Path $repoRoot 'src\Gateway.Host') -c Release -r win-x86 --self-contained true -o $resolvedOutputDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish fallo (exit code $LASTEXITCODE)."
}

# --- Borrar pki/ y los .pdb ---
# Los certificados se generan solos en el primer arranque; mandar los
# propios arrastra el nombre de host de esta maquina. Los .pdb exponen
# rutas absolutas de disco de desarrollo en los stack traces.
$pkiOut = Join-Path $resolvedOutputDir 'pki'
if (Test-Path $pkiOut) {
    Remove-Item -Recurse -Force $pkiOut
}
Get-ChildItem -Path $resolvedOutputDir -Filter '*.pdb' -Recurse -File |
    Remove-Item -Force

# --- Guarda: appsettings.Local.json no puede viajar en el paquete ---
# Deberia ser imposible (CopyToPublishDirectory=Never, V2-26), pero si
# alguna vez aparece es porque alguien pisó esa configuracion sin
# querer, y ese archivo puede tener credenciales reales de un desarrollo
# local. Frenar en seco antes de armar el zip.
$localSettingsOut = Join-Path $resolvedOutputDir 'appsettings.Local.json'
if (Test-Path $localSettingsOut) {
    throw "SEGURIDAD: '$localSettingsOut' aparecio en la salida publicada. Aborta: no se empaqueta appsettings.Local.json (puede tener credenciales reales)."
}

# --- CSV de la POC ---
$configOutDir = Join-Path $resolvedOutputDir 'config'
New-Item -ItemType Directory -Force -Path $configOutDir | Out-Null
Copy-Item (Join-Path $repoRoot 'config\poc-sql.example.csv') `
    (Join-Path $configOutDir 'poc-sql.example.csv') -Force

# --- Plantilla de appsettings.Local para la POC, en vez de la de desarrollo ---
Copy-Item (Join-Path $repoRoot 'tools\package\appsettings.Local.poc.example.json') `
    (Join-Path $resolvedOutputDir 'appsettings.Local.example.json') -Force

# --- LEEME de la POC, en la raiz de la salida ---
# Prefijo 00- para que aparezca primero en el Explorador; el archivo del
# repositorio no se renombra, solo la copia publicada.
Copy-Item (Join-Path $repoRoot 'docs\LEEME-POC.txt') `
    (Join-Path $resolvedOutputDir '00-LEEME-POC.txt') -Force

# --- Fijar Ua:TagsCsvPath en el appsettings.json DE LA SALIDA ---
# Nunca el del repositorio: se edita la copia publicada, despues de
# copiarla dotnet publish.
$appsettingsOut = Join-Path $resolvedOutputDir 'appsettings.json'
$settings = Get-Content $appsettingsOut -Raw | ConvertFrom-Json
$settings.Ua.TagsCsvPath = 'config/poc-sql.example.csv'
$updatedJson = $settings | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($appsettingsOut, $updatedJson, [System.Text.UTF8Encoding]::new($false))

# Verificar que el JSON reescrito sigue siendo valido antes de seguir.
try {
    Get-Content $appsettingsOut -Raw | ConvertFrom-Json | Out-Null
}
catch {
    throw "El appsettings.json de la salida quedo invalido despues de fijar Ua:TagsCsvPath: $_"
}

# --- Zip, con el hash corto de git, fuera de la carpeta publicada ---
$gitHash = (git rev-parse --short HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitHash)) {
    throw "No se pudo obtener el hash de git (git rev-parse --short HEAD)."
}
$isDirty = [bool](git status --porcelain)
$dirtySuffix = if ($isDirty) { '-dirty' } else { '' }
$zipName = "gateway-poc-$gitHash$dirtySuffix.zip"

New-Item -ItemType Directory -Force -Path $resolvedZipDir | Out-Null
$zipPath = Join-Path $resolvedZipDir $zipName
if (Test-Path $zipPath) {
    Remove-Item -Force $zipPath
}
Compress-Archive -Path (Join-Path $resolvedOutputDir '*') -DestinationPath $zipPath -Force

# --- Reporte final ---
$zipSizeMb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
$topLevelNonDll = Get-ChildItem -Path $resolvedOutputDir |
    Where-Object { $_.Extension -ne '.dll' } |
    Select-Object -ExpandProperty Name

Write-Host ''
Write-Host "Paquete: $zipPath"
Write-Host "Tamano: $zipSizeMb MB"
Write-Host 'Archivos de primer nivel (sin contar .dll):'
$topLevelNonDll | ForEach-Object { Write-Host "  $_" }
