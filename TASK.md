# TASK: [53_Adaptive_WakeWord_Silero_Autodownload_And_Tts_Stabilization]

## Статус: ✅ Выполнено

---

## Подзадачи

- [x] 1. Инициализация `TASK.md` и аудит зависимостей
- [x] 2. Создание интерфейса `IWakeWordDetector` в `Voice/WakeWord/`
- [x] 3. Реализация `OpenWakeWordDetector` (ONNX Runtime, `jarvis.onnx`, автозагрузка, 50–80 мс)
- [x] 4. Реализация `VoskGrammarWakeWordDetector` (малая модель Vosk, грамматика `["{customName}", "[unk]"]`)
- [x] 5. Создание фабрики `WakeWordFactory`
- [x] 6. Рефакторинг `VoiceListener.cs` (интеграция `IWakeWordDetector`, полное устранение звуковых сигналов/бипов)
- [x] 7. Доработка `SileroTtsEngine.cs` (автозагрузка `Models/Silero/ru_v3.onnx` с консольным прогресс-баром, мужской голос `aidar`)
- [x] 8. Доработка `SystemSpeechTtsEngine.cs` (полный запрет женского голоса Ирины, мужской голос / занижение питча)
- [x] 9. Стабилизация `EdgeTtsEngine.cs` (быстрый Reconnect 400 мс, Keep-Alive пинг WebSocket)
- [x] 10. Актуализация конфигурации: `appsettings.json`, `AppSettingsService.cs`, `Program.cs`
- [x] 11. Обновление `.gitignore` (модели, архивы, ONNX)
- [x] 12. Верификация сборки: `dotnet build`
- [x] 13. Синхронизация документации: `ARCHITECTURE.md` и `README.md`
- [x] 14. Формирование аккуратного локального коммита в Git (БЕЗ `git push`)
- [x] 15. Финальный отчёт и Walkthrough
