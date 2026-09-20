# JARVIS / GemHelper — Голосовой ассистент на C# / .NET 8 + WPF

> Локальный голосовой ассистент для Windows с WPF HUD, гибридным отказоустойчивым TTS, Dual-Path роутингом команд, автоматизацией Steam и FSM-диалогами подтверждений. Полностью офлайн-способен: LLM запускается локально через LM Studio, STT работает через Vosk, TTS — через Edge Neural (с фоллбэком на Silero ONNX и Windows SAPI).

![Platform](https://img.shields.io/badge/Platform-Windows-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-purple)
![Language](https://img.shields.io/badge/Language-C%23-green)

---

## 📁 Структура проекта

```
Gem/
├── Core/
│   ├── ICommandHandler.cs          # Единый интерфейс обработчика команд (CommandName, ExecuteAsync)
│   ├── CommandRequest.cs           # Неизменяемый DTO запроса (command, args: JsonElement)
│   ├── CommandResult.cs            # Результат выполнения: Success, Message, Data; фабрики Ok()/Fail()
│   ├── CommandRouter.cs            # Диспетчер команд: Register(), валидация JSON, асинхронная маршрутизация
│   └── JarvisState.cs              # Enum состояний HUD: Idle, Listening, Thinking, Action
│
├── Handlers/
│   ├── VolumeCommandHandler.cs     # Управление мастер-громкостью (NAudio CoreAudioApi, set/change/mute/get)
│   ├── MediaCommandHandler.cs      # Мультимедиа-команды через MediaKeyService (Play/Pause/Next/Prev/Stop)
│   ├── TimerCommandHandler.cs      # Таймеры и напоминания через TimerService (set/cancel/status)
│   ├── AppControlCommandHandler.cs # Запуск/закрытие приложений и Steam-игр + медиа-алиасы
│   ├── HotkeyCommandHandler.cs     # Симуляция горячих клавиш (Ctrl+C, Win+D, Win+Shift+S и др.)
│   ├── SystemCommandHandler.cs     # Завершение ассистента (action: close, name: jarvis)
│   └── WeatherCommandHandler.cs    # Прогноз погоды через WeatherService + голосовое озвучивание
│
├── Services/
│   ├── VoiceListener.cs            # Потоковый захват микрофона (NAudio WaveIn 16 кГц + Vosk STT FSM)
│   ├── LlmIntentService.cs         # Dual-Path Router: TryFastMatch (< 5 мс) + LLM fallback (LM Studio)
│   ├── JarvisOrchestrator.cs       # FSM-координатор подтверждений (PendingActionState, 10 с таймаут)
│   ├── SteamService.cs             # Steam: индексация VDF/ACF, Fuzzy-поиск, Pixel Scan, Install/Uninstall
│   ├── AppHandler.cs               # Централизованный исполнитель ответов HandleJarvisResponseAsync
│   ├── MediaKeyService.cs          # Win32 SendInput диспетчер медиа-клавиш VK_MEDIA_* (0 мс)
│   ├── WeatherService.cs           # Клиент Open-Meteo API с геокодингом городов
│   ├── AppSettingsService.cs       # Загрузка и валидация конфигурации из appsettings.json
│   ├── TimerService.cs             # Потокобезопасные таймеры (ConcurrentDictionary + Task.Delay)
│   ├── ITimerService.cs            # Контракт сервиса таймеров
│   ├── ActiveTimer.cs              # Модель активного таймера (Guid, Label, CancellationTokenSource)
│   ├── JarvisResponse.cs           # DTO ответа { CommandRequest, Reply }
│   ├── CompositeVoiceFeedbackService.cs  # Оркестратор TTS: Edge → Silero → System.Speech
│   ├── VoiceFeedbackService.cs     # Мост обратной совместимости к CompositeVoiceFeedbackService
│   └── TTS/
│       ├── IVoiceFeedbackService.cs      # Контракт голосовой обратной связи (SpeakAsync, события, движки)
│       ├── ITtsEngine.cs                 # Базовый контракт движка (Name, IsAvailable, SpeakAsync)
│       ├── EdgeTtsEngine.cs              # Primary: Edge Neural TTS WebSocket (ru-RU-DmitryNeural, 2500 мс таймаут)
│       ├── SileroTtsEngine.cs            # Secondary: Silero ONNX Runtime (локальный, PCM 24/48 кГц)
│       └── SystemSpeechTtsEngine.cs      # Safety Fallback: Windows SAPI (System.Speech, всегда доступен)
│
├── UI/
│   ├── OverlayWidget.xaml          # WPF HUD: прозрачный всегда-поверх оверлей с анимацией состояний
│   ├── OverlayWidget.xaml.cs       # Код-бихайнд HUD (привязка к JarvisState, анимации, позиционирование)
│   ├── SettingsWindow.xaml         # WPF окно настроек (LLM, TTS, STT, пробуждающие слова)
│   └── SettingsWindow.xaml.cs      # Код-бихайнд настроек (чтение/сохранение appsettings.json)
│
├── Voice/
│   └── VoskModelHelper.cs          # Проверка наличия и автозагрузка модели Vosk (--download-model)
│
├── Win32/
│   ├── NativeMethods.cs            # P/Invoke: user32.dll (SendInput, FindWindowEx, EnumWindows, GetWindowRect)
│   ├── NativeStructs.cs            # Структуры Win32: INPUT, KEYBDINPUT, MOUSEINPUT, константы VK_*
│   └── KeyboardHook.cs             # Утилиты низкоуровневого ввода и хуков
│
├── GlobalUsings.cs                 # Глобальные using-директивы проекта
├── appsettings.json                # Конфигурация: LLM, TTS, STT, WakeWords, GameAliases, погода
├── Program.cs                      # Точка входа: регистрация DI, команд, WPF App + VoiceListener
└── Gem.csproj                      # Проект .NET 8 Windows (WPF, NAudio, Vosk, ONNX, System.Speech)
```

---

## 🎙️ STT: Vosk ru-0.42 — Офлайн-распознавание речи

Для распознавания речи используется **полноразмерная акустическая модель `vosk-model-ru-0.42`** (~1.5 ГБ) — наиболее точная публичная русскоязычная модель Vosk.

- **Захват**: `NAudio.Wave.WaveInEvent` (PCM 16 кГц, Mono, 16-bit, буфер 50 мс).
- **Синглтон модели**: `_sharedModel` исключает утечки неуправляемой памяти при повторных запусках.
- **Трейс в консоли**: `[STT: Vosk Partial]` и `[STT: Vosk Final]` в реальном времени.

### FSM состояний VoiceListener

```
WaitingForWakeWord  ──(обнаружен 'джарвис')──>  ListeningForCommand
      ↑                                                  │
      └────────────(тишина 700 мс → фраза готова)────────┘
```

| Состояние | Описание |
|---|---|
| `WaitingForWakeWord` | Непрерывное сканирование на вейк-ворды: `джарвис`, `алиса`, `компьютер`, `гемини` и др. |
| `ListeningForCommand` | Буферизация слов до паузы 700 мс (без промежуточного сброса `recognizer.Reset()`). |
| `EnterConfirmationListening()` | Прямой переход в `ListeningForCommand` **без вейк-ворда** — для FSM-диалогов подтверждений. |

### Установка модели Vosk

```bash
dotnet run -- --download-model
```

Скрипт автоматически скачает `vosk-model-ru-0.42` (~1.5 ГБ) с alphacephei.com и распакует в `./model`. Требуется ~3.5 ГБ свободного места. Прогресс с оценкой скорости отображается в консоли.

> **⚠️** Каталог `model/` занесён в `.gitignore` — модель не коммитится в репозиторий.

---

## 🔀 Dual-Path Routing — Приоритезированная диспетчеризация

`LlmIntentService.TryFastMatch()` выполняется **строго менее 5 мс** без каких-либо сетевых запросов. При промахе запрос уходит в локальную LLM (LM Studio / Qwen).

| Приоритет | Категория | Обработчик | Типичные фразы |
|---|---|---|---|
| **1** | **FSM / PendingAction** | `JarvisOrchestrator` | `да`, `нет`, `отмена`, `удаляй`, `запускай` |
| **2** | **Media Keys** | `MediaKeyService` / `MediaCommandHandler` | `пауза`, `следующий трек`, `предыдущий`, `стоп` |
| **3** | **Volume / Audio** | `VolumeCommandHandler` | `тише`, `громче`, `звук 50`, `мут`, `выключи звук` |
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

## 🗣️ TTS: Гибридный отказоустойчивый синтез речи

`CompositeVoiceFeedbackService` реализует трёхуровневую цепочку отказоустойчивости:

```
1. EdgeTtsEngine        ──(сбой/таймаут 2500 мс)──>
2. SileroTtsEngine      ──(модель отсутствует)──>
3. SystemSpeechTtsEngine (Windows SAPI, всегда доступен)
```

| Движок | Тип | Голос | Особенности |
|---|---|---|---|
| **EdgeTtsEngine** | Онлайн, WebSocket | `ru-RU-DmitryNeural` | Нейросетевое качество; строгий таймаут 2500 мс на подключение и первый пакет; автосброс сокета при зависании |
| **SileroTtsEngine** | Офлайн, ONNX | `aidar` / `eugene` | Быстрый локальный синтез PCM 24/48 кГц без интернета |
| **SystemSpeechTtsEngine** | Офлайн, SAPI | Системный голос | Safety Fallback — доступен на любой Windows-системе |

**Защита от самоперехвата (Acoustic Feedback Prevention):**  
На время речи микрофон `VoiceListener` автоматически глушится. После завершения — 300 мс кулдаун перед возобновлением захвата.

**Все компоненты системы** обязаны вызывать голосовой вывод **исключительно через `IVoiceFeedbackService.SpeakAsync()`** — прямые вызовы движков запрещены (нарушает echo cancellation и FSM-машину подтверждений).

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

### Автоматизация UI
- **Установка** (`InstallGameAsync`): визуальный поиск синей кнопки «Установить» через сканирование `Bitmap` окна Steam.
- **Деинсталляция** (`UninstallGameAsync`): протокол `steam://uninstall/{appId}`, строгая фильтрация HWND диалога Steam (PID, заголовок, Rect), клик по кнопке подтверждения через Color Scan (без эмуляции `VK_RETURN`).

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

Прозрачный всегда-поверх оверлей (`OverlayWidget`) отображает текущее состояние ассистента:

| Состояние `JarvisState` | HUD | Описание |
|---|---|---|
| `Idle` | 💤 Тихий | Ассистент ожидает вейк-ворда |
| `Listening` | 🎙️ Активный | Запись команды (пульсирующая анимация) |
| `Thinking` | 🤔 Обработка | Dual-Path / LLM-запрос выполняется |
| `Action` | ⚡ Действие | Команда исполняется обработчиком |

`SettingsWindow` предоставляет графический интерфейс для настройки LLM, TTS, STT-модели, списка вейк-ворд и других параметров из `appsettings.json`.

---

## ⚙️ Команды: JSON-интерфейс

Все команды передаются в формате JSON. `CommandRouter` валидирует и маршрутизирует к соответствующему `ICommandHandler`.

```json
{ "command": "<имя>", "args": { ... } }
```

| Команда | Действия (`action`) | Пример |
|---|---|---|
| `volume` | `set`, `change`, `mute`, `get` | `{ "command": "volume", "args": { "action": "set", "level": 50 } }` |
| `media` | `play_pause`, `next`, `prev`, `stop` | `{ "command": "media", "args": { "action": "next" } }` |
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
  "WakeWords": ["джарвис", "алиса", "компьютер", "гемини"],
  "Llm": {
    "BaseUrl": "http://127.0.0.1:1234/v1",
    "Model": "qwen2.5-1.5b-instruct"
  },
  "Tts": {
    "PreferredEngine": "Edge",
    "EdgeVoice": "ru-RU-DmitryNeural",
    "ConnectionTimeoutMs": 2500
  },
  "Vosk": {
    "ModelPath": "./model"
  },
  "GameAliases": {
    "ведьмак": "292030",
    "киберпанк": "1091500"
  }
}
```

---

## 🚀 Сборка и запуск

### Требования
- **Windows 10/11** (x64)
- **.NET 8 SDK**
- **LM Studio** с запущенным локальным сервером на `http://127.0.0.1:1234` (для LLM fallback)

### Установка и запуск

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
| `[STT: Vosk Partial]` / `[STT: Vosk Final]` | Промежуточные и итоговые результаты распознавания |
| `[Router: FastMatch] HIT -> ...` | FastMatch нашёл совпадение, команда выполняется |
| `[Router: FastMatch] MISS -> ...` | FastMatch не сработал, запрос идёт в LLM |
| `[Router: Dispatch]` / `[Router: Result]` | Исполнение команды в `CommandRouter` |
| `[TTS: Edge]` | Edge Neural TTS воспроизводит речь |
| `[TTS: Silero]` | Переключение на локальный Silero ONNX |
| `[TTS: System.Speech]` | Переключение на Windows SAPI fallback |
| `[TTS: Warning]` | Сбой движка, инициирован откат к следующему |

---

## 📂 Файлы, исключённые из Git (`.gitignore`)

| Исключение | Причина |
|---|---|
| `bin/`, `obj/` | Артефакты сборки .NET |
| `model/` | Vosk модель `vosk-model-ru-0.42` (~1.5 ГБ) |
| `Models/` / `*.onnx` | Silero ONNX-веса TTS |
| `*.wav`, `*.mp3` | Временные аудиофайлы синтеза речи |
| `appsettings.Development.json` | Локальные настройки разработки |
