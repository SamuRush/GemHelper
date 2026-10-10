# JARVIS / GemHelper — Голосовой ассистент на C# / .NET 8 + WPF

> Локальный голосовой ассистент для Windows с WPF HUD, гибридным отказоустойчивым TTS, Dual-Path роутингом команд, автоматизацией Steam и FSM-диалогами подтверждений. Полностью офлайн-способен: LLM запускается локально через LM Studio, STT работает через Vosk, TTS — через Edge Neural (Primary, онлайн) с Offline/Fallback на нативный Windows SAPI5.

![Platform](https://img.shields.io/badge/Platform-Windows-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![Language](https://img.shields.io/badge/Language-C%23-green)

---

## 📁 Структура проекта

```
Gem/
├── Gem.sln                         # Решение .NET (основной проект + тестовый проект)
├── Core/
│   ├── ICommandHandler.cs          # Единый интерфейс обработчика команд (CommandName, ExecuteAsync)
│   ├── CommandRequest.cs           # Неизменяемый DTO запроса (command, args: JsonElement)
│   ├── CommandResult.cs            # Результат выполнения: Success, Message, Data; фабрики Ok()/Fail()
│   ├── CommandRouter.cs            # Диспетчер команд: Register(), валидация JSON, асинхронная маршрутизация
│   ├── CliRunner.cs                # CLI-раннер команд первого запуска: --download-model, --test-features, --help
│   └── JarvisState.cs              # Enum состояний HUD: Idle, Listening, Thinking, Action
│
├── Handlers/
│   ├── VolumeCommandHandler.cs     # Двухконтурное управление громкостью: мастер (CoreAudioApi) + изолированный плеер (WASAPI SimpleAudioVolume)
│   ├── MediaCommandHandler.cs      # Мультимедиа-команды через IMediaSessionService (GSMTC/Win32)
│   ├── TimerCommandHandler.cs      # Таймеры и напоминания через TimerService (set/cancel/status)
│   ├── AppControlCommandHandler.cs # Запуск/закрытие приложений и Steam-игр + медиа-алиасы
│   ├── HotkeyCommandHandler.cs     # Симуляция горячих клавиш (Ctrl+C, Win+D, Win+Shift+S и др.)
│   ├── SystemCommandHandler.cs     # Завершение ассистента (action: close, name: jarvis)
│   └── WeatherCommandHandler.cs    # Прогноз погоды через WeatherService + голосовое озвучивание
│
├── Services/
│   ├── VoiceListener.cs            # Потоковый захват микрофона (NAudio WaveIn 16 кГц + Vosk STT FSM + RMS Gate)
│   ├── LlmIntentService.cs         # Dual-Path Router: TryFastMatch (< 5 мс) + LLM fallback (LM Studio)
│   ├── JarvisOrchestrator.cs       # FSM-координатор подтверждений (PendingActionState, 10 с таймаут)
│   ├── SteamService.cs             # Steam: индексация VDF/ACF, Fuzzy-поиск, UI Automation, Pixel Scan fallback
│   ├── AppHandler.cs               # Централизованный исполнитель ответов HandleJarvisResponseAsync
│   ├── MediaKeyService.cs          # Win32 SendInput диспетчер медиа-клавиш VK_MEDIA_* (0 мс)
│   ├── MediaSessionService.cs      # GSMTC (WinRT) + управление видео/музыкой (браузеры, VLC, Яндекс Музыка)
│   ├── IMediaSessionService.cs     # Контракт управления воспроизведением (GSMTC)
│   ├── AppAudioService.cs          # Изолированное управление громкостью приложений (WASAPI SimpleAudioVolume)
│   ├── IAppAudioService.cs         # Контракт изолированного управления громкостью плеера
│   ├── WeatherService.cs           # Клиент Open-Meteo API с геокодингом городов
│   ├── AppSettingsService.cs       # Загрузка и валидация конфигурации из appsettings.json
│   ├── TimerService.cs             # Потокобезопасные таймеры (ConcurrentDictionary + Task.Delay)
│   ├── ITimerService.cs            # Контракт сервиса таймеров
│   ├── ActiveTimer.cs              # Модель активного таймера (Guid, Label, CancellationTokenSource)
│   ├── JarvisResponse.cs           # DTO ответа { CommandRequest, Reply }
│   ├── CompositeVoiceFeedbackService.cs  # Оркестратор TTS: Edge (Primary) → System.Speech SAPI5 (Offline/Fallback)
│   ├── VoiceFeedbackService.cs     # Мост обратной совместимости к CompositeVoiceFeedbackService
│   └── TTS/
│       ├── IVoiceFeedbackService.cs      # Контракт голосовой обратной связи (SpeakAsync, события, движки)
│       ├── ITtsEngine.cs                 # Базовый контракт движка (Name, IsAvailable, SpeakAsync)
│       ├── EdgeTtsEngine.cs              # Primary: Edge Neural TTS WebSocket (1500 мс connect, 2000 мс first-chunk, без повторов)
│       └── SystemSpeechTtsEngine.cs      # Offline/Fallback: Windows SAPI5 (COM direct binding: Aidar / Baya / Kseniya / Pavel / Irina)
│
├── UI/
│   ├── OverlayWidget.xaml          # WPF HUD: прозрачный круглый всегда-поверх оверлей с анимацией состояний
│   ├── OverlayWidget.xaml.cs       # Код-бихайнд HUD (RMS-пульсация, Storyboards, таймеры, ContextMenu)
│   ├── SettingsWindow.xaml         # Sci-Fi TabControl: Аудио, AI, Игры/Steam, Система
│   └── SettingsWindow.xaml.cs      # Код-бихайнд настроек (выбор микрофона, VU-метр, TTS, WakeWords)
│
├── Voice/
│   ├── VoskModelHelper.cs          # Проверка наличия и автозагрузка модели Vosk (--download-model)
│   └── WakeWord/
│       ├── IWakeWordDetector.cs    # Единый контракт потокового детектора активации
│       ├── OpenWakeWordDetector.cs # ONNX Runtime (<80 мс) детектор jarvis.onnx с автозагрузкой
│       ├── VoskGrammarWakeWordDetector.cs # Vosk Grammar детектор (строгий UTF-8 P/Invoke, RMS Gate, 150 мс)
│       └── WakeWordFactory.cs      # Фабрика адаптивного выбора детектора по имени
│
├── Win32/
│   ├── NativeMethods.cs            # P/Invoke: user32.dll (SendInput, FindWindowEx, EnumWindows, GetWindowRect)
│   ├── NativeStructs.cs            # Структуры Win32: INPUT, KEYBDINPUT, MOUSEINPUT, константы VK_*
│   └── KeyboardHook.cs             # Утилиты низкоуровневого ввода и хуков
│
├── Gem.Tests/                      # Набор честных модульных и интеграционных тестов (xUnit)
│   ├── VoiceListenerTests.cs       # Тесты RMS-вычисления, RMS Gate, FSM-переходов VoiceListener и токен-матчинга
│   ├── NegativeKwsTests.cs         # Негативные тесты KWS: фонетические обрывки и слоги
│   ├── RmsNoiseGateTests.cs        # Тесты шумового порога RMS Gate
│   ├── AcousticEchoSuppressionTests.cs # Тесты подавления эха и кулдаун 250 мс
│   ├── FsmConcurrencyLockTests.cs  # Тесты изоляции FSM: блокировка параллельных триггеров
│   ├── Sapi5VoiceBitnessTests.cs   # Тесты безопасной инициализации SAPI5 под x64
│   ├── VoskModelHelperTests.cs     # Тесты поиска и валидации модели Vosk (FindModelDirectory)
│   ├── HudModernizationTests.cs    # Тесты редизайна HUD и диагностики аудио
│   ├── MediaControlTests.cs        # Тесты GSMTC медиа-пайплайна (видео/музыка, раздельное управление)
│   ├── PlayerVolumeControlTests.cs # Тесты изолированного управления громкостью плеера (WASAPI)
│   ├── TtsFailoverTests.cs         # Тесты Fast Failover: откат Edge→SAPI5 ≤ 1.6 с
│   ├── DiDecompositionTests.cs     # Тесты DI-декомпозиции и ServiceCollectionExtensions
│   ├── FastRoutingTests.cs         # Тесты TryFastMatch: корректность маршрутизации команд
│   ├── SteamAutomationTests.cs     # Тесты UI Automation поиска диалогов Steam
│   └── Gem.Tests.csproj            # Проект xUnit тестирования (.NET 8 Windows)
│
├── scripts/                        # Скрипты автоматизации и вспомогательные файлы дистрибутива
│   ├── build_release.ps1           # Автоматизация автономной сборки win-x64 и упаковки в ZIP
│   ├── QuickStart.bat              # Интерактивный запуск с проверкой компонентов и модели
│   ├── Run.bat                     # Быстрый запуск приложения
│   ├── Dependencies/               # Инструкции и ярлыки VC++ Redistributable и Silero SAPI5
│   └── Models/                     # Инструкции и скрипты проверки языковых моделей Vosk/WakeWord
│
├── .github/workflows/
│   └── release.yml                 # GitHub Actions CI/CD: автосборка, тестирование и релизы
│
├── GlobalUsings.cs                 # Глобальные using-директивы проекта (+ InternalsVisibleTo)
├── appsettings.json                # Конфигурация: LLM, TTS, STT, WakeWords, GameAliases, Audio, погода
├── Program.cs                      # Точка входа: регистрация DI, команд, WPF App + VoiceListener
└── Gem.csproj                      # Проект .NET 8 Windows (WPF, NAudio, Vosk, ONNX, System.Speech)
```

---

## 🎙️ Адаптивный Wake-Word (< 80 мс) и STT Vosk ru-0.42
 
Для сверхбыстрой активации и глубокого распознавания речи используется гибридный адаптивный пайплайн:
 
- **Адаптивный детектор `IWakeWordDetector` (`Voice/WakeWord/`)**:
  - `VoskGrammarWakeWordDetector` (детектор по умолчанию): специализированный легковесный распознаватель на базе малой модели `vosk-model-small-ru` (~45 МБ, автозагрузка) с жестко ограниченной грамматикой `["{customName}", "[unk]"]` со строгим маршалингом UTF-8 через нативный P/Invoke (`vosk_recognizer_new_grm`), исключающим предупреждения Kaldi `Ignoring word missing in vocabulary` для любых кастомных имён.
  - `OpenWakeWordDetector` (опционально): инференс через `Microsoft.ML.OnnxRuntime` на модели `Models/WakeWord/jarvis.onnx` с аппаратной задержкой **50–80 мс**. При отсутствии файла модель автоматически скачивается при первом старте.
  - **RMS Noise Gate (`NoiseGateRms`)**: низкоамплитудные фреймы тишины и фонового шума отсекаются до передачи в распознаватель Vosk. Порог настраивается в `appsettings.json` и через слайдер в окне настроек.
  - **Порог минимальной акустической длительности (`MinDurationMs: 150`)**: срабатывание разрешено только при накопленной длительности слова $\ge 150$ мс, предотвращая ложные триггеры на вздохи, кашель, чихание и щелчки.
  - **Защита KWS от ложных срабатываний (Token Matching)**: проверка детекции вейк-ворда выполняется строго через скомпилированный `Regex` с изоляцией токенов `(?:\b|\s|^){customName}(?:\b|\s|$)`. Подстрочный поиск (`Contains`) полностью исключён.
  - `WakeWordFactory`: автоматический выбор детектора на основе настроек в `appsettings.json` с передачей параметров `NoiseGateRms` и `MinDurationMs`.
- **Полная бесшумность**: фиксация имени происходит мгновенно и **строго без звуковых сигналов**.
- **Прием команд**: сразу после фиксации имени поток PCM бесшовно переключается на полноразмерную акустическую модель **`vosk-model-ru-0.42`** (~1.5 ГБ) с сессионной буферизацией и таймаутом естественной паузы 700 мс.
- **Синглтон модели Vosk**: `_sharedModel` исключает утечки неуправляемой памяти и гарантирует RAM < 2 ГБ.
- **Реактивная волна RMS (`OnRmsLevelChanged`)**: потокобезопасный проброс текущей энергии звука в UI HUD для динамической анимации пульсации неонового кольца во время речи пользователя.
- **Диагностический бенчмарк-трейс в консоли**:
  - `[WakeWord: OpenWakeWord (ONNX)] [Detection Latency: XX ms]` или `[WakeWord: VoskGrammar ("имя")] [Detection Latency: XX ms]` в момент срабатывания вейк-ворда.
  - `[STT: Vosk Partial]` и `[STT: Vosk Final]` — промежуточное и итоговое распознавание речи в реальном времени.
  - `[TTS Engine: Edge-TTS (...)]`, `[TTS Engine: System.Speech SAPI5 (...)]` — активный движок синтеза при каждом вызове.
 
### FSM состояний VoiceListener
 
```
WaitingForWakeWord  ──(детектор <80 мс, RMS ≥ NoiseGateRms, ≥ 150 мс, БЕСШУМНО)──>  ListeningForCommand
      ↑                                                                                      │
      └────────────────────────(тишина 700 мс → фраза готова)────────────────────────────────┘
```
 
| Состояние | Описание |
|---|---|
| `WaitingForWakeWord` | Потоковая подача кадров в `VoskGrammarWakeWordDetector` (малая грамматика, RMS Gate, мин. длительность $\ge 150$ мс) или опционально в `OpenWakeWordDetector` ONNX (<80 мс). |
| `ListeningForCommand` | Буферизация слов полноразмерной моделью Vosk ru-0.42 до паузы 700 мс. |
| `EnterConfirmationListening()` | Прямой переход в `ListeningForCommand` **без вейк-ворда** — для FSM-диалогов подтверждений. |
 
### Установка полноразмерной модели Vosk
 
```bash
dotnet run -- --download-model
```
 
Скрипт автоматически скачает `vosk-model-ru-0.42` (~1.5 ГБ) с alphacephei.com и распакует в `./model`. Требуется ~3.5 ГБ свободного места.
 
> **⚠️** Каталоги `model/`, `model-small/` и `Models/` занесены в `.gitignore` — нейросетевые веса не коммитятся в Git.

---

## 🔀 Dual-Path Routing — Приоритезированная диспетчеризация

`LlmIntentService.TryFastMatch()` выполняется **строго менее 5 мс** без каких-либо сетевых запросов. При промахе запрос уходит в локальную LLM (LM Studio / Qwen).

| Приоритет | Категория | Обработчик | Типичные фразы |
|---|---|---|---|
| **1** | **FSM / PendingAction** | `JarvisOrchestrator` | `да`, `нет`, `отмена`, `удаляй`, `запускай` |
| **2** | **Media Control** | `MediaCommandHandler` / `IMediaSessionService` (GSMTC) | `останови видео`, `включи музыку`, `следующий трек`, `продолжи` |
| **3** | **Volume / Audio** | `VolumeCommandHandler` / `IAppAudioService` | `тише`, `громче`, `музыка тише`, `громкость музыки 50`, `мут` |
| **4** | **Timers** | `TimerService` / `TimerCommandHandler` | `поставь таймер на 10 минут`, `напомни через 5 мин` |
| **5** | **System / Apps** | `AppControlCommandHandler` | `открой браузер`, `закройся`, `проводник`, `блокнот` |
| **6** | **Steam** | `SteamService` | `запусти ведьмак`, `установи киберпанк`, `удали ...` |
| **7** | **LLM Fallback** | `LlmIntentService.InterpretAsync` | Свободный диалог, погода, нетривиальные намерения |

**Трейс маршрутизации в консоли:**
```
[Router: FastMatch] Анализ фразы: «следующий трек»
[Router: FastMatch] HIT -> media/next
[Router: FastMatch] MISS -> Перенаправление в LLM (InterpretAsync)...
```

---

## 🗣️ TTS: Отказоустойчивый синтез речи

`CompositeVoiceFeedbackService` реализует двухуровневую цепочку отказоустойчивости с поддержкой как мужских, так и женских голосов.

Режим работы определяется флагом `EnableEdgeTts` в `appsettings.json`:

**Режим `EnableEdgeTts: true` (по умолчанию, гибридный):**
```
[+] [TTS] Инициализирована гибридная архитектура озвучки:
    - EnableEdgeTts:       True
    - Primary (Online):    Edge (ru-RU-SvetlanaNeural / ru-RU-DmitryNeural)
    - Offline / Fallback:  SAPI5 (Aidar / Baya / Kseniya / Microsoft Pavel / Microsoft Irina)

1. EdgeTtsEngine        ──(Connect: 1500 мс, FirstAudioChunk: 2000 мс → мгновенный откат)──>
2. SystemSpeechTtsEngine (SAPI5: COM direct binding)
```

**Режим `EnableEdgeTts: false` (полностью офлайн, SAPI5 как первичный):**
```
[+] [TTS] Инициализирована гибридная архитектура озвучки:
    - EnableEdgeTts:       False
    - Primary (Online):    Edge [ОТКЛЮЧЁН]
    - Offline / Fallback:  SAPI5

1. (Edge-TTS пропускается — сетевые запросы не выполняются)
2. SystemSpeechTtsEngine (SAPI5 — первичный оффлайн-голос)
```

| Движок | Тип | Голос | Особенности |
|---|---|---|---|
| **EdgeTtsEngine** | Онлайн, WebSocket | `ru-RU-SvetlanaNeural` / `ru-RU-DmitryNeural` (настраивается) | Нейросетевое качество; таймаут подключения 1500 мс; жёсткий таймаут первого аудиочанка 2000 мс (без повторов); Keep-Alive пинг (15 с); Fast Failover ≤ 1.6 с; **управляется флагом `EnableEdgeTts`** |
| **SystemSpeechTtsEngine** | Офлайн, SAPI5 | **Aidar / Baya / Kseniya / Microsoft Pavel / Microsoft Irina** | Прямой COM-биндинг системных токенов Silero SAPI5. При отсутствии Silero — автоматический сброс на `Microsoft Pavel` / `Microsoft Irina Desktop`; все мужские и женские голоса поддерживаются без pitch-shift |

**Защита от самоперехвата речи (Acoustic Echo Suppression & Processing Lock):**  
- **Processing Lock**: при фиксации команды микрофон мгновенно блокируется (`_isProcessing = true`), исключая повторные срабатывания на всё время работы LLM и роутера.
- **Acoustic Echo Suppression**: во время воспроизведения TTS флаги `_isPaused` и `_isSpeaking` аппаратно блокируют входящие PCM-фреймы. После окончания речи выдерживается защитное окно затухания **250 мс** в блоке `finally`.

**Корректное завершение процесса («закройся / выключись»):**  
Воспроизводится прощальная фраза через TTS с таймаутом 5 с, после чего выполняется `Application.Current.Shutdown()` + `Environment.Exit(0)`.

**Все компоненты системы** обязаны вызывать голосовой вывод **исключительно через `IVoiceFeedbackService.SpeakAsync()`** — прямые вызовы движков запрещены.

---

## 🎛️ Управление громкостью (двухконтурное)

`VolumeCommandHandler` реализует разделение системной и приложённой громкости:

| Цель | Реализация | Примеры фраз |
|---|---|---|
| **Мастер-громкость Windows** | `NAudio MMDeviceEnumerator` / `IAudioEndpointVolume` | `тише`, `громче`, `звук 50`, `выключи звук` |
| **Громкость плеера** | `IAppAudioService` / WASAPI `ISimpleAudioVolume` | `музыка тише`, `прибавь музыку`, `громкость музыки 50` |

Управление громкостью приложения не влияет на системную мастер-громкость.

---

## 📺 Медиа-управление (GSMTC + Win32)

`MediaSessionService` интегрируется с Windows Global System Media Transport Controls (GSMTC):

- **Статус**: опрос `Playing`, `Paused`, `Stopped` предотвращает ложные тоглы при уже играющей/остановленной музыке.
- **Запуск музыки**: поиск процесса `YandexMusic` → запуск через `yandexmusic://` → ожидание инициализации → воспроизведение.
- **Управление видео**: определение активного окна (браузер или медиаплеер) → отправка `VK_SPACE` или адресная пауза.

---

## 🎮 Steam-автоматизация

`SteamService` обеспечивает полный цикл работы со Steam-библиотекой:

### Индексация библиотеки
- Реестр Windows → `libraryfolders.vdf` → манифесты `appmanifest_*.acf`.
- Парсинг `StateFlags`: учитываются игры в установке/загрузке и полностью установленные.

### Нечёткий поиск игр
- **Транслитерация** En ↔ Ru: `witcher` → `ведьмак`, `cyberpunk` → `киберпанк`.
- **Очистка командных префиксов**: «удали», «деинсталлируй», «сноси», «установи», «запусти».
- **Алгоритм Левенштейна** с порогами уверенности:
  - ≥ 0.82 — точное действие без подтверждения.
  - [0.60, 0.82) — запрос уточнения у пользователя.
  - < 0.60 — игра не найдена.

### Автоматизация UI (DPI-независимая)
- **Установка** (`InstallGameAsync`): поиск кнопки «Установить» приоритетно через `UIAutomationClient` (`AutomationElement`, `ControlType.Button`) с фоллбэком на попиксельный сканер.
- **Деинсталляция** (`UninstallGameAsync`): протокол `steam://uninstall/{appId}`, строгая фильтрация HWND диалога Steam (PID, заголовок, Rect), поиск кнопки подтверждения через UI Automation независимо от DPI (125%/150%), темы и HDR.

### Голосовые примеры
```
«запусти ведьмак»          → прямой запуск (conf ≥ 0.82)
«установи киберпанк»       → диалог подтверждения (conf ∈ [0.60, 0.82))
«удали астрониров»         → FSM-подтверждение: «Вы хотите удалить Astroneer, сэр?»
```

---

## 🤖 FSM-диалоги и подтверждения

Для деструктивных операций (удаление игры) и неоднозначных распознаваний активируется `PendingActionState`.

```
Processing ──(conf ∈ [0.60, 0.82))──> AwaitingConfirmation
                                              │
                      ┌───────────────────────┤
                      ▼                       ▼
              EnterConfirmationListening()  (10 сек таймаут)
                      │                       │
          ┌───────────┴─────────┐       ResetToWaiting
          ▼                     ▼
    IsAffirmativeReply    IsNegativeReply
    (да / давай / удаляй) (нет / отбой / отмена)
          │                     │
    ActionExecution        ActionCancelled
```

**Слова-согласия** (`IsAffirmativeReply`): `да`, `давай`, `подтверждаю`, `устанавливай`, `запускай`, `удаляй`, `деинсталлируй`, `сноси`, `верно`, `хорошо`.

**Слова-отказа** (`IsNegativeReply`): `нет`, `отмена`, `не надо`, `отбой`, `стой`, `не то`, `отставить`.

После озвучивания вопроса — **микрофон переходит в `ListeningForCommand` без вейк-ворда** (`EnterConfirmationListening()`). Если за 10 секунд ответа нет — тихий сброс FSM.

---

## 🖥️ WPF HUD Overlay

Прозрачный круглый всегда-поверх оверлей (`OverlayWidget`) с плавными анимациями состояний:

| Состояние `JarvisState` | Цвет | Описание |
|---|---|---|
| `Idle` | 💤 Циан `#00E5FF` | Ассистент ожидает вейк-ворда |
| `Listening` | 🎙️ Золотой `#FFD600` | Запись команды (пульсирующая RMS-анимация) |
| `Thinking` | 🤔 Фиолетовый `#D500F9` | Dual-Path / LLM-запрос выполняется |
| `Action` | ⚡ Изумрудный `#00E676` | Команда исполняется обработчиком |

- **Реактивная пульсация**: масштаб и прозрачность неонового кольца динамически меняются по уровню RMS входящего сигнала.
- **Плавные Storyboard-переходы**: DropShadowEffect морфится между состояниями.
- **Титры распознавания**: Vosk PartialResult / FinalResult отображаются в реальном времени с Fade-out через 3 секунды тишины.
- **Мини-панель таймеров**: обратный отсчёт ММ:СС в правом верхнем углу при активных таймерах.
- **ContextMenu по правому клику**: открытие окна настроек.

`SettingsWindow` реализован как Sci-Fi TabControl со вкладками:
- **🎙 Аудио**: выбор микрофона (`NAudio WaveInEvent.GetCapabilities()`), VU-метр реального времени, слайдер RMS Noise Gate (50–600).
- **🧠 Искусственный интеллект**: параметры LLM (BaseUrl, Model), выбор голоса TTS, переключение Edge/SAPI5.
- **🎮 Игры и Steam**: алиасы игр, список вейк-ворд.
- **⚙️ Система**: город погоды, горячие клавиши.

---

## ⚙️ Команды: JSON-интерфейс

Все команды передаются в формате JSON. `CommandRouter` валидирует и маршрутизирует к соответствующему `ICommandHandler`.

```json
{ "command": "<имя>", "args": { ... } }
```

| Команда | Действия (`action`) | Пример |
|---|---|---|
| `volume` | `set`, `change`, `mute`, `get` | `{ "command": "volume", "args": { "action": "set", "level": 50 } }` |
| `volume` (плеер) | `set`, `change`, `mute` | `{ "command": "volume", "args": { "action": "set", "level": 50, "target": "player" } }` |
| `media` | `play_pause`, `next`, `prev`, `stop`, `pause_video`, `play_music` | `{ "command": "media", "args": { "action": "next" } }` |
| `timer` | `set`, `cancel`, `status` | `{ "command": "timer", "args": { "action": "set", "seconds": 300, "label": "кофе" } }` |
| `app` | `start`, `close`, `status`, `uninstall` | `{ "command": "app", "args": { "action": "start", "name": "steam" } }` |
| `hotkey` | — | `{ "command": "hotkey", "args": { "keys": ["ctrl", "shift", "esc"] } }` |
| `weather` | — | `{ "command": "weather", "args": { "city": "Москва" } }` |
| `system` | `close` | `{ "command": "system", "args": { "action": "close", "name": "jarvis" } }` |

---

## 🛠️ Конфигурация (`appsettings.json`)

Все параметры настраиваются через `appsettings.json` — без хардкода в коде:

```json
{
  "CityName": "Санкт-Петербург",
  "Latitude": 59.93863,
  "Longitude": 30.31413,
  "Audio": {
    "DeviceIndex": 0,
    "DeviceName": "Микрофон (Fifine Microphone)"
  },
  "WakeWord": {
    "Name": "джарвис",
    "OnnxModelPath": "Models/WakeWord/jarvis.onnx",
    "SmallModelPath": "Models/VoskSmall/vosk-model-small-ru",
    "Threshold": 0.5,
    "NoiseGateRms": 350.0,
    "MinDurationMs": 150
  },
  "WakeWords": ["джарвис", "jarvis", "алиса", "компьютер", "гемини"],
  "Llm": {
    "BaseUrl": "http://127.0.0.1:1234/v1",
    "ApiKey": "lm",
    "Model": "qwen2.5-1.5b-instruct",
    "UseResponseFormat": false,
    "Debug": true
  },
  "Tts": {
    "PreferredEngine": "Edge",
    "Voice": "ru-RU-SvetlanaNeural",
    "EdgeVoice": "ru-RU-SvetlanaNeural",
    "Sapi5Voice": "Aidar",
    "ConnectionTimeoutMs": 5000,
    "EnableEdgeTts": true
  },
  "Vosk": {
    "ModelPath": "./model"
  },
  "GameAliases": {
    "ведьмак": "292030",
    "киберпанк": "1091500",
    "дота": "570",
    "кс": "730"
  }
}
```

> **💡 `EnableEdgeTts: false`** — полностью отключает Edge-TTS. Ассистент работает строго офлайн: первичным голосом становится **нативный SAPI5**, без сетевых запросов.
> **💡 `EnableEdgeTts: true`** (по умолчанию) — Edge-TTS работает как Primary с Fast Failover (таймаут подключения 1500 мс, жёсткий таймаут первого чанка 2000 мс) и автоматическим сбросом на SAPI5 при сбоях.
> **💡 `Audio.DeviceIndex`** — индекс устройства захвата звука. Выбирается через Sci-Fi окно настроек без перезапуска.
> **💡 `WakeWord.NoiseGateRms`** — порог RMS Noise Gate. Регулируется слайдером в настройках (50–600) с обновлением в рантайме.

---

## 📦 Скачивание и запуск готового релиза (Portable / Без .NET SDK)

Для обычных пользователей доступен готовый автономный портативный дистрибутив. **Устанавливать .NET SDK или среду разработки НЕ ТРЕБУЕТСЯ** — всё необходимое уже входит в состав сборки!

### Пошаговая инструкция:

1. **Скачивание**:
   - Перейдите в раздел [GitHub Releases](../../releases) репозитория.
   - Скачайте архив **`JARVIS-win-x64.zip`** из последнего релиза.

2. **Распаковка**:
   - Распакуйте содержимое zip-архива в любую удобную директорию (например, `C:\JARVIS` или на рабочий стол).
   - Путь к папке не должен содержать повреждённых символов.

3. **Запуск в один клик**:
   - Запустите командный файл **`QuickStart.bat`** (либо **`Run.bat`** / **`JARVIS.exe`**).
   - Скрипт автоматически проверит разрядность Windows (x64) и доступность компонентов.

4. **Загрузка языковой модели Vosk (STT)**:
   - При первом запуске `QuickStart.bat` обнаружит отсутствие модели и предложит меню:
     - Нажмите клавишу **`1`** и `Enter` — ассистент самостоятельно скачает и распакует полноразмерную русскую акустическую модель `vosk-model-ru-0.42` (~1.5 ГБ).
     - После завершения загрузки модель будет сохранена в папку `Models\Vosk\` и ассистент будет полностью готов к голосовому управлению.

5. **Опциональные компоненты**:
   - **Visual C++ Redistributable (x64)**: если возникает ошибка о нехватке нативных библиотек C++, установите пакет от Microsoft (`Dependencies\VisualCppRedistributable.url`).
   - **Офлайн-голос Silero SAPI5**: для максимального качества офлайн-синтеза установите пакет `Aidar (Russian)` по инструкции `Dependencies\SileroSAPI5_Setup_Guide.txt`.
   - **LM Studio**: для сложных диалогов и свободного общения запустите локальный сервер на `http://127.0.0.1:1234`.

---

### 📂 Структура готового дистрибутива (Portable ZIP)

```
JARVIS-win-x64/
├── Gem.exe                     # Автономный исполняемый файл приложения (.NET 8 self-contained)
├── JARVIS.exe                  # Пользовательский ярлык для удобного запуска
├── QuickStart.bat              # Скрипт первого запуска: проверка среды и автозагрузка модели
├── Run.bat                     # Быстрый запуск ассистента
├── appsettings.json            # Файл конфигурации (TTS, STT, LLM, аудио, алиасы)
│
├── Dependencies/               # Вспомогательные файлы и ссылки на компоненты
│   ├── README.txt              # Справка по зависимостям
│   ├── VisualCppRedistributable.url # Ссылка на Microsoft VC++ 2015-2022 x64
│   └── SileroSAPI5_Setup_Guide.txt # Руководство по установке системного голоса Silero
│
├── Models/
│   ├── Vosk/                   # Директория языковой модели распознавания речи
│   │   ├── README.txt          # Инструкция по ручной установке vosk-model-ru-0.42
│   │   └── CheckModel.bat      # Проверка готовности модели Vosk
│   └── WakeWord/               # Директория моделей активации
│       └── README.txt          # Информация по автозагрузке ONNX-модели jarvis.onnx
│
└── [Библиотеки .NET и DLL]     # libvosk.dll, onnxruntime.dll, NAudio, WPF библиотеки
```

---

## 🚀 Сборка и запуск для разработчиков

### Требования
- **Windows 10/11** (x64)
- **.NET 8 SDK**
- **LM Studio** с запущенным локальным сервером на `http://127.0.0.1:1234` (для LLM fallback)

### Сборка релизного пакета через скрипт
```powershell
# Автоматическая сборка автономного дистрибутива и упаковка в ZIP
pwsh ./scripts/build_release.ps1 -Configuration Release -ZipOutput $true
```

### Разработка и запуск из исходного кода

```bash
# 1. Клонирование репозитория
git clone https://github.com/SamuRush/GemHelper.git
cd GemHelper

# 2. Загрузка модели Vosk (~1.5 ГБ, требуется ~3.5 ГБ свободного места)
dotnet run -- --download-model

# 3. Сборка
dotnet build

# 4. Запуск
dotnet run
```

> При запуске автоматически открывается WPF HUD Overlay и консоль с трейс-логами STT/Router/TTS.

---

## 📋 Диагностические маркеры (консоль)

| Маркер | Описание |
|---|---|
| `[WakeWord: ONNX]` | OpenWakeWord инференс на jarvis.onnx (<80 мс) |
| `[WakeWord: Vosk Grammar]` | Vosk Grammar распознаватель для имён (по умолчанию) |
| `[STT: Vosk Partial]` / `[STT: Vosk Final]` | Промежуточные и итоговые результаты распознавания |
| `[Router: FastMatch] HIT -> ...` | FastMatch нашёл совпадение, команда выполняется |
| `[Router: FastMatch] MISS -> ...` | FastMatch не сработал, запрос идёт в LLM |
| `[Router: Dispatch]` / `[Router: Result]` | Исполнение команды в `CommandRouter` |
| `[TTS: SAPI5] Обнаружен голос: ...` | Перечень всех установленных SAPI5 голосов при старте |
| `[TTS: SAPI5 Info]` | Информация о совместимости токена с x64 процессом |
| `[TTS: SAPI5] Успешно активирован голос: '...'` | Подтверждение успешного выбора голоса |
| `[TTS: Edge]` | Edge Neural TTS воспроизводит речь |
| `[TTS: System.Speech]` | Переключение на Windows SAPI5 |
| `[TTS: Warning]` | Сбой движка, инициирован откат к следующему |

---

## 🧪 Методология честного негативного и стресс-тестирования

В проекте реализован набор автоматических тестов (`Gem.Tests/` на xUnit), полностью исключающий фиктивные заглушки:

1. **VoiceListener Test (`VoiceListenerTests.cs`)**:
   - RMS-вычисление тишины и речи через `VoiceListener.CalculateRms` и `VoskGrammarWakeWordDetector.CalculateRms`.
   - RMS Noise Gate: тихий шум не передаётся в детектор, речевой сигнал — передаётся.
   - FSM: триггер вейк-ворда немедленно переводит `VoiceListener` в `ListeningForCommand`.
   - Token Matching: фонетические обрывки («джа», «рис», «вис», «сюрприз») отвергаются, корректные токены принимаются.
2. **Negative KWS Test (`NegativeKwsTests.cs`)**:
   - Фильтрация по длительности (<150 мс): короткие фреймы не вызывают срабатывания.
   - Отсечение фонетических обрывков и суффиксов строгим `IsIsolatedTokenMatch`.
3. **RMS Noise Gate Test (`RmsNoiseGateTests.cs`)**:
   - Аудио тишины, дыхания и тихого белого шума (`RMS < NoiseGateRms`) не передаётся в Vosk.
4. **Acoustic Echo Suppression Test (`AcousticEchoSuppressionTests.cs`)**:
   - При `IsSpeaking == true` все входящие фреймы отбрасываются.
   - Разблокировка строго после окончания TTS и обязательного кулдауна **250 мс**.
5. **FSM Concurrency Lock Test (`FsmConcurrencyLockTests.cs`)**:
   - При `_isProcessing == true` параллельные триггеры вейк-ворда игнорируются.
6. **SAPI5 Voice Bitness Test (`Sapi5VoiceBitnessTests.cs`)**:
   - Безопасная инициализация SAPI5 в x64 при наличии 32-битных токенов; выбор `Microsoft Pavel` без сбоев.
7. **TTS Failover Test (`TtsFailoverTests.cs`)**:
   - При таймауте/обрыве Edge-TTS полный откат на SAPI5 занимает ≤ 1.6 с.
8. **Media Control Tests (`MediaControlTests.cs`)**:
   - Проверка всех GSMTC сценариев (видео/музыка, раздельные контракты).
9. **Player Volume Tests (`PlayerVolumeControlTests.cs`)**:
   - Изменение громкости плеера не затрагивает системную мастер-громкость.

### Команды запуска тестов:

```bash
# Запуск полного набора xUnit-тестов через файл решения Gem.sln
dotnet test

# Запуск встроенного набора самотестирования ассистента
dotnet run --project Gem.csproj -- --test-features
```

---

## 📂 Файлы, исключённые из Git (`.gitignore`)

| Исключение | Причина |
|---|---|
| `bin/`, `obj/` | Артефакты сборки .NET |
| `model/` | Полноразмерная модель Vosk `vosk-model-ru-0.42` (~1.5 ГБ) |
| `model-small/` | Малая модель Vosk `vosk-model-small-ru` (~45 МБ) |
| `Models/` / `*.onnx` | ONNX модели (openWakeWord) |
| `*.zip` | Временные архивы загрузки моделей |
| `*.wav`, `*.mp3` | Временные аудиофайлы синтеза речи |
| `appsettings.Development.json` | Локальные настройки разработки |
