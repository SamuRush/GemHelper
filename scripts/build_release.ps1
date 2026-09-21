<#
.SYNOPSIS
    Автоматизация сборки автономного релизного дистрибутива JARVIS (win-x64).

.DESCRIPTION
    Скрипт компилирует проект в режиме self-contained под Windows x64,
    формирует корректную файловую структуру релиза 'dist/JARVIS-win-x64',
    добавляет скрипты первого запуска (QuickStart.bat, Run.bat), инструкции,
    ярлыки зависимостей и упаковывает результат в портативный ZIP-архив с хешем SHA-256.

.PARAMETER Configuration
    Конфигурация сборки (по умолчанию: Release).

.PARAMETER Runtime
    Идентификатор целевой среды (по умолчанию: win-x64).

.PARAMETER OutputDir
    Целевая директория дистрибутива (по умолчанию: <RepoRoot>/dist/JARVIS-win-x64).

.PARAMETER ZipOutput
    Создавать ли портативный ZIP-архив dist/JARVIS-win-x64.zip (по умолчанию: $true).

.PARAMETER SkipClean
    Пропустить очистку предыдущей сборки (по умолчанию: $false).
#>

[CmdletBinding()]
param (
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "",
    [bool]$ZipOutput = $true,
    [bool]$SkipClean = $false,
    [bool]$SelfContained = $true
)

$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = (Get-Item "$ScriptDir\..").FullName
$DistRoot = Join-Path $RepoRoot "dist"

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $DistRoot "JARVIS-win-x64"
}

$ZipPath = Join-Path $DistRoot "JARVIS-win-x64.zip"
$ChecksumsPath = Join-Path $DistRoot "checksums.txt"

Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host "         СБОРКА РЕЛИЗНОГО ДИСТРИБУТИВА JARVIS ($Runtime)" -ForegroundColor Cyan
Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host "Корень репозитория: $RepoRoot" -ForegroundColor Gray
Write-Host "Каталог дистрибутива: $OutputDir" -ForegroundColor Gray
Write-Host "Конфигурация:       $Configuration" -ForegroundColor Gray
Write-Host "Среда выполнения:   $Runtime (Self-Contained: $SelfContained)" -ForegroundColor Gray
Write-Host ""

# 1. Проверка наличия .NET SDK
try {
    $dotnetVersion = & dotnet --version
    Write-Host "[+] Обнаружен .NET SDK версии: $dotnetVersion" -ForegroundColor Green
} catch {
    Write-Error "[!] Ошибка: .NET SDK не найден в PATH. Установите .NET 8 SDK для сборки проекта."
    exit 1
}

# 2. Очистка предыдущей сборки
if (-not $SkipClean) {
    if (Test-Path $OutputDir) {
        Write-Host "[*] Очистка каталога $OutputDir..." -ForegroundColor Yellow
        Remove-Item -Recurse -Force $OutputDir
    }
    if (Test-Path $ZipPath) {
        Write-Host "[*] Удаление предыдущего архива $ZipPath..." -ForegroundColor Yellow
        Remove-Item -Force $ZipPath
    }
}

if (-not (Test-Path $DistRoot)) {
    New-Item -ItemType Directory -Force -Path $DistRoot | Out-Null
}

# 3. Публикация .NET проекта в режиме Self-Contained
Write-Host ""
Write-Host "[1/5] Публикация проекта Gem.csproj (self-contained, win-x64)..." -ForegroundColor Cyan
$projectPath = Join-Path $RepoRoot "Gem.csproj"

$publishArgs = @(
    "publish",
    $projectPath,
    "-c", $Configuration,
    "-r", $Runtime,
    "--self-contained", "$SelfContained",
    "-o", $OutputDir
)

Write-Host "      Команда: dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
& dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "[!] Ошибка во время выполнения dotnet publish (код возврата: $LASTEXITCODE)."
    exit $LASTEXITCODE
}
Write-Host "[+] Проект успешно скомпилирован и опубликован." -ForegroundColor Green

# 4. Формирование структуры дистрибутива
Write-Host ""
Write-Host "[2/5] Формирование структуры дистрибутива..." -ForegroundColor Cyan

# 4.1 Создание JARVIS.exe (удобный алиас для Gem.exe)
$gemExe = Join-Path $OutputDir "Gem.exe"
$jarvisExe = Join-Path $OutputDir "JARVIS.exe"
if (Test-Path $gemExe) {
    Copy-Item $gemExe $jarvisExe -Force
    Write-Host "      [+] Создан ярлык запуска JARVIS.exe" -ForegroundColor Green
}

# 4.2 Копирование QuickStart.bat и Run.bat
Copy-Item (Join-Path $ScriptDir "QuickStart.bat") (Join-Path $OutputDir "QuickStart.bat") -Force
Copy-Item (Join-Path $ScriptDir "Run.bat") (Join-Path $OutputDir "Run.bat") -Force
Write-Host "      [+] Скопированы QuickStart.bat и Run.bat" -ForegroundColor Green

