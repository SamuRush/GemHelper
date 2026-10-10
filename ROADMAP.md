# JARVIS — Project Roadmap & Autonomous Backlog

## Архитектурные принципы и инварианты
- **Fast Command Processing (0 ms, без LLM)**: системные действия, звук, медиа, таймеры, запуск системных утилит.
- **Cognitive Layer (LLM / LM Studio / Qwen)**: свободный диалог, извлечение сложных параметров, контекст диалога.
- **Fail-Safe TTS**: приоритет стабильности речи над облачными сервисами; таймаут сетевого синтеза не более 1.5 с с мгновенным бесшовным откатом на SAPI5.
- **DPI- и Theme-независимая автоматизация**: отказ от жестких попиксельных проверок в пользу Win32 UI Automation.
- **Строгий TDD-пайплайн**: ни одна задача не закрывается без покрывающих тестов в `Gem.Tests/` и успешного прогона `dotnet test Gem.sln`.

---

## 🎯 Автономная очередь спринта (Active Autonomous Queue)
*Агент берет первую задачу со статусом `[ ]`, реализует тесты, код, добивается `dotnet test` (exit code 0), ставит `[x]` и коммитит.*

### [x] Task-01: Стабилизация TTS — Fast Failover и отсечение зависаний Edge-TTS
- **Проблема**: WebSocket-клиент Edge-TTS периодически зависает на 5–10 секунд из-за ротации токенов Microsoft или микросбоев сети, блокируя цикл ответа ассистента.
- **Зона изменений**: `Services/TTS/EdgeTtsEngine.cs`, `Services/CompositeVoiceFeedbackService.cs`, `Gem.Tests/TtsFailoverTests.cs`.
- **Технические требования**:
  1. В `EdgeTtsEngine`: сократить `ConnectionTimeoutMs` до `1500` мс. 
  2. Ввести `CancellationTokenSource` с жестким тайм-лимитом `2000` мс на получение первого чанка аудиоданных. При превышении — немедленно прерывать сокет без повторных долгих ретраев.
  3. В `CompositeVoiceFeedbackService`: при срыве Edge-TTS мгновенно переключаться на `SystemSpeechTtsEngine` (токен `Aidar (Russian)` / `Microsoft Pavel`) с логированием предупреждения без выбрасывания необработанных исключений наружу.
  4. Написать юнит-тест `TtsFailoverTests.cs`, симулирующий таймаут/обрыв сокета и проверяющий, что полный откат на SAPI5 занимает не более 1.6 с.
- **Definition of Done**: `dotnet test Gem.sln` проходит успешно; синтез речи при отключенной сети переключается на SAPI5 без ощутимой паузы.

---

### [x] Task-02: Модернизация UI HUD — Реактивная волна, анимации состояний и оверлей таймеров
- **Проблема**: WPF HUD окно статично («для галочки»), отсутствует визуальная реакция на речь, титры и статус фоновых задач.
- **Зона изменений**: `UI/` (XAML/Code-behind), `Services/VoiceListener.cs`, `Services/TimerService.cs`.
- **Технические требования**:
  1. **RMS Waveform / Пульсация**: пробросить событие текущего уровня энергии звука `OnRmsLevelChanged(float rms)` из `VoiceListener` в UI потокобезопасно через `Dispatcher`. Привязать масштаб/прозрачность неонового кольца к уровню входящего сигнала (анимация «дыхания» при речи пользователя).
  2. **Анимации перехода (WPF Storyboards)**: плавное морфинг-свечение (DropShadowEffect) между состояниями:
     - `Idle`: мягкий циан (`#00E5FF`);
     - `Listening`: золотой/янтарный (`#FFD600`);
     - `Thinking`: фиолетовый (`#D500F9`);
     - `Action`: изумрудный (`#00E676`).
  3. **Титры распознавания речи**: плашка в нижней части HUD для вывода `Vosk PartialResult` и `FinalResult` в реальном времени с автоматическим Fade-out через 3 секунды тишины.
  4. **Мини-панель активных таймеров**: компактный счетчик (обратный отсчет ММ:СС) в правом верхнем углу виджета при наличии активных таймеров в `TimerService`.
