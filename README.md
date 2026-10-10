# Gem — Высокопроизводительный HUD-оверлей и голосовой ассистент

> Модульный киберпанк HUD-оверлей с голосовым управлением для Windows.  
> Локальный LLM-роутер команд, WASAPI Loopback спектроанализатор, процедурная физика осколков, Zero-GC рендеринг на DirectX.

![Platform](https://img.shields.io/badge/Platform-Windows_10%2F11_x64-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![Language](https://img.shields.io/badge/Language-C%23-green)
![Rendering](https://img.shields.io/badge/Rendering-DrawingVisual_%2F_DirectX-cyan)
![Audio](https://img.shields.io/badge/Audio-WASAPI_Loopback_%2F_NAudio-orange)

---

## ✨ Ключевые возможности

### 🎛 Киберпанк Overdrive HUD (Arc Reactor)
- **Радиальный FFT-спектрограф** — 128 билатеральных лучей/лепестков, честный WASAPI Loopback захват, окно Ханнинга, 64 частотных полосы, гравитационный Peak Falloff (0.88/кадр).
- **Детектор транзиентов баса (60–150 Гц)** — IIR Butterworth LPF 2-го порядка, кольцевой буфер истории (~1 сек, 43 слота), адаптивный порог `1.35 × avg + 0.008`, cooldown 280 мс.
- **Overdrive: раскол ядра на створки** — экспоненциальный заряд шкалы Arc Gauge за 2.5–3.5 с непрерывного бита; читаемый киберпанк-титр «OVERRIDE!» с хроматической аберрацией (Cyan/Crimson).
- **Физический движок осколков** — пул `struct ShardParticle[350]`, крейсерский дрейф ≥ 160 px/с, импульс от каждого kick-транзиента, отталкивание курсором мыши (радиус 110 px), AABB-коллизии с реальными окнами и 4 кромками экрана.
- **JIT-трекинг окон** — `EnumWindows` + `DwmGetWindowAttribute(DWMWA_CLOAKED)`, многоуровневая фильтрация cloaked/фантомных/служебных окон; опрос только при заряде > 50%.
- **Кинематографичный гипер-коллапс** — 5-фазовая хореография: Anticipation → Hyper Slam (EaseInExpo) → Core Whiteout → Shockwave Blast (80→370 px) → Heavy Settle (затухающие пружинные колебания, остывание в `#00E5FF`).
- **Screen Glass Crack** — процедурная паутина растрескивания на базе `StreamGeometry` с экранной тряской (Screen Shake).
- **Cyberpunk Memory Glitch Decryptor** — агрессивные глитч-дампы системной памяти (`MEM_CORRUPTION_DETECTED`, `FATAL_CORE_BREACH`…) и бегущий код поверх окон; спавн ≥ 250 px от центра ядра; Zero-GC рендеринг через `DrawingContext.DrawText`.
- **5 орбитальных колец** — асинхронное гироскопическое вращение (±8–42°/с), ускорение привязано к энергии суб-баса.

### ⚡ Производительность и Zero-GC
- Рендеринг через `CompositionTarget.Rendering` — стабильные **60 FPS** с аппаратным ускорением DirectX.
- Строгая Zero-Allocation политика: предвыделенные `struct`-пулы, `StreamGeometry.Freeze()`, кэшированные `Pen`/`Brush`, переиспользуемый `StringBuilder`.
- Предкомпилированные замороженные `SpectrumGradientPalettes` (64 уровня) — O(1) без heap-аллокаций.
- Сквозные клики мыши без задержек: `WM_NCHITTEST → HTTRANSPARENT` через Win32 P/Invoke.

### 🎙 Голосовой пайплайн
- **Wake-Word < 80 мс** — `VoskGrammarWakeWordDetector` (малая модель ~45 МБ, UTF-8 P/Invoke `vosk_recognizer_new_grm`) по умолчанию; опционально `OpenWakeWordDetector` (ONNX Runtime, `jarvis.onnx`).
- **RMS Noise Gate** + порог минимальной длительности 150 мс: исключает ложные срабатывания на вздохи, кашель, щелчки.
- **STT Vosk ru-0.42** (~1.5 ГБ), синглтон `_sharedModel`, таймаут паузы 700 мс, RAM < 2 ГБ.
- **Acoustic Echo Suppression** — Processing Lock + блокировка фреймов во время TTS + cooldown 250 мс в `finally`.

### 🤖 Локальный роутер команд
- **Двухуровневый парсинг**: `TryFastMatch` (< 5 мс, строгий `Regex.Compiled`, без сети) → локальная LLM fallback (LM Studio / Qwen, OpenAI-compatible API).
- FSM-диалоги подтверждений (`PendingActionState`, 10 с таймаут, bypass wake-word через `EnterConfirmationListening`).

### 🗣 Отказоустойчивый TTS
- **Primary**: Edge Neural TTS WebSocket (`ru-RU-SvetlanaNeural` / `ru-RU-DmitryNeural`), таймаут подключения 1500 мс, жёсткий таймаут первого чанка 2000 мс, Fast Failover ≤ 1.6 с.
- **Offline/Fallback**: Windows SAPI5, прямой COM-биндинг токенов Silero (Aidar / Baya / Kseniya), автосброс на Microsoft Pavel/Irina при отсутствии Silero.

---

## 🗂 Структура решения

```
Gem/
├── Gem.sln                          # Решение: основной проект + Gem.Tests
├── Gem.csproj                       # .NET 8 Windows (WPF, NAudio, Vosk, ONNX)
├── Program.cs                       # Точка входа: DI, роутер, WPF App + VoiceListener
├── GlobalUsings.cs                  # Глобальные using + InternalsVisibleTo
├── appsettings.json                 # Конфигурация: LLM, TTS, STT, WakeWord, Audio
├── HudSettings.json                 # Настройки HUD (Gain, BassScale, тема, Overdrive)
│
├── UI/                              # Рендеринг HUD и окно настроек
│   ├── ArcReactorVisualHost.cs      # Хост DrawingVisual: оркестрация слоёв рендеринга
│   ├── AudioSpectrumAnalyzer.cs     # FFT-анализ: 64 полосы, Hanning, билатеральная корона
│   ├── BeatDetector.cs              # Детектор транзиентов баса (IIR LPF + адаптивный порог)
│   ├── ButterworthBassFilter.cs     # IIR Butterworth 2-го порядка (fc = 150 Гц)
│   ├── FftProcessor.cs              # NAudio FastFourierTransform обёртка
│   ├── HudPalettes.cs               # Замороженные SpectrumGradientPalettes (64 уровня, O(1))
│   ├── HudRenderer.cs               # Корневой рендерер: кольца, ядро, спектр
│   ├── HudRenderer.Geometries.cs    # Предгенерация и заморозка StreamGeometry-ресурсов
│   ├── HudRenderer.Overdrive.cs     # Рендер фазы Overdrive: шкала Arc Gauge, титр OVERRIDE!
│   ├── HudSettings.cs               # DTO настроек HUD + HudSettingsStorage (JSON)
│   ├── OverdriveParticleOverlayWindow.cs  # Полноэкранный WPF-оверлей физики осколков
│   ├── OverdriveStateMachine.cs     # FSM Overdrive: Charge → Active → Reassembly → Cooldown
│   ├── OverlayWidget.xaml/cs        # Главный WPF HUD-виджет
│   ├── OverlayWidget.Effects.cs     # Screen Shake, Screen Glass Crack, Shockwave
│   ├── ParticleEngine.cs            # Физический движок осколков (пул struct ShardParticle[350])
│   ├── ParticleEngine.Glitch.cs     # Cyberpunk Memory Glitch Decryptor
│   ├── ParticleEngine.Physics.cs    # AABB-коллизии, Mouse Repulsion, Window Bounce
│   ├── ParticleModels.cs            # Структуры ShardParticle, WindowAabb, GlitchCodeBlock
│   ├── SettingsModels.cs            # Модели настроек для SettingsWindow
│   ├── SettingsWindow.xaml/cs       # Sci-Fi TabControl настроек
│   ├── SettingsWindow.Audio.cs      # Вкладка: микрофон, VU-метр, RMS Noise Gate
│   ├── SettingsWindow.Hud.cs        # Вкладка: Gain, BassScale, Falloff, тема, Overdrive
│   ├── SettingsWindow.Steam.cs      # Вкладка: алиасы игр, список wake-word
│   ├── SettingsWindow.VoiceAi.cs    # Вкладка: LLM, TTS движок, голоса
│   ├── WasapiLoopbackCaptureService.cs  # WASAPI Loopback поток для спектроанализатора
│   └── WindowBoundsTracker.cs       # JIT-трекинг видимых окон (EnumWindows + DWM)
│
├── Voice/                           # Захват звука и Wake-Word детектор
│   ├── AudioEnergyGate.cs           # RMS Noise Gate (порог настраивается)
│   ├── MicrophoneAudioRecorder.cs   # NAudio WaveInEvent (16 кГц, 16 бит, моно)
│   ├── SpeechPhraseBuffer.cs        # Буферизация фразы с таймаутом паузы 700 мс
│   ├── VoskModelHelper.cs           # Проверка и автозагрузка модели Vosk
│   ├── VoskModelManager.cs          # Синглтон модели Vosk (_sharedModel, RAM < 2 ГБ)
│   └── WakeWord/
│       ├── IWakeWordDetector.cs     # Контракт потокового детектора активации
│       ├── OpenWakeWordDetector.cs  # ONNX Runtime (jarvis.onnx, < 80 мс)
│       ├── VoskGrammarWakeWordDetector.cs  # KWS по умолчанию (vosk-small + UTF-8 грамматика)
│       └── WakeWordFactory.cs       # Фабрика выбора детектора из appsettings.json
│
├── Services/                        # Бизнес-логика и сервисы
│   ├── VoiceListener.cs             # FSM захвата речи (WaitingForWakeWord → ListeningForCommand)
│   ├── VoiceListener.Processing.cs  # Processing Lock, Acoustic Echo Suppression
│   ├── LlmIntentService.cs          # Dual-Path Router: TryFastMatch + LLM fallback
│   ├── JarvisOrchestrator.cs        # FSM подтверждений (PendingActionState, 10 с таймаут)
│   ├── CompositeVoiceFeedbackService.cs  # TTS оркестратор: Edge → SAPI5 fallback
│   ├── VoiceFeedbackService.cs      # Мост обратной совместимости
│   ├── MediaSessionService.cs       # GSMTC (WinRT) + управление видео/музыкой
│   ├── AppAudioService.cs           # WASAPI SimpleAudioVolume (изолированная громкость)
│   ├── SteamService.cs              # Steam: индексация VDF/ACF + нечёткий поиск Levenshtein
│   ├── TimerService.cs              # Потокобезопасные таймеры (ConcurrentDictionary + Task.Delay)
│   ├── AppHandler.cs                # Централизованный исполнитель HandleJarvisResponseAsync
│   ├── WeatherService.cs            # Open-Meteo API + геокодинг городов
│   ├── AppSettingsService.cs        # Загрузка/сохранение appsettings.json
│   ├── ServiceCollectionExtensions.cs  # DI модули: AddJarvisCore, AddJarvisHandlers, AddJarvisTts
│   ├── Routing/                     # Модули TryFastMatch (по категориям)
│   │   ├── MediaFastMatcher.cs      # Медиа-команды (< 5 мс)
│   │   ├── VolumeFastMatcher.cs     # Команды громкости
│   │   ├── TimerFastMatcher.cs      # Таймеры и напоминания
│   │   ├── AppFastMatcher.cs        # Системные приложения
│   │   ├── SystemFastMatcher.cs     # Системные команды
│   │   ├── RoutingHelper.cs         # Утилиты нормализации текста
│   │   └── LlmSystemPrompt.cs       # Системный промпт LLM + история диалога
│   ├── Audio/                       # Вспомогательные аудио-сервисы
│   │   ├── AudioSessionFinder.cs    # Поиск WASAPI-сессии по PID/имени
│   │   ├── ElectronWindowController.cs  # Ctrl+Up/Down для Electron-плееров
│   │   └── ProcessFamilyTreeResolver.cs  # Дерево дочерних PID (Toolhelp32)
│   ├── Media/                       # Медиа-контроллеры
│   │   ├── GsmtcMediaController.cs  # Управление через Windows.Media.Control (WinRT)
│   │   ├── VideoWindowFinder.cs     # Поиск активного окна видеоплеера
│   │   └── YouTubeLauncher.cs       # Запуск YouTube через UI Automation
│   ├── Steam/                       # Steam-автоматизация (вынесена в подмодули)
│   │   ├── SteamLibraryIndexer.cs   # Индексация VDF/ACF манифестов
│   │   ├── SteamInstaller.cs        # Установка/удаление через UI Automation
│   │   ├── SteamPhonetics.cs        # Транслитерация En↔Ru + Levenshtein
│   │   ├── SteamUiAutomation.cs     # AutomationElement: поиск кнопок Steam DPI-независимо
│   │   ├── SteamModels.cs           # DTO (GameEntry, SteamSearchResult)
│   │   ├── SteamService.Aliases.cs  # Словарь пользовательских алиасов
│   │   └── SteamService.Search.cs   # Нечёткий поиск + пороги уверенности
│   ├── App/
│   │   ├── AppCatalog.cs            # Реестр системных приложений (browser, calc, explorer…)
│   │   └── JarvisResponseDispatcher.cs  # Диспетчер ответов ассистента
│   ├── TTS/
│   │   ├── IVoiceFeedbackService.cs # Контракт TTS (SpeakAsync, события, движки)
│   │   ├── ITtsEngine.cs            # Базовый контракт движка
│   │   ├── EdgeTtsEngine.cs         # Edge Neural WebSocket (1500/2000 мс таймауты, Keep-Alive)
│   │   └── SystemSpeechTtsEngine.cs # SAPI5 COM-биндинг (Silero + Microsoft Pavel/Irina)
│   ├── MediaKeyService.cs           # Win32 SendInput (VK_MEDIA_*, 0 мс задержка)
│   ├── IMediaSessionService.cs      # Контракт GSMTC
│   ├── IAppAudioService.cs          # Контракт изолированной громкости
│   ├── ITimerService.cs             # Контракт таймеров
│   ├── JarvisResponse.cs            # DTO ответа { CommandRequest, Reply }
│   └── ActiveTimer.cs               # Модель активного таймера (Guid, Label, CTS)
│
├── Handlers/                        # Обработчики команд (ICommandHandler)
│   ├── VolumeCommandHandler.cs      # Двухконтурная громкость: мастер + WASAPI плеер
│   ├── MediaCommandHandler.cs       # Медиа (GSMTC + Win32)
│   ├── TimerCommandHandler.cs       # Таймеры (set/cancel/status)
│   ├── AppControlCommandHandler.cs  # Приложения, Steam, медиа-алиасы
│   ├── AppControlCommandHandler.Lifecycle.cs  # Запуск/закрытие процессов
│   ├── HotkeyCommandHandler.cs      # Симуляция горячих клавиш (SendInput)
│   ├── SystemCommandHandler.cs      # Завершение ассистента (с TTS прощанием)
│   ├── WeatherCommandHandler.cs     # Прогноз погоды (голосовой вывод через IVoiceFeedbackService)
│   └── App/
│       ├── DefaultBrowserResolver.cs  # Разрешение браузера по умолчанию через реестр
│       └── KnownAppRegistry.cs      # Канонический реестр системных приложений
│
├── Core/                            # Ядро: контракты и роутер
│   ├── ICommandHandler.cs           # Интерфейс обработчика (CommandName, ExecuteAsync)
│   ├── CommandRequest.cs            # Неизменяемый DTO запроса (command, args: JsonElement)
│   ├── CommandResult.cs             # Результат: Success, Message, Data; Ok()/Fail()
│   ├── CommandRouter.cs             # Диспетчер: Register(), валидация JSON, маршрутизация
│   ├── CliRunner.cs                 # CLI: --download-model, --test-features, --help
│   └── JarvisState.cs              # Enum состояний HUD: Idle, Listening, Thinking, Action
│
├── Win32/                           # Безопасные P/Invoke обёртки над Win32 API
│   ├── NativeMethods.cs             # user32.dll, dwmapi.dll (SendInput, EnumWindows, DwmGetWindowAttribute…)
│   ├── NativeStructs.cs             # INPUT, KEYBDINPUT, MOUSEINPUT, константы VK_*
│   └── KeyboardHook.cs             # Утилиты низкоуровневого ввода
│
├── Gem.Tests/                       # Набор xUnit тестов
│   ├── VoiceListenerTests.cs        ├── NegativeKwsTests.cs
│   ├── RmsNoiseGateTests.cs         ├── AcousticEchoSuppressionTests.cs
│   ├── FsmConcurrencyLockTests.cs   ├── Sapi5VoiceBitnessTests.cs
│   ├── TtsFailoverTests.cs          ├── MediaControlTests.cs
│   ├── PlayerVolumeControlTests.cs  ├── FastRoutingTests.cs
│   ├── BassFilterTests.cs           ├── RadialSpectrumAnalyzerTests.cs
│   ├── OverdriveShatterTests.cs     ├── HudSettingsTests.cs
│   ├── HudModernizationTests.cs     ├── DiDecompositionTests.cs
│   ├── SteamAutomationTests.cs      ├── VoskModelHelperTests.cs
│   └── OptimizationAndSafetyTests.cs
│
└── scripts/
    ├── build_release.ps1            # Автономная сборка win-x64 + упаковка в ZIP
    ├── QuickStart.bat               # Интерактивный запуск с проверкой компонентов
    └── Run.bat                      # Быстрый запуск
```

---

## 🔀 Dual-Path Routing — порядок диспетчеризации

`LlmIntentService.TryFastMatch()` выполняется **< 5 мс** без сети. При промахе — локальная LLM (LM Studio / Qwen).

| Приоритет | Категория | Модуль | Примеры фраз |
|:---------:|-----------|--------|--------------|
| **1** | FSM / PendingAction | `JarvisOrchestrator` | `да`, `нет`, `отмена`, `удаляй` |
| **2** | Media Control | `MediaFastMatcher` → `MediaCommandHandler` | `останови видео`, `следующий трек`, `включи музыку` |
| **3** | Volume / Audio | `VolumeFastMatcher` → `VolumeCommandHandler` | `тише`, `громче`, `музыка тише`, `звук 50` |
| **4** | Timers | `TimerFastMatcher` → `TimerCommandHandler` | `таймер 10 минут`, `напомни через 5 мин` |
| **5** | System / Apps | `AppFastMatcher` → `AppControlCommandHandler` | `открой браузер`, `проводник`, `закройся` |
| **6** | Steam | `SteamService` | `запусти ведьмак`, `установи киберпанк`, `удали астрониров` |
| **7** | LLM Fallback | `LlmIntentService.InterpretAsync` | Свободный диалог, нетривиальные интенты |

```
[Router: FastMatch] Анализ фразы: «следующий трек»
[Router: FastMatch] HIT -> media/next
[Router: FastMatch] MISS -> Перенаправление в LLM (InterpretAsync)...
```

---

## 🎛 Состояния HUD и цветовая схема

| Состояние `JarvisState` | Цвет | Описание |
|-------------------------|------|----------|
| `Idle` | 💤 Циан `#00E5FF` → Ультрамарин `#0040FF` | Ожидание wake-word; корона плавно колышется |
| `Listening` | 🎙 Янтарный `#FFD600` | Запись команды (RMS-пульсация кольца) |
| `Thinking` | 🤔 Фиолетовый `#D500F9` | Dual-Path / LLM-запрос |
| `Action` | ⚡ Изумрудный `#00E676` | Исполнение команды обработчиком |

**Дополнительные цветовые темы HUD** (SettingsWindow → вкладка «🎛 Визуализатор»):

| Тема | Описание |
|------|----------|
| Arc Reactor (по умолчанию) | Циан `#00E5FF` → глубокий ультрамарин `#0040FF` |
| Cyberpunk Flow | Циан → Фиолетовый `#8A2BE2` → Маджента `#F500F5` |
| Solar Flare | Янтарь `#FFC400` → Оранжевый `#FF6400` → Неоновый красный `#FF141E` |
| Custom | Ручная настройка HEX: Core Color, Glow Color, Rays Accent Color |

---

## 🎮 Управление и горячие клавиши

| Действие | Жест / Команда |
|----------|----------------|
| Перетаскивание HUD | Drag мышью внутри 75 px ядра |
| Открыть настройки | ПКМ по ядру → Settings / двойной клик |
| Включить Overdrive | 2.5–3.5 с непрерывного плотного баса |
| Сбросить FSM-диалог | Сказать «нет» / «отмена» / истечёт 10 с |
| Выход | `«закройся»` / `«выключись»` / ПКМ → Exit |

---

## 🛠 Сборка и запуск

### Требования
- **Windows 10/11** (x64)
- **.NET 8 SDK**
- **LM Studio** с локальным сервером `http://127.0.0.1:1234` (для LLM fallback, опционально)

### Разработка из исходного кода

```bash
# 1. Клонирование
git clone https://github.com/SamuRush/GemHelper.git

# 2. Загрузка полноразмерной модели Vosk ru-0.42 (~1.5 ГБ)
dotnet run --project Gem.csproj -- --download-model

# 3. Сборка
dotnet build Gem.csproj

# 4. Запуск
dotnet run --project Gem.csproj
```

### Сборка релизного пакета (self-contained, win-x64)

```powershell
pwsh ./scripts/build_release.ps1 -Configuration Release -ZipOutput $true
```

### Тесты

```bash
# Полный набор xUnit (через Gem.sln)
dotnet test Gem.sln

# Встроенный набор самотестирования
dotnet run --project Gem.csproj -- --test-features
```

---

## ⚙️ Конфигурация (`appsettings.json`)

```json
{
  "CityName": "Санкт-Петербург",
  "Audio": { "DeviceIndex": 0, "DeviceName": "Микрофон (Fifine Microphone)" },
  "WakeWord": {
    "Name": "джарвис",
    "SmallModelPath": "Models/VoskSmall/vosk-model-small-ru",
    "NoiseGateRms": 350.0,
    "MinDurationMs": 150
  },
  "WakeWords": ["джарвис", "jarvis", "гемини"],
  "Llm": {
    "BaseUrl": "http://127.0.0.1:1234/v1",
    "Model": "qwen2.5-1.5b-instruct"
  },
  "Tts": {
    "EnableEdgeTts": true,
    "EdgeVoice": "ru-RU-SvetlanaNeural",
    "Sapi5Voice": "Aidar",
    "ConnectionTimeoutMs": 1500
  },
  "Vosk": { "ModelPath": "./model" },
  "GameAliases": { "ведьмак": "292030", "киберпанк": "1091500" }
}
```

| Параметр | Описание |
|----------|----------|
| `EnableEdgeTts: true` | Edge Neural TTS Primary + SAPI5 Fallback (Fast Failover ≤ 1.6 с) |
| `EnableEdgeTts: false` | Полностью офлайн: SAPI5 как первичный движок |
| `WakeWord.NoiseGateRms` | RMS порог шумового гейта (настраивается слайдером в Settings, 50–600) |
| `WakeWord.MinDurationMs` | Минимальная акустическая длительность wake-word (150 мс) |
| `Audio.DeviceIndex` | Индекс устройства захвата (выбор без перезапуска через Settings) |

---

## 📦 Portable-дистрибутив

```
JARVIS-win-x64/
├── Gem.exe                  # Self-contained .NET 8 (без установки SDK)
├── QuickStart.bat           # Первый запуск: проверка среды + автозагрузка модели Vosk
├── Run.bat                  # Быстрый запуск
├── appsettings.json
├── Dependencies/
│   ├── VisualCppRedistributable.url
│   └── SileroSAPI5_Setup_Guide.txt
└── Models/
    ├── Vosk/                # vosk-model-ru-0.42 (~1.5 ГБ, скачивается автоматически)
    └── WakeWord/            # jarvis.onnx (скачивается при первом запуске)
```

**Шаги установки:**
1. Скачать `JARVIS-win-x64.zip` из [GitHub Releases](../../releases).
2. Распаковать в любую директорию без пробелов в пути.
3. Запустить `QuickStart.bat` — скрипт проверит среду и предложит скачать модель Vosk.
4. (Опционально) Установить Silero SAPI5 для качественного офлайн-синтеза речи.

---

## 🧪 Покрытие тестами

| Модуль | Тест-файл | Что проверяется |
|--------|-----------|-----------------|
| VoiceListener FSM | `VoiceListenerTests.cs` | RMS-вычисление, Noise Gate, FSM-переходы, Token Matching |
| KWS негативные кейсы | `NegativeKwsTests.cs` | Фреймы < 150 мс, фонетические обрывки («джа», «рис», «вис») |
| RMS Noise Gate | `RmsNoiseGateTests.cs` | Тишина/дыхание не проходят в Vosk |
| Acoustic Echo | `AcousticEchoSuppressionTests.cs` | Блокировка фреймов при `IsSpeaking`, cooldown 250 мс |
| FSM Concurrency | `FsmConcurrencyLockTests.cs` | Параллельные wake-word игнорируются при `_isProcessing` |
| TTS Failover | `TtsFailoverTests.cs` | Edge → SAPI5 ≤ 1.6 с при сбое |
| Медиа-пайплайн | `MediaControlTests.cs` | GSMTC контракты, идемпотентность Play/Pause |
| Громкость плеера | `PlayerVolumeControlTests.cs` | WASAPI изоляция от мастер-громкости |
| Bass Detection | `BassFilterTests.cs` | IIR LPF, буфер истории 43 слота, адаптивный порог 1.35 |
| FFT Спектр | `RadialSpectrumAnalyzerTests.cs` | Захват Loopback, Peak Falloff, билатеральная симметрия |
| Overdrive | `OverdriveShatterTests.cs` | Физика осколков, Arc Gauge, пороги, HudSettings.json |
| HUD Settings | `HudSettingsTests.cs` | Сериализация, палитры, runtime-применение к ArcReactorVisualHost |
| FastMatch | `FastRoutingTests.cs` | Корректность маршрутизации < 5 мс |
| DI декомпозиция | `DiDecompositionTests.cs` | Регистрация сервисов через ServiceCollectionExtensions |
| Steam UI Automation | `SteamAutomationTests.cs` | AutomationElement, DPI-независимость |
| SAPI5 x64 | `Sapi5VoiceBitnessTests.cs` | Безопасная инициализация 32-бит токенов в x64 |

---

## 📋 Диагностические маркеры (консоль)

| Маркер | Описание |
|--------|----------|
| `[WakeWord: VoskGrammar ("имя")] [Detection Latency: XX ms]` | KWS сработал, время фрейма |
| `[WakeWord: OpenWakeWord (ONNX)] [Detection Latency: XX ms]` | ONNX детектор (< 80 мс) |
| `[STT: Vosk Partial]` / `[STT: Vosk Final]` | Промежуточное / итоговое распознавание |
| `[Router: FastMatch] HIT -> media/next` | FastMatch нашёл совпадение |
| `[Router: FastMatch] MISS -> LLM` | Запрос уходит в LLM |
| `[Router: Dispatch]` / `[Router: Result]` | Исполнение в CommandRouter |
| `[TTS: Edge] Воспроизведение завершено.` | Edge Neural TTS успешно |
| `[TTS: Warning] Сбой...` | Инициирован откат на SAPI5 |
| `[TTS: SAPI5] Успешно активирован голос: '...'` | Подтверждение выбора SAPI5 голоса |

---

## 📂 `.gitignore` — исключения

| Путь | Причина |
|------|---------|
| `bin/`, `obj/` | Артефакты сборки |
| `model/` | vosk-model-ru-0.42 (~1.5 ГБ) |
| `Models/VoskSmall/` | vosk-model-small-ru (~45 МБ) |
| `Models/WakeWord/*.onnx` | ONNX модели (jarvis.onnx) |
| `*.wav`, `*.mp3` | Временные аудиофайлы TTS |
| `appsettings.Development.json` | Локальные настройки разработки |
