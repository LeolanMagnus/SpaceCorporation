# DEVELOPMENT.md — журнал разработки Space Corporation

Хронологический список того, что реально сделано. Детали и план каждого этапа — в соответствующих
файлах (`stage1-plan.md` и далее). Источник замысла: `prototype-plan.md`.

Движок: **Godot 4.7.2-stable.mono**, язык: **C# / .NET 8** (`Godot.NET.Sdk/4.7.2`).
Проверка: `./run-tests.sh` (dotnet build + headless-прогон автотеста).

---

## Этап 1 — Фундамент + оболочка дебаг-режима ✅ (выполнен)

План: `stage1-plan.md`. Коммиты: `dad5287` → `a61fe4e` → `4966694` + правки UI-из-сцены + пауза-опция.
Автотест приёмки: **89 проверок, 0 падений**, exit code 0.

### 1.0 Проект
- Godot-.NET проект в корне: `project.godot`, `SpaceCorporation.csproj` (net8.0).
- Стартовая сцена `scenes/Main.tscn` (корень `Node3D`). Изначально в ней лежали `WorldEnvironment`,
  `DirectionalLight3D`, `Camera3D`, стенд и корабль; на шаге 1.6 сцена переписана на инстансы
  отдельных сцен (см. ниже).
- `dotnet build` проходит, сцена рендерится без ошибок в консоли.

### 1.1 Физика: невесомость и слои
- `physics/3d/default_gravity = 0.0` — глобальной гравитации нет.
- 6 именованных слоёв 3D-физики: `Ship, Station, Character, Target, DockingZone, DebugHelper`.
- `scripts/core/CollisionLayers.cs` — константы-степени двойки, magic-чисел в коде нет.
- `TestStand` (`StaticBody3D`, слой Station) — стенд для проверки коллизий до этапа 4.

### 1.2 DebugLayer — оболочка дебаг-UI
- `scenes/DebugLayer.tscn` + `scripts/debug/DebugLayer.cs` (`CanvasLayer`, layer = 10).
- **F3** показывает/скрывает кнопку «Debug Mode» (левый верхний угол, зелёный текст).
- Клик по кнопке открывает/закрывает панель (`PanelContainer` → `VBoxContainer`).
- `RegisterDebugButton(string label, Action callback)` — кнопка в один вызов; тестовая «Test»
  печатает в консоль.
- **UI строится из сцены, а не из кода**: всё статическое дерево (DebugRoot, BtnDebugMode,
  DebugPanel, VBox, LblDebugInfo, Separator, ChkPauseWorld, Buttons) объявлено в `.tscn` вместе с позициями,
  размерами, `process_mode = Always`, `mouse_filter` и цветами. Скрипт только находит узлы и меняет
  их свойства; клик кнопки подключён связью сигнала в сцене (`pressed` → `TogglePanel`).
  Код создаёт лишь динамические кнопки из `RegisterDebugButton`.
  - Нюанс Godot: у `Button` минимальная высота из шрифта — `offset_bottom` в сцене нужен с запасом
    (44 вместо 34), иначе движок сдвигает `Position.Y` с 8 на 3.

### 1.3 Живой DebugInfo
- `UpdateDebugInfo()` в `_Process` — строка обновляется каждый кадр, блоки склеиваются через `\n`.
- `RegisterInfoSource(key, Func<string>)` — система добавляет свой блок одной строкой;
  повторная регистрация по тому же ключу заменяет блок (безопасно после рестарта сцены).
- Сейчас выводятся только реальные данные: FPS, позиция камеры, `gravity = 0`.

### 1.4 Ввод и пауза
- Действие `debug_toggle` = **F3** прописано в InputMap (`project.godot`), не хардкодом.
- Открытая панель ставит дерево на паузу (`GetTree().Paused = true`), у `DebugLayer`
  `ProcessMode = Always` → UI живёт на паузе. F3 при открытом меню закрывает панель и снимает паузу.
- `MouseFilter = Ignore` на фоне, `Stop` только на кнопке и панели — клики по игре не глотаются.
- Снимается **только собственная пауза** слоя, чтобы не ломать будущее меню паузы (коммит `4966694`).
- **Пауза мира — опция**: чекбокс `ChkPauseWorld` («Pause world», по умолчанию включён), связь
  `toggled → SetPauseWorld` в сцене; `SetPauseWorld(bool)` / `TogglePauseWorld()` доступны и из кода,
  пауза меняется мгновенно, не закрывая панель. В `LblDebugInfo` — строка `world: PAUSED / running`.
  Причина: на паузе физический шаг не выполняется, поэтому «Nudge Ship» применяет импульс, но корабль
  трогается только после снятия паузы — для теста тяги/инерции этапа 2 нужен живой мир.

### 1.5 Корабль + шина отладки
- `Ship` (`scenes/Ship.tscn`: `RigidBody3D` + `BoxMesh` + `CollisionShape3D`, скрипт `scripts/core/Ship.cs`):
  `GravityScale = 0`, `LinearDamp = 0`, `AngularDamp = 1.5`, слой Ship / маска Station+Target —
  висит в невесомости; в сцене уже есть узел `Muzzle` под лазеры этапа 3.
- Кнопка «Nudge Ship» — пихает корабль импульсом, чтобы проверять невесомость глазами.
- `scripts/core/DebugInfoBus.cs` — **autoload-сингл**: шина кнопок и debug-info. Системы регистрируют
  кнопки/строки, не зная про UI; шина копит запросы, пока `DebugLayer` не готов.

### 1.6 Главная сцена из импортированных сцен + крепление игрока
- `Main.tscn` содержит **только инстансы**: `Environment`, `TestStand`, `Ship`, `Player`, `DebugLayer`.
  Камеры, света и геометрии в корне `Main` больше нет.