- **Definition of Done**: Проект компилируется без предупреждений; при запуске оверлей корректно реагирует на голос и отображает состояния.

---

### [x] Task-03: Ликвидация техдолга и декомпозиция монолитного Program.cs
- **Проблема**: `Program.cs` раздут до 115 КБ. В одном файле смешаны DI-регистрация, консольные CLI-утилиты скачивания моделей, обработчики горячих клавиш и глобальные перехватчики ошибок.
- **Зона изменений**: `Program.cs`, `Core/`, `Services/ServiceCollectionExtensions.cs`.
- **Технические требования**:
  1. Вынести конфигурацию DI-контейнера в `Services/ServiceCollectionExtensions.cs` (`AddJarvisCore()`, `AddJarvisHandlers()`, `AddJarvisTts()`).
  2. Вынести CLI-команды первого запуска (`--download-model`, `--test-features`) в отдельный класс `Core/CliRunner.cs`.
  3. Устранить антипаттерн прямого обращения к синглтонам через `.Instance` в классах `Handlers/` — перевести все зависимости на инжекцию через конструкторы.
  4. Сократить размер `Program.cs` до компактного файла точки входа (не более 15–20 КБ).
- **Definition of Done**: `dotnet build Gem.sln` и `dotnet test Gem.sln` выполняются без ошибок; все зависимости регистрируются через `IServiceProvider`.

---

### [x] Task-04: Рефакторинг кликера Steam — замена попиксельного сканирования на UI Automation
- **Проблема**: Метод `FindSteamBlueButtonInWindow` использует `Bitmap.CopyFromScreen` и захардкоженные RGB-диапазоны. Метод ломается при смене темы Steam, включении HDR или нестандартном DPI (125%/150%).
- **Зона изменений**: `Services/SteamService.cs`, `Win32/`.
- **Технические требования**:
  1. Реализовать поиск модального диалога Steam через `UIAutomationClient` / `AutomationElement` по структуре окна и типам элементов (`ControlType.Button` с именами «Удалить», «Uninstall», «Установить», «Install»).
  2. Оставить попиксельный сканер `FindSteamBlueButtonInWindow` строго как вторичный fallback, если UI Automation не вернул элемент.
  3. Покрыть unit-тестами логику сопоставления заголовков окон и кнопок.
- **Definition of Done**: `dotnet test Gem.sln` проходит успешно; автоматизация деинсталляции корректно находит подтверждающий элемент независимо от масштабирования экрана.

---

### [x] Task-05: Модернизация окна настроек (Sci-Fi TabControl), диагностика аудио и редизайн HUD
- **Проблема**: Окно настроек монолитно, отсутствует диагностика микрофона (пользователь не видит входящий сигнал, высокий RMS-порог отсекает речь, невозможно сменить микрофон). HUD-виджет имеет лишний квадратный контейнер и шестеренку.
- **Зона изменений**: `UI/SettingsWindow.xaml`, `UI/SettingsWindow.xaml.cs`, `UI/OverlayWidget.xaml`, `UI/OverlayWidget.xaml.cs`, `Services/VoiceListener.cs`, `Services/AppSettingsService.cs`, `appsettings.json`, `Gem.Tests/`.
- **Технические требования**:
  1. **Диагностика и выбор микрофона**: поддержка выбора устройства через `NAudio.Wave.WaveInEvent.GetCapabilities()`, сохранение `Audio: { DeviceIndex, DeviceName }`, переключение устройства на лету в `VoiceListener`.
  2. **RMS Noise Gate**: слайдер порога (50–600, по умолчанию 350) с динамическим обновлением порога в рантайме.
  3. **VU-метр реального времени**: потокобезопасный `ProgressBar` уровня звука в `SettingsWindow`.
  4. **Выбор TTS и голоса**: выбор движка (Edge-TTS / SAPI5) и переключение голоса (Aidar, Microsoft Pavel, etc.).
  5. **Архитектура TabControl**: стилизованные темные Sci-Fi вкладки: «🎙 Аудио», «🧠 Искусственный интеллект», «🎮 Игры и Steam», «⚙️ Система» (город погоды, автозапуск, горячие клавиши).
  6. **Редизайн HUD**: окно строго круглой формы (`AllowsTransparency="True"`, `WindowStyle="None"`, `DropShadowEffect`), удаление квадратного контейнера и шестеренки, открытие настроек по правому клику мыши (`ContextMenu`).
