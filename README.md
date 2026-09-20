# Gem: Система автоматизации и голосового управления на C# (.NET 8)

Модульное консольное приложение на .NET 8 (Windows) с архитектурой диспетчера команд (`CommandRouter` / `ICommandHandler`), вызовом через строковый JSON-формат, управлением громкостью, приложениями, симуляцией клавиатурных событий (SendInput), а также фоновым слушателем микрофона (Vosk) с детекцией ключевого слова «джарвис» и алгоритмом определения конца фразы (Silence Detection).

---

## 📁 Структура проекта

```
Gem/
├── Core/
│   ├── ICommandHandler.cs          # Единый интерфейс обработчика команд
│   ├── CommandRequest.cs           # DTO запроса (command, args)
│   ├── CommandResult.cs            # Результат выполнения (Success, Message, Data)
│   └── CommandRouter.cs            # Диспетчер команд (валидация, парсинг JSON, маршрутизация)
├── Handlers/
│   ├── VolumeCommandHandler.cs     # Управление системным звуком (NAudio CoreAudioApi)
│   ├── AppControlCommandHandler.cs # Запуск и закрытие процессов (Process.Start / Kill)
│   ├── HotkeyCommandHandler.cs     # Симуляция горячих клавиш (P/Invoke SendInput / keybd_event)
│   ├── MediaCommandHandler.cs      # Управление медиа-плеером (Play/Pause, Next, Prev, Stop)
│   └── TimerCommandHandler.cs      # Таймеры и напоминания (set, cancel, status)
├── Services/
│   ├── TimerService.cs             # Локальный сервис таймеров (ConcurrentDictionary + Task.Delay)
│   ├── ITimerService.cs            # Контракт сервиса таймеров
│   ├── ActiveTimer.cs              # Модель активного таймера
│   ├── LlmIntentService.cs         # LLM-интерпретатор команд + быстрый TryFastMatch (0 мс)
│   ├── CompositeVoiceFeedbackService.cs # Гибридный оркестратор TTS (Edge -> Silero -> System.Speech)
│   ├── VoiceFeedbackService.cs     # Обратная совместимость и мост к CompositeVoiceFeedbackService
│   ├── TTS/
│   │   ├── ITtsEngine.cs           # Контракт ядра TTS (Name, IsAvailable, SpeakAsync)
│   │   ├── EdgeTtsEngine.cs        # Primary: Edge Neural TTS (ru-RU-DmitryNeural + NAudio MP3)
│   │   ├── SileroTtsEngine.cs      # Secondary: Silero ONNX Runtime (aidar/eugene + PCM)
│   │   └── SystemSpeechTtsEngine.cs # Fallback: Windows SAPI (System.Speech)
│   ├── VoiceListener.cs            # Фоновый слушатель микрофона (Vosk + NAudio WaveIn)
│   ├── MediaKeyService.cs          # Быстрый Win32 SendInput диспетчер медиа-клавиш (0 мс)
│   └── JarvisResponse.cs           # DTO ответа { commandRequest, reply }
├── Win32/
│   ├── NativeStructs.cs            # Структуры Win32: INPUT, KEYBDINPUT, MOUSEINPUT, VK_*
│   ├── NativeMethods.cs            # P/Invoke импорты user32.dll (SendInput, MapVirtualKey)
│   └── KeyboardHook.cs             # Мост эмуляции клавиатурных мультимедиа-событий
├── Voice/
│   └── VoskModelHelper.cs          # Проверка наличия и автоматическая загрузка модели Vosk
├── appsettings.json                # Конфигурация LLM (BaseUrl, ApiKey, Model)
├── Program.cs                      # Точка входа: регистрация команд, REPL, тесты
└── Gem.csproj                      # Файл проекта (.NET 8 Windows, NAudio, Vosk, System.Speech)
```

---

## 🚀 Формат вызова команд через JSON

