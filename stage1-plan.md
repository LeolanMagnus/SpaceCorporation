# Этап 1 — Фундамент + оболочка дебаг-режима (0.5 недели)

Источник: `prototype-plan.md`, раздел «Этап 1».
Окружение: Godot **4.7.2-stable.mono** (проверено локально), язык — **C# (.NET)**.

**Цель этапа:** пустая сцена, в которой уже есть работающая «приборная панель» для отладки
всех последующих механик. К концу этапа любой будущий механизм подключается одной строкой:
`debugUi.RegisterDebugButton("...", () => { ... });`

**Критерий готовности (из исходного плана):** F3 → появляется кнопка «Debug Mode» →
по клику открывается панель; `RegisterDebugButton` работает — тестовая кнопка печатает в консоль.

> **СТАТУС: этап выполнен** (код в репо, коммит `dad5287` + последующие).
> Автотест `tests/Stage1Test.tscn` — **89 проверок, 0 падений** (`./run-tests.sh`).
> Отклонения от плана в конце файла («Итоги этапа»).
> **Шаг 1.6 (переработка):** `Main.tscn` переписана на инстансы отдельных сцен, игрок-капсула
> со своей камерой вынесен в `scenes/Player.tscn` и крепится к кораблю через `AttachPoint`
> (см. конец файла). Узлы `Camera3D`/`WorldEnvironment` из корня `Main` убраны.

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
- [x] **Пауза — опция**: чекбокс `ChkPauseWorld` («Pause world», по умолчанию включён) в панели.
      Выключен → мир живёт при открытой панели (нужно для теста тяги/инерции этапа 2:
      импульс применяется сразу, а на паузе физический шаг не идёт). Смена галочки меняет
      паузу мгновенно; `SetPauseWorld(bool)` доступен и из кода.

**Результат:** F3 полностью управляет режимом; по умолчанию игра на паузе, UI работает;
паузу мира можно отключить, не закрывая панель.

## Шаг 1.5 — Тестовый корабль-заглушка (1.5 ч, опционально, но полезно)

Нужен, чтобы к этапу 2 не переделывать сцену.

- [x] `PlayerShip` → **вынесен в отдельную сцену `scenes/Ship.tscn`** (шаг 1.6): `RigidBody3D` +
      `BoxMesh` + `CollisionShape3D`, `GravityScale = 0`,
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
- [x] Паузу мира можно отключить чекбоксом в панели, не закрывая саму панель.

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
scenes/Main.tscn              // только инстаны сцен: Environment, TestStand, Ship, Player, DebugLayer
scenes/Environment.tscn       // WorldEnvironment (небо) + DirectionalLight3D; камеры здесь нет
scenes/Player.tscn            // CharacterBody3D-капсула + Body/CollisionShape3D + CameraRig/Camera3D
scenes/Ship.tscn              // RigidBody3D: Hull, CollisionShape3D, Muzzle, AttachPoints/Seat
scenes/TestStand.tscn         // StaticBody3D (слой Station) + StandPoint — точка «пол станции»
scenes/DebugLayer.tscn        // всё дерево UI: DebugRoot, BtnDebugMode, DebugPanel, VBox, LblDebugInfo,
                              // Separator, ChkPauseWorld («Pause world»), Buttons; связи pressed/toggled