- `scenes/Environment.tscn` — `WorldEnvironment` (процедурное небо) + `DirectionalLight3D`.
- `scenes/Player.tscn` — `CharacterBody3D` (капсула 0.3/1.8) + `Body`/`CollisionShape3D` +
  `CameraRig/Camera3D`. **Камера — потомок капсулы**, поэтому «привязана к игроку» без кода:
  куда капсула, туда и камера.
- `scripts/core/AttachPoint.cs` — точка крепления: `Kind` (`Seat` / `Floor` / `Hatch`),
  `IsDefaultForPlayer`, `PlayerOffset`; все точки состоят в группе `attach_point`.
- `scripts/core/Player.cs` — `AttachTo(point)` делает `Reparent(point)` (игрок становится узлом
  внутри иерархии корабля/станции и движется вместе с ней), `Detach()` возвращает в корень `Main`
  с сохранением мировой позиции. Автопристёгивание к `IsDefaultForPlayer` выполняется на **первом
  физическом шаге**, потому что `Reparent` во время `_Ready()` запрещён.
- `Player.MoveInAttachedPlane(input, speed)` — движение в локальной плоскости XZ точки крепления:
  задел на этап 6 (ходьба по палубе корабля/станции). Точка `TestStand/StandPoint` (`Kind = Floor`)
  демонстрирует крепление к другой сцене.
- Маска коллизий игрока намеренно **без слоя Ship** (`Station | Target`), иначе капсула в кабине
  застревает в корпусе.
- Кнопки «Detach Player» / «Attach Player to Seat» — цикл открепления и пристёжки прямо из панели.
- `tests/ProbeNest.tscn` — отдельный прогон гипотезы (5 проверок): вложенный `CharacterBody3D`
  внутри `RigidBody3D` следует за кораблём, после `Detach` — нет, `AttachTo` к другой сцене и
  повторная пристёжка работают, камера следует за игроком в любой иерархии.

### Отклонения и дополнения к плану
1. Статический UI целиком перенесён в `DebugLayer.tscn` (в плане он описывался как строящийся кодом)
   — меньше кода, правки вида/раскладки без компиляции.
2. `DebugInfoBus` сделан autoload уже на шаге 1.2 (в плане — для 1.5): системы этапов 3–6 должны
   уметь регистрироваться до появления UI.
3. `RegisterInfoSource` получил **ключ** (в плане его не было) — перерегистрация вместо дублей.
4. Пауза дерева при открытой панели закреплена тестом (риск «конфликт с вводом» закрыт), но
   позже сделана **опцией** («Pause world» в панели) — на паузе не работает «Nudge Ship».
5. Godot 4.7: у `RigidBody3D` свойство называется `CustomIntegrator` (bool), а не `CustomIntegratorMode`
   — на этапе 1 не используется, оставлен комментарий под пружину стыковки (этап 5).

### Инфраструктура проверки
- `tests/Stage1Test.cs` + `tests/Stage1Test.tscn` — автотест приёмки: 89 проверок (сцена, гравитация,
  слои, структура панели из `.tscn`, «скрипт не создаёт UI», связи сигналов кнопки и чекбокса,
  пауза + её переключение опцией «Pause world» (мир живёт / мир заморожен), шина, невесомость корабля;
  с шага 1.6 — что `Main` состоит только из импортированных сцен, что у игрока есть активная камера
  — потомок капсулы, слои/маска игрока, и цикл автопристёжка → Detach → повторная пристёжка).
- `tests/ProbeNest.cs` + `tests/ProbeNest.tscn` — отдельный прогон гипотезы крепления (5 проверок):
  `./run-tests.sh res://tests/ProbeNest.tscn`.
- `run-tests.sh` — `dotnet build` + `godot --headless` прогон теста, exit code 0 = успех.
- `.gitignore` — `.godot/`, `bin/`, `obj/`.

### Что сознательно НЕ делалось на этапе 1
Управление кораблём (2), стрельба и мишени (3), станция и урон (4), стыковка (5),
ввод для персонажа и магнитные ботинки (6), ассеты.

### Задел, который дальше не переделывается
- `RegisterDebugButton` → Spawn Target / Clear Targets (3), Toggle God Mode (4),
  Teleport to Docking Zone (5), Toggle Boots / Teleport to Platform (6).
- `RegisterInfoSource` → скорость и угловая скорость (2), HP (4), DockingState (5), ботинки (6).
- Слои коллизий → маски `RayCast3D` для стрельбы (3) и магнитных ботинок (6).
- `AttachPoint` + `Player.AttachTo/Detach` → переход корабль ↔ станция (6): у точки уже есть
  `Kind.Hatch` и `Kind.Floor`, а `Player.MoveInAttachedPlane` — горизонтальное движение по палубе
  в локальной плоскости точки крепления. Проверено: вложенный `CharacterBody3D` внутри
  `RigidBody3D` в Godot 4.7 работает корректно.

---

## Этап 2 — Пилотирование 6DoF ⏳ (не начат, ~1 неделя)

План будет в `stage2-plan.md`. Ключевое: линейные силы и торки только в локальных осях
(`GlobalTransform.Basis.X/Y/Z`), демпфирование вращения, дебаг-кнопки под настройку «ощущения»,
строки скорости/угловой скорости в DebugInfo.

---

## Как запустить и проверить

```
godot --path .        # игра: F3 → «Debug Mode» → панель → чекбокс «Pause world» + кнопки
                      #       Test / BusButton / Nudge Ship / Detach Player / Attach Player to Seat
                      #       (для тяги паузу лучше выключить)
./run-tests.sh        # dotnet build + 89 проверок приёмки, exit code 0
./run-tests.sh res://tests/ProbeNest.tscn   # прогон гипотезы крепления игрока к сценам
```