Все команды передаются в строковом формате JSON:
```json
{
  "command": "<имя_команды>",
  "args": { ... }
}
```

Диспетчер возвращает объект `CommandResult`:
```json
{
  "success": true,
  "message": "Описание результата",
  "data": { ... }
}
```

---

### 1. Управление системной громкостью (`"command": "volume"`)

Реализовано через **NAudio CoreAudioApi** (`MMDeviceEnumerator`, `AudioEndpointVolume`).

* **Установка конкретного уровня (0–100%):**
  ```json
  { "command": "volume", "args": { "action": "set", "level": 40 } }
  ```
* **Относительное изменение громкости (delta):**
  ```json
  { "command": "volume", "args": { "action": "change", "delta": 10 } }
  { "command": "volume", "args": { "action": "change", "delta": -5 } }
  ```
* **Включение / выключение звука (Mute):**
  ```json
  { "command": "volume", "args": { "action": "mute", "isMuted": true } }
  { "command": "volume", "args": { "action": "mute", "isMuted": false } }
  ```
  *(Если `isMuted` не указан, состояние переключается на противоположное).*
* **Запрос текущего статуса:**
  ```json
  { "command": "volume", "args": { "action": "get" } }
  ```

---

### 2. Запуск и завершение приложений (`"command": "app"`)

Реализовано через `System.Diagnostics.Process` с поддержкой `UseShellExecute = true`, путей по умолчанию, протоколов и маппинга частых фонетических искажений модели Vosk:
* **Steam**: `"steam"`, `"стим"`, `"tim"`, `"тим"` $\to$ запуск по пути `"C:\Program Files (x86)\Steam\steam.exe"` или через протокол `"steam://open/main"`.
* **Discord**: `"discord"`, `"дискорд"`, `"диск"` $\to$ запуск из `%LocalAppData%\Discord\Update.exe` (`--processStart Discord.exe`) или `app-*/Discord.exe`.
* **Telegram**: `"telegram"`, `"телега"`, `"телеграм"` $\to$ запуск из `%AppData%\Telegram Desktop\Telegram.exe` или протокол `"tg://"`.
* **Chrome**: `"chrome"`, `"хром"`, `"браузер"` $\to$ запуск из `Program Files` или `chrome`.
* **Notepad**: `"notepad"`, `"блокнот"` $\to$ запуск `"notepad"`.
* **Calculator**: `"calc"`, `"калькулятор"` $\to$ запуск `"calc"`.

* **Запуск приложения:**
  ```json
  { "command": "app", "args": { "action": "start", "name": "steam" } }
  { "command": "app", "args": { "action": "start", "name": "tim" } }
  { "command": "app", "args": { "action": "start", "name": "дискорд" } }
  { "command": "app", "args": { "action": "start", "name": "телега" } }
  ```
* **Закрытие приложения:**
  ```json
  { "command": "app", "args": { "action": "close", "name": "steam", "force": false } }
  { "command": "app", "args": { "action": "close", "name": "discord" } }
  ```
  *(Сначала отправляет запрос на вежливое закрытие главного окна `CloseMainWindow()`, при неудаче или `force: true` завершает процесс через `Kill()`)*.
* **Проверка статуса процесса:**
  ```json
  { "command": "app", "args": { "action": "status", "name": "steam" } }
  ```

---

### 3. Симуляция горячих клавиш (`"command": "hotkey"`)

Реализовано через Windows API **P/Invoke `SendInput`** с поддержкой 64-битной разметки структур `INPUT` / `KEYBDINPUT` и отказоустойчивым резервным вызовом при ограничениях безопасности Windows (UIPI).

* **Сочетание клавиш (нажатие в прямом порядке, отпускание в обратном):**
  ```json
  { "command": "hotkey", "args": { "keys": ["ctrl", "shift", "esc"], "delayMs": 50 } }
  { "command": "hotkey", "args": { "keys": ["win", "d"] } }
  { "command": "hotkey", "args": { "keys": ["alt", "tab"] } }
  ```
