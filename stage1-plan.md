# Этап 1 — Фундамент + оболочка дебаг-режима (0.5 недели)

Источник: `prototype-plan.md`, раздел «Этап 1».
Окружение: Godot **4.7.2-stable.mono** (проверено локально), язык — **C# (.NET)**.

**Цель этапа:** пустая сцена, в которой уже есть работающая «приборная панель» для отладки
всех последующих механик. К концу этапа любой будущий механизм подключается одной строкой:
`debugUi.RegisterDebugButton("...", () => { ... });`

**Критерий готовности (из исходного плана):** F3 → появляется кнопка «Debug Mode» →
по клику открывается панель; `RegisterDebugButton` работает — тестовая кнопка печатает в консоль.

---

## Шаг 1.0 — Создать проект (0.5 ч)

- [ ] `project.godot` в корне рабочей директории (`F:\petprojects\SpaceCorporation`), тип **.NET**
      (в проекте должны появиться `SpaceCorporation.csproj` и solution).
- [ ] Стартовая сцена: `scenes/Main.tscn`.
- [ ] `Main.tscn` (корень — `Node3D` "Main"):
      - `WorldEnvironment` + `DirectionalLight3D` (чтобы геометрия была видна),
      - `Camera3D` (временно, без управления — до этапа 2),
      - `DebugLayer` (CanvasLayer) — из шага 1.2.
- [ ] `dotnet build` проходит; F5 в редакторе открывает сцену без ошибок.

**Результат:** проект открывается, сцена рендерится, консоль чистая.

## Шаг 1.1 — Физика: невесомость и слои коллизий (1 ч)

- [ ] `project.godot` → `physics/3d/default_gravity = 0.0` (глобальная гравитация выключена).
- [ ] Именованные слои коллизий (Project Settings → Layer Names → 3D Physics):
      `1=Ship`, `2=Station`, `3=Character`, `4=Target`, `5=DockingZone`, `6=DebugHelper`.
- [ ] Зафиксировать номера в `scripts/core/CollisionLayers.cs` (константы-степени двойки),
      чтобы в коде не писать magic-числа:
      ```csharp
      public static class CollisionLayers
      {
          public const uint Ship        = 1u << 0;
          public const uint Station     = 1u << 1;
          public const uint Character   = 1u << 2;
          public const uint Target      = 1u << 3;
          public const uint DockingZone = 1u << 4;
          public const uint DebugHelper = 1u << 5;
      }
      ```
- [ ] Заглушка-стенд: один `StaticBody3D` + `BoxMesh`/`BoxShape3D` на слое Station
      (временно, для проверки коллизий; на этапе 4 заменяется станцией).

**Результат:** тело, брошенное в сцену, не падает; слои видны в инспекторе.

## Шаг 1.2 — DebugLayer: оболочка UI (3–4 ч) — ядро этапа

Файл: `scripts/debug/DebugLayer.cs` (класс `CanvasLayer`), сцена `scenes/DebugLayer.tscn`.

Структура узлов:
```
DebugLayer (CanvasLayer, Layer = 10, ProcessMode = Always)
└── DebugRoot (Control, anchors = full rect, MouseFilter = Ignore)
    ├── BtnDebugMode (Button)          // левый верхний угол, зелёный текст, «Debug Mode»
    └── DebugPanel (PanelContainer)    // изначально Visible = false
        └── VBox (VBoxContainer)
            ├── LblDebugInfo (Label)   // «Debug Info», изначально пустой
            └── Buttons (VBoxContainer) // сюда добавляются кнопки из RegisterDebugButton
```

Требования из плана:
- [ ] Кнопка в левом верхнем углу, текст зелёный (`AddThemeColorOverride`), надпись «Debug Mode»,
      изначально `Visible = false`.
- [ ] **F3** — переключает видимость кнопки.
- [ ] Клик по кнопке — показать/скрыть `PanelContainer` с `VBoxContainer`.
- [ ] В панели: `Label` «Debug Info» (пустой) + `RegisterDebugButton(string label, Action callback)`:
      ```csharp
      public Button RegisterDebugButton(string label, Action callback)
      {
          var b = new Button { Text = label };
          b.Pressed += () => callback?.Invoke();
          _buttons.AddChild(b);
          return b;
      }
      ```
- [ ] Тестовая кнопка «Test» → `GD.Print("debug button pressed")` — вызывается из `DebugLayer.Ready()`.

**Результат:** F3 → кнопка → панель; тестовая кнопка печатает в консоль.

## Шаг 1.3 — DebugInfo-апдейтер (1.5 ч)

- [ ] В `DebugLayer._Process(double delta)` — метод `UpdateDebugInfo()`,
      который пишет в `LblDebugInfo.Text` (строка, обновляемая раз в кадр).
- [ ] Реестр источников: `RegisterInfoSource(Func<string> provider)` — каждая система
      позже добавляет свой блок текста; апдейтер склеивает блоки через `\n`.
