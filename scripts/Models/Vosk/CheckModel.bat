@echo off
chcp 65001 >nul
cd /d "%~dp0"

echo =====================================================================
echo           Проверка наличия языковой модели Vosk
echo =====================================================================
echo.

set "FOUND=0"
if exist "am" set "FOUND=1"
if exist "final.mdl" set "FOUND=1"
if exist "vosk-model-ru-0.42\am" set "FOUND=1"
if exist "..\..\model\am" set "FOUND=1"
if exist "..\..\model\final.mdl" set "FOUND=1"

if "%FOUND%"=="1" (
    echo [+] Модель Vosk успешно обнаружена и готова к работе!
    echo.
) else (
    echo [!] Модель Vosk не обнаружена.
    echo     Для автоматической загрузки модели запустите 'QuickStart.bat'
    echo     или выполните: ..\..\Gem.exe --download-model
    echo.
)

pause
