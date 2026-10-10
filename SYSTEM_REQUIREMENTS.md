# JARVIS — Системные требования

> Актуально для версии проекта на 2026-10-10.

---

## Профили развёртывания

| Параметр | 🟡 Минимальный<br>*Small Model / Low RAM* | 🟢 Рекомендуемый<br>*Full Model / GPU Offload* |
|---|---|---|
| **ОС** | Windows 10 (1903+) x64 | Windows 10 (21H2+) / Windows 11 x64 |
| **CPU** | 4-ядерный, 2.0 ГГц | 6-ядерный, 3.0 ГГц+ |
| **ОЗУ** | 4 ГБ (процесс JARVIS: **≤ 350 МБ**) | 8 ГБ+ (unmanaged RAM Vosk: **≥ 1.5 ГБ**) |
| **GPU / WPF Rendering** | Software Rendering (Tier 0–1) — возможны фризы HUD | **Hardware Rendering Tier 2** (DirectX 9+, WDDM 1.0+) — обязателен для 60 FPS Arc Reactor |
| **VRAM (для GPU Offload)** | — | 4 ГБ+ VRAM (опционально, для LLM-бэкенда) |
| **Дисковое пространство** | 2 ГБ (модели + окружение) | 8 ГБ+ (модели + окружение + LLM) |
| **Vosk STT-модель** | `vosk-model-small-ru-0.22` | `vosk-model-ru-0.42` |
| **Потребление ОЗУ процессом (STT)** | **≤ 350 МБ** | **≥ 1 500 МБ** (unmanaged, вне GC .NET) |
| **Размер модели Vosk на диске** | ~45 МБ | ~1.8 ГБ |
| **Качество распознавания речи** | Базовое (достаточно для команд) | Высокое (large vocab, пунктуация) |
| **Python** | 3.10 x64 | 3.10–3.12 x64 |
| **.NET / WPF Runtime** | .NET 6 Desktop Runtime | .NET 8 Desktop Runtime |
| **Сеть** | Не требуется (offline STT) | Не требуется (offline STT); нужна для облачных LLM |

---

## Модели Vosk — подробности

### Минимальный профиль — `vosk-model-small-ru-0.22`

- Размер: **~45 МБ**
- Потребление ОЗУ процессом JARVIS: строго **≤ 350 МБ**
- Предназначена для работы на слабом железе и ноутбуках без дискретной GPU
- Скорость отклика: < 300 мс (команды длиной ≤ 5 слов)
- Скачать: <https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip>

### Рекомендуемый профиль — `vosk-model-ru-0.42`

- Размер: **~1.8 ГБ**
- Требует **≥ 1 500 МБ unmanaged RAM** (память выделяется вне управляемой кучи .NET; не учитывается в стандартных метриках Task Manager для managed-кода)
- Расширенный словарь, поддержка пунктуации, значительно выше точность
- Рекомендуется при работе с диктовкой и сложными командами
- Скачать: <https://alphacephei.com/vosk/models/vosk-model-ru-0.42.zip>

---

## Аппаратное ускорение WPF — Arc Reactor HUD

> [!IMPORTANT]
> Для бесфризовой работы процедурного **Arc Reactor HUD** на частоте **60 FPS** необходим **WPF Hardware Rendering Tier 2**.

### Что такое Hardware Rendering Tier 2

WPF определяет уровни аппаратного рендеринга через `RenderCapability.Tier`:

| Tier | Значение `RenderCapability.Tier >> 16` | Условие |
|---|---|---|
| Tier 0 | `0` | GPU отсутствует или не поддерживает DirectX 7 |
| Tier 1 | `1` | DirectX 7–8 (частичное ускорение) |
| **Tier 2** | **`2`** | **DirectX 9+, WDDM 1.0+ — полное GPU-ускорение** |

### Проверка текущего уровня (C#)

```csharp
int tier = RenderCapability.Tier >> 16;
// Ожидаемое значение для Arc Reactor HUD без фризов: 2
```

### Последствия при работе на Tier 0 / Tier 1

- Процедурные эффекты (шейдеры HLSL, `DrawingVisual`, анимация `DispatcherTimer`) переключаются в **Software Rendering**
- Загрузка CPU возрастает на 15–40 %
- Вероятны **фризы HUD** при FPS < 30 во время активных эффектов Arc Reactor

### Рекомендации

- Убедитесь, что установлены актуальные драйверы GPU (NVIDIA / AMD / Intel)
- В виртуальных машинах активируйте **3D-ускорение** (VMware / Hyper-V enhanced graphics)
- Не отключайте аппаратное ускорение через `DisableHWAcceleration` в реестре (`HKLM\SOFTWARE\Microsoft\Avalon.Graphics`)

---

## Быстрая проверка совместимости

```powershell
# ОЗУ
(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory / 1GB

# GPU и DirectX версия
dxdiag /t $env:TEMP\dxdiag.txt; Start-Sleep 3; Select-String "DDI Version" $env:TEMP\dxdiag.txt

# WPF Rendering Tier (из PowerShell через .NET)
Add-Type -AssemblyName PresentationCore
[System.Windows.Media.RenderCapability]::Tier -shr 16
```

---

*Документ сгенерирован и поддерживается в корне репозитория JARVIS.*