- **Definition of Done**: `dotnet build Gem.csproj` и `dotnet test Gem.sln` завершаются без ошибок, настройки корректно сохраняются в `appsettings.json`.

---

### [x] Task-06: Переработка медиа-пайплайна — GSMTC, разделение видео/музыки и адресный роутинг
- **Проблема**: Слепая эмуляция VK_MEDIA_PLAY_PAUSE приводит к ложным включениям музыки при попытке поставить на паузу, отсутствует разделение сценариев управления видео в браузере/плеере и запуска Яндекс Музыки.
- **Зона изменений**: `Services/IMediaSessionService.cs`, `Services/MediaSessionService.cs`, `Handlers/MediaCommandHandler.cs`, `Handlers/AppControlCommandHandler.cs`, `Services/LlmIntentService.cs`, `Gem.Tests/MediaControlTests.cs`.
- **Технические требования**:
  1. **Интеграция с Windows Media Session (GSMTC)**: подключить `GlobalSystemMediaTransportControlsSessionManager` (WinRT / `Windows.Media.Control`) для опроса статуса (`Playing`, `Paused`, `Stopped`) и идемпотентного вызова Play/Pause/Stop без слепого тогла.
  2. **Сценарий «Запуск музыки»**: проверка запущенного процесса Яндекс Музыки (`YandexMusic`, `yandexmusic`, `Yandex.Music`), если нет — запуск через `yandexmusic://` или fallback в браузер `https://music.yandex.ru`, ожидание инициализации и старт воспроизведения. Если запущен — фокус окна и сигнал воспроизведения.
  3. **Сценарий «Управление видео»**: определение активного окна (`GetForegroundWindow`, `GetWindowText`, имя процесса). Если активен браузер (Chrome, Edge, Firefox, Yandex) или медиаплеер (VLC, MPC) — отправка `VK_SPACE` или адресная команда паузы.
  4. **Обновление роутера (`TryFastMatch`)**: регистрация контрактов «останови видео» (`pause_video`), «поставь на паузу» (`pause`), «продолжи видео» (`resume_video`), «включи яндекс музыку» (`play_music`) без костыльного маппинга в слепой `play_pause`.
  5. **Покрытие тестами**: создание `Gem.Tests/MediaControlTests.cs`, проверка всех сценариев и контрактов через `dotnet test Gem.sln`.
- **Definition of Done**: `dotnet build Gem.csproj` и `dotnet test Gem.sln` завершаются с кодом 0; все тесты проходят успешно.

---

### [x] Task-07: Изолированное управление громкостью плеера (AudioSession / WASAPI SimpleAudioVolume & Electron Multi-Process)
- **Проблема**: Команды изменения громкости музыки («музыка тише», «сделай музыку громче», «громкость музыки 50») изменяли глобальную мастер-громкость всей системы Windows, создавая конфликт с системными звуками, играми и Discord. Кроме того, мультипроцессный Electron (Яндекс Музыка) играет звук в дочернем подпроцессе, из-за чего поиск по главному PID окна не менял реальный звук.
- **Зона изменений**: `Services/IAppAudioService.cs`, `Services/AppAudioService.cs`, `Handlers/VolumeCommandHandler.cs`, `Services/MediaKeyService.cs`, `Services/LlmIntentService.cs`, `Services/ServiceCollectionExtensions.cs`, `Gem.Tests/PlayerVolumeControlTests.cs`.
- **Технические требования**:
  1. **Интеграция с AudioSessionManager2 (WASAPI / NAudio)**: опрос аудиосессий через `AudioSessionManager2.GetSessionEnumerator()` (`RefreshSessions()`), перебор всех активных сессий устройства без привязки к единственному главному PID.
  2. **Построение дерева процессов (Family PIDs)**: сбор родительских и дочерних PID через Win32 `Toolhelp32Snapshot` (`GetTargetFamilyPids`) для точного сопоставления аудиосессий внутренних рабочих процессов Electron.
  3. **Управление громкостью через `SimpleAudioVolume`**: установка `session.SimpleAudioVolume.Volume` и `session.SimpleAudioVolume.Mute` ко **всем** дочерним аудиосессиям приложения без модификации мастер-громкости Windows.
  4. **Альтернативный fallback и синхронизация слайдера для Electron**: отправка сочетаний клавиш `Ctrl+Up` / `Ctrl+Down` (`MediaKeyService.SendCtrlKeyCombination`) в активное или найденное окно Яндекс Музыки для синхронного сдвига внутреннего программного слайдера плеера.
  5. **Роутер `TryFastMatch`**: распознавание фраз «музыка тише», «музыка громче», «прибавь музыку», «убавь музыку», «громкость музыки 50», «громкость плеера 70», «видео громче», «видео тише» с пробросом `target: "player"` или `target: "video"` в команду `volume`.
  6. **Сохранение системных команд**: общие фразы «тише», «громче», «звук 50», «выключи звук» продолжают управлять мастер-громкостью системы (`target: "master"`).
  7. **Тестирование**: расширение тестов в `Gem.Tests/PlayerVolumeControlTests.cs` (247 тестов) и верификация через `dotnet test Gem.sln`.