scripts/core/CollisionLayers.cs
scripts/core/DebugInfoBus.cs  // autoload-сингл: шина кнопок и debug-info
scripts/core/Ship.cs          // кораблик: невесомость, слои, кнопка «Nudge Ship» (вместо ShipStub.cs)
scripts/core/AttachPoint.cs   // точка крепления игрока: Kind (Seat/Floor/Hatch), IsDefaultForPlayer, PlayerOffset
scripts/core/Player.cs        // капсула + камера; AttachTo / Detach / MoveInAttachedPlane (задел этапа 6)
scripts/debug/DebugLayer.cs   // F3, кнопка «Debug Mode», панель, RegisterDebugButton, UpdateDebugInfo
tests/Stage1Test.cs / .tscn   // автотест приёмки (89 проверок)
tests/ProbeNest.cs / .tscn    // прогон гипотезы: вложенный CharacterBody3D + перепривязка в рантайме
run-tests.sh                  // dotnet build + godot --headless прогон теста
```

Отличия и дополнения к плану:

1. **Весь статический UI живёт в `scenes/DebugLayer.tscn`** — дерево из шага 1.2 описано узлами
   (`DebugRoot` → `BtnDebugMode` + `DebugPanel` → `VBox` → `LblDebugInfo`, `Separator`, `Buttons`),
   там же заданы позиции, размеры, `process_mode = Always`, `mouse_filter` (Ignore на фоне, Stop на
   кнопке и панели) и зелёный цвет текста через `theme_override_colors/font_color`.
   Скрипт только находит узлы через `GetNode` и меняет их свойства (видимость, текст).
   Клик кнопки подключён связью сигнала в сцене (`pressed` → метод `TogglePanel`), а не подпиской в коде.
   Кодинственный узел, который создаёт код, — кнопки из `RegisterDebugButton`: их список появляется
   только в рантайме. Проверено тестом: `VBox` содержит ровно 3 ребёнка из `.tscn`, связь сигнала
   существует, `Separator` задан в сцене.
   Нюанс Godot: у `Button` есть минимальная высота из шрифта, поэтому `offset_bottom` в сцене нужно
   ставить с запасом (44 вместо 34) — иначе движок сдвигает `Position.Y` с 8 на 3.
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
   Позже пауза сделана **опцией** (чекбокс «Pause world»), потому что на паузе не работает
   `Nudge Ship`: импульс применяется, но физический шаг не выполняется, и корабль трогается
   только после закрытия панели.
5. **Godot 4.7.2:** свойство `RigidBody3D` называется `CustomIntegrator` (bool), а не
   `CustomIntegratorMode`; на этапе 1 не нужен, оставлен комментарий под этап 5 (пружина стыковки).
6. **`TestStand`** (StaticBody3D, слой Station) и **`Ship`** (шаг 1.5) живут в `Main.tscn`;
   у корабля уже есть узел `Muzzle` под лазеры этапа 3.

### Шаг 1.6 — главная сцена собрана из импортированных сцен (переработка)

Что сделано:

1. **`Main.tscn` больше не содержит узлов** — только пять инстансов: `Environment`, `TestStand`,
   `Ship`, `Player`, `DebugLayer`. Камера и свет из корня `Main` удалены.
2. **`scenes/Player.tscn`** — `CharacterBody3D` (капсула `radius 0.3 / height 1.8`) + `Body`/
   `CollisionShape3D` + `CameraRig/Camera3D`. Камера — **потомок капсулы**, поэтому привязана к
   игроку автоматически: куда капсула, туда и камера.
3. **`scenes/Ship.tscn`** (вместо самописного `PlayerShip` в `Main.tscn`) — `RigidBody3D` со всеми
   настройками невесомости, `Muzzle` и узлом `AttachPoints/Seat`. `scripts/core/ShipStub.cs` удалён,
   вместо него `scripts/core/Ship.cs`.
4. **Игрок крепится к кораблю не вложенностью узлов, а кодом**: `AttachPoint` (Kind `Seat` /
   `Floor` / `Hatch`, `IsDefaultForPlayer`, `PlayerOffset`) состоит в группу `attach_point`, а
   `Player.AttachTo(point)` делает `Reparent(point)` — то есть в рантайме игрок становится узлом
   внутри иерархии корабля и летает вместе с ним. `Detach()` возвращает его в корень `Main`
   с сохранением мировой позиции.
5. **Автопристёгивание отложено на первый физический шаг**: `Reparent` во время `_Ready()` запрещён
   (дерево сцены ещё достраивается), зато на первом шаге все точки уже в группе — порядок узлов в
   `Main.tscn` не важен.
6. **Задел на этап 6 (ходьба по кораблю/станции)**: `Player.MoveInAttachedPlane(input, speed)` —
   движение в локальной плоскости XZ точки крепления («вперёд» игрока = «вперёд» корабля/станции);
   `AttachPoint.Kind.Floor` + точка `TestStand/StandPoint` уже демонстрируют крепление к другой
   сцене. Маска коллизий игрока намеренно **без слоя Ship**, чтобы капсула в кабине не застревала
   в корпусе.
7. **`scenes/Environment.tscn`** — `WorldEnvironment` (процедурное небо) + `DirectionalLight3D`.

Проверка гипотезы (`tests/ProbeNest.tscn`, 5 проверок, все `ok=True`):

| # | что проверяется | результат |
|---|---|---|
| 1 | пристёгнутый к креслу игрок летает вместе с кораблём | `shipMoved=2.99`, `playerMoved=2.95` |
| 2 | после `Detach` игрок не следует за кораблём | `shipMoved=3.24`, `playerMoved=0.00` |
| 3 | `AttachTo` к точке другой сцены (стенд «как станция») | позиция совпала с ожидаемой |
| 4 | повторная пристёжка ставит локальный `PlayerOffset` | `(0, 0.9, 0)` |
| 5 | камера следует за игроком в любой иерархии | смещение камеры над игроком сохранено |

Вывод по архитектуре: вложенный `CharacterBody3D` внутри `RigidBody3D` в Godot 4.7 работает
корректно (движок сам переносит движение родителя), поэтому этап 6 можно делать на той же схеме
крепления, что и кресло кабины.

Как проверить:

```
godot --path .                     # игра: F3 → «Debug Mode» → панель → кнопки Test / BusButton /
                                   # Nudge Ship / Detach Player / Attach Player to Seat
./run-tests.sh                     # dotnet build + 89 проверок приёмки, exit code 0
./run-tests.sh res://tests/ProbeNest.tscn   # прогон гипотезы крепления (5 проверок ok=True)
```