# 4.3 Создание каталога Models/Vosk и копирование инструкций/скриптов
$voskDir = Join-Path $OutputDir "Models\Vosk"
if (-not (Test-Path $voskDir)) {
    New-Item -ItemType Directory -Force -Path $voskDir | Out-Null
}
$srcVoskDir = Join-Path $ScriptDir "Models\Vosk"
if (Test-Path $srcVoskDir) {
    Copy-Item (Join-Path $srcVoskDir "*") $voskDir -Recurse -Force
}
Write-Host "      [+] Сформирован каталог Models/Vosk (CheckModel.bat, README.txt)" -ForegroundColor Green

# 4.4 Создание каталога Models/WakeWord
$wakeWordDir = Join-Path $OutputDir "Models\WakeWord"
if (-not (Test-Path $wakeWordDir)) {
    New-Item -ItemType Directory -Force -Path $wakeWordDir | Out-Null
}
$srcWakeWordDir = Join-Path $ScriptDir "Models\WakeWord"
if (Test-Path $srcWakeWordDir) {
    Copy-Item (Join-Path $srcWakeWordDir "*") $wakeWordDir -Recurse -Force
}
Write-Host "      [+] Сформирован каталог Models/WakeWord" -ForegroundColor Green

# 4.5 Создание каталога Dependencies и копирование ссылок/инструкций
$depDir = Join-Path $OutputDir "Dependencies"
if (-not (Test-Path $depDir)) {
    New-Item -ItemType Directory -Force -Path $depDir | Out-Null
}
$srcDepDir = Join-Path $ScriptDir "Dependencies"
if (Test-Path $srcDepDir) {
    Copy-Item (Join-Path $srcDepDir "*") $depDir -Recurse -Force
}
Write-Host "      [+] Сформирован каталог Dependencies (VC++ Redistributable, Silero SAPI5)" -ForegroundColor Green

# 4.6 Проверка appsettings.json
$appSettingsSrc = Join-Path $RepoRoot "appsettings.json"
$appSettingsDst = Join-Path $OutputDir "appsettings.json"
if (Test-Path $appSettingsSrc) {
    Copy-Item $appSettingsSrc $appSettingsDst -Force
    Write-Host "      [+] Актуализирован appsettings.json" -ForegroundColor Green
}

# 5. Проверка целостности дистрибутива
Write-Host ""
Write-Host "[3/5] Проверка целостности компонентов дистрибутива..." -ForegroundColor Cyan
$requiredFiles = @("Gem.exe", "JARVIS.exe", "QuickStart.bat", "Run.bat", "appsettings.json")
foreach ($req in $requiredFiles) {
    $p = Join-Path $OutputDir $req
    if (-not (Test-Path $p)) {
        Write-Error "[!] Ошибка: Обязательный файл $req отсутствует в $OutputDir!"
        exit 1
    }
}
Write-Host "[+] Все обязательные компоненты присутствуют." -ForegroundColor Green

# 6. Создание ZIP-архива
if ($ZipOutput) {
    Write-Host ""
    Write-Host "[4/5] Упаковка в портативный архив JARVIS-win-x64.zip..." -ForegroundColor Cyan
    
    if (Test-Path $ZipPath) {
        Remove-Item -Force $ZipPath
    }
    
    Compress-Archive -Path "$OutputDir\*" -DestinationPath $ZipPath -CompressionLevel Optimal
    
    $zipItem = Get-Item $ZipPath
    $sizeMb = [math]::Round(($zipItem.Length / 1MB), 2)
    $hashObj = Get-FileHash -Path $ZipPath -Algorithm SHA256
    $hash = $hashObj.Hash
    
    # Сохранение чек-суммы
    "$hash  JARVIS-win-x64.zip" | Out-File -FilePath $ChecksumsPath -Encoding utf8
    
    Write-Host "[+] Портативный архив создан успешно:" -ForegroundColor Green
    Write-Host "    Файл:    $ZipPath" -ForegroundColor White
    Write-Host "    Размер:  $sizeMb МБ" -ForegroundColor White
    Write-Host "    SHA-256: $hash" -ForegroundColor Yellow
} else {
    Write-Host ""
    Write-Host "[4/5] Создание ZIP-архива пропущено по запросу (-ZipOutput `$false)." -ForegroundColor Gray
}

# 7. Итоговая сводка
Write-Host ""
Write-Host "[5/5] Сборка релиза завершена успешно!" -ForegroundColor Green
Write-Host "=====================================================================" -ForegroundColor Cyan
Write-Host "Дистрибутив готов к распространению в: $OutputDir" -ForegroundColor White
if ($ZipOutput) {
    Write-Host "Готовый ZIP для GitHub Releases:       $ZipPath" -ForegroundColor White
}
Write-Host "=====================================================================" -ForegroundColor Cyan
