# TASK: [52] Актуализация README.md и пуш на GitHub

## Статус: 🔄 В работе

---

## Подзадачи

- [x] Аудит `ARCHITECTURE.md` — проверка актуальности (актуален, изменений не требует)
- [x] Аудит `README.md` — выявление расхождений с реальной кодовой базой
- [x] Составление Implementation Plan и получение подтверждения
- [x] Полная перезапись `README.md` под текущую архитектуру:
  - [x] Заголовок, описание, бейджи
  - [x] Дерево файлов проекта (актуальное)
  - [x] Раздел STT (Vosk ru-0.42, FSM, бесшовный захват)
  - [x] Раздел Dual-Path Routing (7 приоритетов, < 5 мс)
  - [x] Раздел TTS (Edge → Silero → System.Speech)
  - [x] Раздел Steam-автоматизация (Fuzzy, VDF/ACF, Pixel Scan)
  - [x] Раздел FSM-подтверждения (PendingActionState)
  - [x] Раздел WPF HUD Overlay (состояния Idle/Listening/Thinking/Action)
  - [x] Таблица команд JSON-интерфейса
  - [x] Установка, сборка, запуск
- [ ] Проверка `git status` (нет лишних файлов)
- [ ] `git add README.md ARCHITECTURE.md TASK.md`
- [ ] `git commit -m "docs: synchronize README project structure and core features with latest architecture"`
- [ ] `git push origin main`
- [ ] Верификация: `git log --oneline -1` и `working tree clean`