* **Одиночные клавиши:**
  ```json
  { "command": "hotkey", "args": { "key": "enter" } }
  { "command": "hotkey", "args": { "key": "volume_up" } }
  { "command": "hotkey", "args": { "key": "f5" } }
  ```
* **Поддерживаемые клавиши:**
  - Модификаторы: `ctrl`, `alt`, `shift`, `win` (lwin/rwin)
  - Символы: `a`-`z`, `0`-`9`
  - Управление: `enter`, `esc`, `tab`, `space`, `backspace`, `delete`, `insert`, `home`, `end`, `pageup`, `pagedown`, стрелки `up`, `down`, `left`, `right`
  - Функциональные: `f1`-`f12`
  - Мультимедиа: `volume_up`, `volume_down`, `volume_mute`, `media_play_pause`, `media_next`, `media_prev`

### 5. Команды таймера и напоминаний (`timer`)
* **Установка таймера / напоминания:**
  ```json
  { "command": "timer", "args": { "action": "set", "seconds": 300, "label": "выключить плиту" } }
  ```
* **Отмена таймеров:**
  ```json
  { "command": "timer", "args": { "action": "cancel" } }
  ```
* **Статус активного таймера:**
  ```json
  { "command": "timer", "args": { "action": "status" } }
  ```
* **Голосовые шаблоны (FastMatch < 5 мс):**
  - «поставь таймер на 10 секунд», «заведи таймер на полтора часа», «включи таймер на минуту»
  - «на 30 секунд», «на 5 минут», «на 1 час», «на полчаса»
  - «напомни через 15 минут выключить плиту»
  - «отмени таймер», «сбрось таймер», «выключи таймер»
  - «сколько осталось», «статус таймера», «что с таймером»

---

## 🎙️ Фоновый слушатель микрофона (`VoiceListener` на базе Vosk)

Класс `Gem.Voice.VoiceListener` обеспечивает постоянное распознавание речи в отдельном потоке:
1. **Постоянный захват аудио:** NAudio `WaveInEvent` (PCM 16 kHz, 16-bit Mono) с буфером 50 мс.
2. **Вейк-ворд:** слушатель находится в режиме ожидания до тех пор, пока не услышит ключевое слово **«джарвис»** (или «jarvis»). При распознавании генерируется событие:
   ```csharp
   voiceListener.OnWakeWordDetected += (wakeWord) => { ... };
   ```
3. **Silence Detection (детектор конца фразы):**
   - После фиксации вейк-ворда слушатель переходит в состояние `ListeningForCommand`.
   - Непрерывно вычисляет RMS-энергию входящего аудиопотока и отслеживает акустические границы Vosk (`AcceptWaveform`).
   - Если после произнесения команды фиксируется пауза тишины (по умолчанию 1.3 секунды), фраза считается завершённой.
4. **Событие команды:**
   ```csharp
   voiceListener.OnCommandSpoken += (commandText) =>
   {
       Console.WriteLine($"Распознана фраза: {commandText}");
   };
   ```

### Установка модели Vosk
Для работы офлайн-распознавания требуется модель Vosk. 
Вы можете загрузить её одной командой прямо через приложение:
```bash
dotnet run -- --download-model
```
Скрипт автоматически скачает полноразмерную высокоточную модель `vosk-model-ru-0.42` (~1.5 ГБ) и распакует её в директорию `./model`.

> **⚠️ Внимание:** Для загрузки и распаковки потребуется ~3.5 ГБ свободного дискового пространства.
> Процесс займёт несколько минут — в консоли отображается прогресс с оценкой скорости загрузки.

---

## 🛠️ Сборка и запуск

1. Сборка проекта:
   ```bash
   dotnet build
   ```
2. Интерактивный запуск:
   ```bash
   dotnet run
   ```
   *При запуске автоматически прогоняется демонстрационный набор команд, после чего открывается интерактивная консоль `JSON > `.*
