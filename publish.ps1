# publish.ps1
# Собирает лаунчер, генерирует installer и manifest.json для GitHub Releases.
#
# Использование:
#   .\publish.ps1 -BaseUrl "https://github.com/USER/REPO/releases/latest/download"
#
# Требования:
#   Inno Setup 6 — https://jrsoftware.org/isdl.php
#   iscc.exe должен быть в PATH или по пути C:\Program Files (x86)\Inno Setup 6\ISCC.exe
#
# Результат:
#   dist/
#     Aquila.exe          — bootstrap exe (внутри installer)
#     AquilaSetup.exe     — installer (публикуется как Release asset)
#     assets/             — фон, иконки (публикуются как Release assets)
#     manifest.json       — манифест обновлений (публикуется как Release asset)

param(
    [Parameter(Mandatory)]
    [string]$BaseUrl,

    [string]$Configuration = "Release",
    [string]$OutDir        = "dist"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Утилиты ──────────────────────────────────────────────────────────────────

function Get-FileSha256([string]$Path) {
    (Get-FileHash -Path $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function New-ManifestFile([string]$Type, [string]$RelPath, [string]$AbsPath, [string]$Url) {
    [ordered]@{
        type = $Type
        path = $RelPath
        hash = (Get-FileSha256 $AbsPath)
        size = (Get-Item $AbsPath).Length
        url  = $Url
    }
}

# ── Очистка ───────────────────────────────────────────────────────────────────

Write-Host "==> Очистка $OutDir..." -ForegroundColor Cyan
if (Test-Path $OutDir) { Remove-Item -Recurse -Force $OutDir }
New-Item -ItemType Directory -Path $OutDir | Out-Null
New-Item -ItemType Directory -Path "$OutDir/assets" | Out-Null
New-Item -ItemType Directory -Path "$OutDir/servers" | Out-Null
New-Item -ItemType Directory -Path "$OutDir/configs" | Out-Null

# ── Сборка Shell ──────────────────────────────────────────────────────────────

Write-Host "==> Сборка Shell..." -ForegroundColor Cyan
dotnet publish Shell/Shell.csproj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -o "$OutDir/_shell_tmp"

$shellExe = Get-ChildItem "$OutDir/_shell_tmp" -Filter "*.exe" | Select-Object -First 1
if (-not $shellExe) { throw "Shell exe не найден после публикации" }
Copy-Item $shellExe.FullName "$OutDir/Aquila.exe"
Remove-Item -Recurse -Force "$OutDir/_shell_tmp"

# ── Сборка Installer (Inno Setup) ─────────────────────────────────────────────

Write-Host "==> Сборка installer..." -ForegroundColor Cyan

# update.json нужен внутри installer — копируем из configs/
Copy-Item "configs\update.json" "$OutDir\configs\update.json" -Force

$iscc = "iscc.exe"
if (-not (Get-Command $iscc -ErrorAction SilentlyContinue)) {
    $iscc = "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
}
if (-not (Test-Path $iscc)) {
    throw "Inno Setup не найден. Установите с https://jrsoftware.org/isdl.php и добавьте в PATH."
}

& $iscc "installer\aquila.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup завершился с ошибкой $LASTEXITCODE" }

$installerPath = "$OutDir/AquilaSetup.exe"
if (-not (Test-Path $installerPath)) { throw "AquilaSetup.exe не найден после сборки installer" }

# ── Копируем assets ───────────────────────────────────────────────────────────

Write-Host "==> Копирование assets..." -ForegroundColor Cyan
$assetFiles = @()
$srcAssets = "assets"
if (Test-Path $srcAssets) {
    Get-ChildItem $srcAssets -File | Where-Object {
        $_.Extension -in @(".jpg", ".jpeg", ".png", ".ico", ".gif", ".webp")
    } | ForEach-Object {
        Copy-Item $_.FullName "$OutDir/assets/$($_.Name)"
        $assetFiles += $_
    }
}

# ── Генерация manifest.json ───────────────────────────────────────────────────

Write-Host "==> Генерация manifest.json..." -ForegroundColor Cyan

$version = (Get-Date).ToString("yyyy.MM.dd.HHmm")
$files   = [System.Collections.Generic.List[object]]::new()

# AquilaSetup.exe — тип installer (при обновлении запускается тихо /VERYSILENT)
# Хеш считается от Aquila.exe внутри, чтобы сравнивать с кешем на клиенте
$exeHash = Get-FileSha256 "$OutDir/Aquila.exe"
$setupSize = (Get-Item $installerPath).Length
$files.Add([ordered]@{
    type = "installer"
    path = "AquilaSetup.exe"
    hash = $exeHash          # хеш Aquila.exe — клиент сравнивает с exe.hash кешем
    size = $setupSize
    url  = "$BaseUrl/AquilaSetup.exe"
})

# Assets
foreach ($f in $assetFiles) {
    $files.Add((New-ManifestFile "asset" $f.Name "$OutDir/assets/$($f.Name)" "$BaseUrl/$($f.Name)"))
}

# Конфиги из configs/
foreach ($cfg in @("auth.json", "ui.json", "update.json")) {
    $cfgPath = "configs/$cfg"
    if (Test-Path $cfgPath) {
        Copy-Item $cfgPath "$OutDir/configs/$cfg"
        $files.Add((New-ManifestFile "config" $cfg "$OutDir/configs/$cfg" "$BaseUrl/configs/$cfg"))
    }
}

# Серверы из configs/
$serversPath = "configs/servers.json"
if (Test-Path $serversPath) {
    Copy-Item $serversPath "$OutDir/configs/servers.json"
    $files.Add((New-ManifestFile "config" "servers.json" "$OutDir/configs/servers.json" "$BaseUrl/configs/servers.json"))
}

$manifest = [ordered]@{ version = $version; cleanup = @(); files = $files }
$manifest | ConvertTo-Json -Depth 10 | Set-Content "$OutDir/manifest.json" -Encoding UTF8

# ── Упаковка installer в ZIP ──────────────────────────────────────────────────
# Файлы извлечённые из ZIP не получают метку Zone.Identifier (Mark-of-the-Web),
# поэтому Windows Smart App Control не блокирует их при запуске.
# Пользователи должны скачивать AquilaSetup.zip, а не exe напрямую.

Write-Host "==> Упаковка installer в ZIP..." -ForegroundColor Cyan
$zipPath = "$OutDir/AquilaSetup.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }
# Копируем bat-файл в dist чтобы упаковать вместе с exe
Copy-Item "installer\run_setup.bat" "$OutDir\run_setup.bat" -Force
Compress-Archive -Path @($installerPath, "$OutDir\run_setup.bat") -DestinationPath $zipPath
Write-Host "    AquilaSetup.zip готов: $([math]::Round((Get-Item $zipPath).Length/1MB,1)) MB"
Write-Host "    ZIP содержит: AquilaSetup.exe + run_setup.bat"

# ── Итог ──────────────────────────────────────────────────────────────────────

Write-Host ""
Write-Host "==> Готово! Файлы для публикации в $OutDir/:" -ForegroundColor Green
Get-ChildItem $OutDir -Recurse -File | ForEach-Object {
    $rel  = $_.FullName.Substring((Resolve-Path $OutDir).Path.Length + 1)
    $size = "{0:N0} KB" -f ($_.Length / 1024)
    Write-Host "    $rel  ($size)"
}
Write-Host ""
Write-Host "Загрузите все файлы из $OutDir/ как assets в GitHub Release." -ForegroundColor Yellow
Write-Host "Для скачивания пользователями публикуйте AquilaSetup.zip (не .exe напрямую)." -ForegroundColor Cyan
Write-Host "ZIP обходит блокировку Windows Smart App Control без подписи кода." -ForegroundColor Cyan
Write-Host "Manifest URL: $BaseUrl/manifest.json" -ForegroundColor White
