@echo off
chcp 65001 >nul
title JARVIS / GemHelper — Quick Start
cd /d "%~dp0"

echo =====================================================================
echo           JARVIS / GemHelper — Голосовой ассистент
echo =====================================================================
echo.

:: 1. Проверка архитектуры ОС (требуется Windows x64)
if /i "%PROCESSOR_ARCHITECTURE%" neq "AMD64" if /i "%PROCESSOR_ARCHITEW6432%" neq "AMD64" (
    echo [!] ОШИБКА: JARVIS поддерживает только 64-битную операционную систему Windows (x64).
    echo.
    pause
    exit /b 1
)

:: 2. Определение исполняемого файла
set "EXE_NAME=Gem.exe"
if exist "JARVIS.exe" set "EXE_NAME=JARVIS.exe"

if not exist "%EXE_NAME%" (
    echo [!] ОШИБКА: Исполняемый файл приложения не найден в текущей папке!
    echo     Ожидался файл 'Gem.exe' или 'JARVIS.exe'.
    echo.
    pause
    exit /b 1
)

:: 3. Проверка наличия языковой модели Vosk
set "MODEL_FOUND=0"
if exist "model\am" set "MODEL_FOUND=1"
if exist "model\final.mdl" set "MODEL_FOUND=1"
if exist "model\conf" set "MODEL_FOUND=1"
if exist "Models\Vosk\am" set "MODEL_FOUND=1"
if exist "Models\Vosk\final.mdl" set "MODEL_FOUND=1"
if exist "Models\Vosk\conf" set "MODEL_FOUND=1"
if exist "Models\Vosk\vosk-model-ru-0.42\am" set "MODEL_FOUND=1"

if "%MODEL_FOUND%"=="0" (
    echo [i] ВНИМАНИЕ: Полноразмерная языковая модель Vosk (vosk-model-ru-0.42) не обнаружена.
    echo     Без неё распознавание команд голосом будет ожидать загрузки модели.
    echo.
    echo     Выберите действие:
    echo       [1] Скачать русскую модель Vosk (~1.5 ГБ) автоматически прямо сейчас
    echo       [2] Запустить ассистент без модели (HUD, команды и текстовый ввод доступны)
    echo       [3] Открыть текстовую справку по установке модели
    echo.
    set /p "USER_CHOICE=    Ваш выбор (1/2/3, по умолчанию 1): "
    if "%USER_CHOICE%"=="" set "USER_CHOICE=1"
    if "%USER_CHOICE%"=="1" (
        echo.
        echo [+] Запуск встроенной загрузки модели...
        "%EXE_NAME%" --download-model
        echo.
    )
    if "%USER_CHOICE%"=="3" (
        if exist "Models\Vosk\README.txt" start notepad "Models\Vosk\README.txt"
    )
)

:: 4. Информация перед запуском
echo.
echo [+] Запуск JARVIS (%EXE_NAME%)...
echo     - Голосовой синтез (TTS): Edge Neural Dmitry (онлайн) / SAPI5 Aidar/Pavel (офлайн)
echo     - Для работы сложных ответов LLM рекомендуется запустить LM Studio (http://127.0.0.1:1234)
echo.

:: 5. Запуск приложения с пробросом параметров
"%EXE_NAME%" %*
set "EXIT_CODE=%ERRORLEVEL%"

if %EXIT_CODE% neq 0 (
    echo.
    echo [!] Приложение завершилось с кодом: %EXIT_CODE%
    echo     Если возникла ошибка нехватки библиотек C++, установите VC++ Redistributable
    echo     из папки 'Dependencies\vc_redist.x64.url'.
    echo.
    pause
)

exit /b %EXIT_CODE%
