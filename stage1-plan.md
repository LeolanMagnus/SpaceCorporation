# Этап 1 — Фундамент + оболочка дебаг-режима (0.5 недели)

Источник: `prototype-plan.md`, раздел «Этап 1».
Окружение: Godot **4.7.2-stable.mono** (проверено локально), язык — **C# (.NET)**.

**Цель этапа:** пустая сцена, в которой уже есть работающая «приборная панель» для отладки
всех последующих механик. К концу этапа любой будущий механизм подключается одной строкой:
`debugUi.RegisterDebugButton("...", () => { ... });`

**Критерий готовности (из исходного плана):** F3 → появляется кнопка «Debug Mode» →
по клику открывается панель; `RegisterDebugButton` работает — тестовая кнопка печатает в консоль.

> **СТАТУС: этап выполнен** (код в репо, коммит `dad5287` + последующие).
> Автотест `tests/Stage1Test.tscn` — **50 проверок, 0 падений** (`./run-tests.sh`).
> Отклонения от плана в конце файла («Итоги этапа»).

---

## Шаг 1.0 — Создать проект (0.5 ч)

- [x] `project.godot` в корне рабочей директории (`F:\petprojects\SpaceCorporation`), тип **.NET**
      (в проекте должны появиться `SpaceCorporation.csproj` и solution).
- [x] Стартовая сцена: `scenes/Main.tscn`.
- [x] `Main.tscn` (корень — `Node3D` "Main"):
      - `WorldEnvironment` + `DirectionalLight3D` (чтобы геометрия была видна),
      - `Camera3D` (временно, без управления — до этапа 2),
      - `DebugLayer` (CanvasLayer) — из шага 1.2.
- [x] `dotnet build` проходит; F5 в редакторе открывает сцену без ошибок.

**Результат:** проект открывается, сцена рендерится, консоль чистая.

## Шаг 1.1 — Физика: невесомость и слои коллизий (1 ч)

- [x] `project.godot` → `physics/3d/default_gravity = 0.0` (глобальная гравитация выключена).
- [x] Именованные слои коллизий (Project Settings → Layer Names → 3D Physics):
      `1=Ship`, `2=Station`, `3=Character`, `4=Target`, `5=DockingZone`, `6=DebugHelper`.
- [x] Зафиксировать номера в `scripts/core/CollisionLayers.cs` (константы-степени двойки),
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
- [x] Заглушка-стенд: один `StaticBody3D` + `BoxMesh`/`BoxShape3D` на слое Station
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
- [x] Кнопка в левом верхнем углу, текст зелёный (`AddThemeColorOverride`), надпись «Debug Mode»,
      изначально `Visible = false`.
- [x] **F3** — переключает видимость кнопки.
- [x] Клик по кнопке — показать/скрыть `PanelContainer` с `VBoxContainer`.
- [x] В панели: `Label` «Debug Info» (пустой) + `RegisterDebugButton(string label, Action callback)`:
      ```csharp
      public Button RegisterDebugButton(string label, Action callback)
      {
          var b = new Button { Text = label };
          b.Pressed += () => callback?.Invoke();
          _buttons.AddChild(b);
          return b;
      }
      ```
- [x] Тестовая кнопка «Test» → `GD.Print("debug button pressed")` — вызывается из `DebugLayer.Ready()`.

**Результат:** F3 → кнопка → панель; тестовая кнопка печатает в консоль.

## Шаг 1.3 — DebugInfo-апдейтер (1.5 ч)

- [x] В `DebugLayer._Process(double delta)` — метод `UpdateDebugInfo()`,
      который пишет в `LblDebugInfo.Text` (строка, обновляемая раз в кадр).
- [x] Реестр источников: `RegisterInfoSource(Func<string> provider)` — каждая система
      позже добавляет свой блок текста; апдейтер склеивает блоки через `\n`.
- [x] Пока выводить только то, что реально есть: FPS, позиция камеры, «gravity = 0».
      (Скорость корабля / HP / состояние стыковки появятся на этапах 2, 4, 5.)

**Результат:** в панели живая строка с FPS; добавление источника — одна строка кода.

## Шаг 1.4 — Ввод и конфликт с меню (1 ч)

- [x] InputMap: действие `debug_toggle` = **F3** (прописать в `project.godot`, а не хардкодом).
- [x] Пока панель открыта: `GetTree().Paused = true`, у `DebugLayer` — `ProcessMode = Always`,
      чтобы меню жило на паузе (смягчение риска «Дебаг-меню конфликтует с вводом»).
- [x] `MouseFilter = Stop` только на самих контролах (кнопка/панель), фон — `Ignore`,
      чтобы клики не проглатывались на весь экран.