- **Definition of Done**: `dotnet build Gem.csproj` и `dotnet test Gem.sln` проходят без ошибок; громкость плеера меняется изолированно от системной громкости как через WASAPI-сессии, так и с fallback-синхронизацией Electron UI.

---

## 📦 Завершенные фазы

### Фаза 1. Ядро и голосовой пайплайн [ЗАВЕРШЕНО]
- [x] Потоковый STT на базе Vosk с буферизацией (16 кГц, моно).
- [x] Бесшумный wake-word детектор (Vosk Grammar KWS + OpenWakeWord ONNX).
- [x] RMS Noise Gate (450.0) и акустический фильтр минимальной длительности (150 мс).
- [x] Двухконтурная блокировка микрофона (FSM Processing Lock & Acoustic Echo Suppression).
- [x] Конфигурация через `appsettings.json` (список имен, пути к моделям).

### Фаза 2. Интеграция со Steam [ЗАВЕРШЕНО]
- [x] Нечеткий поиск игр по транслитерации и нормализованным названиям (AppID mapping).
- [x] Индексация библиотек через реестр Windows и `libraryfolders.vdf`.
- [x] Деинсталляция игр Steam с обязательным подтверждением FSM («Вы действительно хотите удалить {GameTitle}, сэр?»).
- [x] Режим прямого ожидания ответа без вейк-ворда (`EnterConfirmationListening`).

### Фаза 3. Контроль среды (Environmental Control) [ЗАВЕРШЕНО]
- [x] Прямое управление мастер-громкостью Windows (`CoreAudioApi` / `IAudioEndpointVolume`).
- [x] Глобальные медиа-клавиши (Play/Pause, Next, Prev, Stop via `SendInput`).
- [x] 0 мс быстрый роутинг в `TryFastMatch()` без обращения к LLM.
- [x] Фоновые таймеры и напоминания (`TimerService.cs` + `TimerCommandHandler.cs`).

---

## 🚀 Будущие фазы развития

### Фаза 4. Системный роутер приложений и окон [В РАБОТЕ]
- [x] Базовый запуск системных программ (`browser`, `notepad`, `calc`, `explorer`, `discord`, `telegram`) через `AppControlCommandHandler`.
- [ ] Управление окнами через Win32 API («сверни все окна», «разверни браузер», «закрой текущее окно»).
- [ ] Завершение фоновых процессов с подтверждением через FSM («закрой Telegram»).

### Фаза 5. Когнитивный слой и диалоговая память [ПЛАНИРУЕТСЯ]
- [ ] Кратковременная скользящая контекстная память диалога (последние 3–5 реплик в `LlmIntentService`).
- [ ] Динамическое переключение провайдеров LLM (LM Studio / Ollama / OpenAI API) через `appsettings.json`.
- [ ] Умный fallback: при неоднозначной команде формировать вежливый уточняющий вопрос от лица Джарвиса.