- [ ] Пока выводить только то, что реально есть: FPS, позиция камеры, «gravity = 0».
      (Скорость корабля / HP / состояние стыковки появятся на этапах 2, 4, 5.)

**Результат:** в панели живая строка с FPS; добавление источника — одна строка кода.

## Шаг 1.4 — Ввод и конфликт с меню (1 ч)

- [ ] InputMap: действие `debug_toggle` = **F3** (прописать в `project.godot`, а не хардкодом).
- [ ] Пока панель открыта: `GetTree().Paused = true`, у `DebugLayer` — `ProcessMode = Always`,
      чтобы меню жило на паузе (смягчение риска «Дебаг-меню конфликтует с вводом»).
- [ ] `MouseFilter = Stop` только на самих контролах (кнопка/панель), фон — `Ignore`,
      чтобы клики не проглатывались на весь экран.
- [ ] F3 при открытом меню закрывает панель и снимает паузу.

**Результат:** F3 полностью управляет режимом; игра на паузе, UI работает.

## Шаг 1.5 — Тестовый корабль-заглушка (1.5 ч, опционально, но полезно)

Нужен, чтобы к этапу 2 не переделывать сцену.

- [ ] `PlayerShip` — `RigidBody3D` + `BoxMesh` + `CollisionShape3D`, `GravityScale = 0`,
      `LinearDamp = 0`, `AngularDamp = 1.5`, слой Ship / маска Station+Target.
- [ ] `scripts/core/DebugInfoBus.cs` — сингловый узел-«шина», через который системы
      отдают текст в debug-info (чтобы UI не знал про них).

**Результат:** корабль висит в невесомости; панель его видит через `RegisterInfoSource`.

---

## Порядок работы (порционные инкременты)

1. 1.0 → 1.1 → запуск: сцена, невесомость, слои. **Commit.**
2. 1.2 → запуск: F3 / кнопка / панель / тестовая кнопка. **Commit.**
3. 1.3 → запуск: живой DebugInfo. **Commit.**
4. 1.4 → запуск: пауза + переключение по F3. **Commit.**
5. 1.5 → запуск: корабль-заглушка в невесомости. **Commit.**

## Что должно быть в репозитории по итогам этапа

```
project.godot            // gravity = 0, input map (debug_toggle = F3), слои коллизий
SpaceCorporation.csproj
scenes/Main.tscn
scenes/DebugLayer.tscn
scripts/core/CollisionLayers.cs
scripts/core/DebugInfoBus.cs
scripts/debug/DebugLayer.cs
```

## Чек-лист приёмки этапа

- [ ] F3 показывает/скрывает кнопку «Debug Mode» (зелёный текст, левый верхний угол).
- [ ] Клик по кнопке открывает и закрывает панель с `VBoxContainer`.
- [ ] `RegisterDebugButton("Test", ...)` — одна строка: кнопка появляется и печатает в консоль.
- [ ] Строка `DebugInfo` обновляется каждый кадр, источники добавляются без правки `DebugLayer`.
- [ ] Глобальная гравитация = 0: `RigidBody3D` с `GravityScale = 0` не падает.
- [ ] Слои 1–6 именованы и доступны из C# через константы.
- [ ] При открытом дебаг-менее игра на паузе, но UI реагирует на клики.

## Что сознательно НЕ делаем на этапе 1

Управление кораблём (этап 2), стрельба и мишени (3), станция и урон (4), стыковка (5),
персонаж и магнитные ботинки (6), камера от первого лица, ассеты и «красота».

## Риски этапа и как их закрыть

| Риск | Закрытие |
|---|---|
| F3 пересекается с игровыми действиями | InputMap-действие + пауза дерева при открытом меню, `ProcessMode = Always` |
| `Control`-фон глотает клики по игре | `MouseFilter = Ignore` на контейнере, `Stop` на кнопке/панели |
| UI-текст правится в каждой системе | `RegisterInfoSource` / `DebugInfoBus` — системы не знают про UI |
| C#-класс не привязан к узлу | `.csproj` создан, класс наследует `Godot.Node`, имя файла = имя класса |

## Оценка

Шаги 1.0–1.4: ~7.5 ч (это и есть «0.5 недели» из исходного плана). Шаг 1.5 — запас, ещё 1.5 ч.

## Связь с этапами 2–7

Задел, который дальше не переделывается:
- `RegisterDebugButton` → кнопки Spawn Target / Clear Targets (3), Toggle God Mode (4),
  Teleport to Docking Zone (5), Toggle Boots / Teleport to Platform (6).
- `RegisterInfoSource` → скорость и угловая скорость (2), HP (4), DockingState (5),
  состояние ботинок (6).
- Слои коллизий → маски RayCast3D для стрельбы (3) и магнитных ботинок (6).