- [x] F3 при открытом меню закрывает панель и снимает паузу.

**Результат:** F3 полностью управляет режимом; игра на паузе, UI работает.

## Шаг 1.5 — Тестовый корабль-заглушка (1.5 ч, опционально, но полезно)

Нужен, чтобы к этапу 2 не переделывать сцену.

- [x] `PlayerShip` — `RigidBody3D` + `BoxMesh` + `CollisionShape3D`, `GravityScale = 0`,
      `LinearDamp = 0`, `AngularDamp = 1.5`, слой Ship / маска Station+Target.
- [x] `scripts/core/DebugInfoBus.cs` — сингловый узел-«шина», через который системы
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

- [x] F3 показывает/скрывает кнопку «Debug Mode» (зелёный текст, левый верхний угол).
- [x] Клик по кнопке открывает и закрывает панель с `VBoxContainer`.
- [x] `RegisterDebugButton("Test", ...)` — одна строка: кнопка появляется и печатает в консоль.
- [x] Строка `DebugInfo` обновляется каждый кадр, источники добавляются без правки `DebugLayer`.
- [x] Глобальная гравитация = 0: `RigidBody3D` с `GravityScale = 0` не падает.
- [x] Слои 1–6 именованы и доступны из C# через константы.
- [x] При открытом дебаг-менее игра на паузе, но UI реагирует на клики.

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

---

## Итоги этапа (что реально сделано)

Фактический набор файлов:

```
project.godot                 // gravity = 0, debug_toggle = F3, 6 именованных слоёв, autoload DebugInfoBus
SpaceCorporation.csproj       // Godot.NET.Sdk/4.7.2, net8.0
scenes/Main.tscn              // WorldEnvironment, DirectionalLight3D, Camera3D, TestStand, PlayerShip, DebugLayer
scenes/DebugLayer.tscn        // CanvasLayer + скрипт (весь UI строится кодом в BuildUi)
scripts/core/CollisionLayers.cs
scripts/core/DebugInfoBus.cs  // autoload-сингл: шина кнопок и debug-info
scripts/core/ShipStub.cs      // корабль-заглушка + кнопка «Nudge Ship»
scripts/debug/DebugLayer.cs   // F3, кнопка «Debug Mode», панель, RegisterDebugButton, UpdateDebugInfo
tests/Stage1Test.cs / .tscn   // автотест приёмки (50 проверок)
run-tests.sh                  // dotnet build + godot --headless прогон теста
```

Отличия и дополнения к плану:

1. **UI строится кодом, а не в `.tscn`.** `DebugLayer.tscn` содержит только корень `CanvasLayer`
   со скриптом; `DebugRoot / BtnDebugMode / DebugPanel / VBox / LblDebugInfo / Buttons` создаются
   в `BuildUi()`. Причина: кнопки из `RegisterDebugButton` всё равно создаёт код, а UI, описанный
   и в `.tscn`, и в коде, расходится при правках. Пути к узлам соответствуют дереву из шага 1.2.
2. **`DebugInfoBus` сделан autoload-синглом уже на шаге 1.2** (в плане он значился для 1.5).
   Это нужно, чтобы системы этапов 3–6 регистрировали кнопки и строки до/без UI: шина копит
   запросы и выгружает их, когда `DebugLayer` готов.
3. **Имена API приведены к плану:** `DebugLayer.RegisterDebugButton(label, callback)`,
   `DebugInfoBus.RegisterDebugButton(...)` / `RegisterInfoSource(key, provider)`.
   `RegisterInfoSource` принимает ключ + `Func<string>` (ключа в плане не было): повторная
   регистрация по тому же ключу заменяет блок, поэтому системы могут перерегистрироваться
   после рестарта сцены.
4. **Открытая панель ставит дерево на паузу** (`GetTree().Paused = true`) — закрытие риска
   «конфликт с вводом» из шага 1.4. Проверено тестом: на паузе DebugInfo продолжает обновляться.
5. **Godot 4.7.2:** свойство `RigidBody3D` называется `CustomIntegrator` (bool), а не
   `CustomIntegratorMode`; на этапе 1 не нужен, оставлен комментарий под этап 5 (пружина стыковки).
6. **`TestStand`** (StaticBody3D, слой Station) и **`PlayerShip`** (шаг 1.5) живут в `Main.tscn`;
   у корабля уже есть узел `Muzzle` под лазеры этапа 3.

Как проверить:

```
godot --path .        # игра: F3 → «Debug Mode» → панель → кнопки Test / BusButton / Nudge Ship
./run-tests.sh        # dotnet build + 50 проверок приёмки, exit code 0
```
