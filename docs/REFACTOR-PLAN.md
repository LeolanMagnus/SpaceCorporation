# План рефакторинга: разбиение больших файлов и группировка по папкам

## ✅ Статус выполнения (2026, этапы A и B выполнены)

Все инкременты закоммичены, `dotnet build` = 0 ошибок, полный набор headless-тестов
идентичен baseline HEAD (предсуществующие фейлы Stage1/2/4/5 не изменились по именам и числу).

**Инструменты (`tools/`):** `split_map.py` (инвентаризация членов класса с учётом строк/стрингов/комментариев)
и `split_cls.py` (разрезка на partial-файлы по правилам «строка→бакет», чистый перенос строк).

**Максимальный файл: 4532 → 829 строк.**

Этап A (partial-декомпозиция):
- `Ship.cs` (4532) → 9 файлов: `Ship.cs`(789 ядро) + `.Physics`(434) `.Net`(829) `.Sync`(705) `.Capture`(216) `.Docking`(420) `.Combat`(526) `.PilotCam`(364) `.Debug`(244).
- `NetworkManager.cs` (1573) → 5: ядро(519) + `.Registries` `.Docking` `.Identity`(537) `.Stats`.
- `Player.cs` (614) → ядро + `.Attach`; `WorldSpawner.cs` (662) → ядро + `.Spawn`.

Этап B (папки): перенос через `git mv` вместе с `.cs.uid`.
```
scripts/core/  → чистый домен (FlightModel, CaptureRules, Docking, Faction, ModuleCatalog, PilotAim, Collision*, buses)  [9 файлов]
scripts/net/   ← NetworkManager(+partials), NetState, NetTypes, SnapshotBroadcaster, BroadcasterCore, ShipRecordCodec, InputFrame, SpatialIndex  [12]
scripts/entities/ ← Ship(+9 partials), Player(+Attach), Station(+State), TargetState, AttachPoint, WorldSpawner(+Spawn), ShipTypes/Health/Loadout  [20]
scripts/ui/, scripts/debug/  без изменений
```
Правки путей: `[autoload]` в `project.godot`, `ext_resource path` во всех `scenes/*.tscn` и `tests/*.tscn`.
Обязательный шаг: **регенерация `.godot/uid_cache.bin`** (`godot --headless --path . --import`) — сцены
ссылаются на скрипты по `uid`, кэш помнил старые пути, без реимпорта autoload падал.
Осиротевшие `.cs.uid` partial-файлов в core удалены. Namespace НЕ менялся (папки и namespace в C# расходятся намеренно).

Этап C (по желанию, НЕ выполнен — это уже не pure-refactor): общий `Registry<T>`, вынос остатка правил в чистые функции.

Коммиты: `2b52baa` A1 · `7776bba` A2 · `9272e8b` A3 · `78eb1ed` B · `84864fc` B-cleanup.

---

## 0. Инвентаризация (что и почему болит)

Строки в основных `.cs` (production-код):

| Файл | Строк | Проблема |
|---|---|---|
| `scripts/core/Ship.cs` | 4532 | мега-класс: физика, сеть (вход/снапшоты/реконсиляция), стыковка, capture, оружие, камера, дебаг-кнопки, тест-хуки |
| `scripts/core/NetworkManager.cs` | 1573 | autoload-мегаменеджер: реестры (ships/targets/stations/attach), docking-карта, пилоты, идентичность, таблицы входа/спавна, запуск, события соединения |
| `scripts/core/WorldSpawner.cs` | 662 | спавн мира + камеры + кнопочные хуки + поиск точек |
| `scripts/core/Player.cs` | 614 | движение, attach-логика (клиент/сервер), пилот-камера, дебаг-кнопки |
| `scripts/core/SnapshotBroadcaster.cs` | 600 | ок, на границе |
| `scripts/ui/HudLayer.cs` | 480 | сборка UI + обновление панелей + ком-лог |
| `scripts/core/FlightModel.cs` | 465 | приемлемо |

Тесты (не критично для продакшена, но для читаемости): `Stage2Test.cs` 1380, `Stage3Test.cs` 890, `NetTest.cs` 872, `Stage1Test.cs` 714.

### Already в проекте конвенции (на них и опираемся)

- **`partial class` уже используется** — `Stage4Test` / `Stage4Test.Helpers.cs`, `Stage5Test` / `Stage5Test.Helpers.cs` (файл-хелпер как «отдельный partial того же драйвера, чтобы основной файл остался картой фаз»). Значит partial-декомпозиция — принятый стиль.
- Внутри файлов уже есть секционные разделители `// ─── реестр кораблей ───` — их границы = естественные границы partial-файлов.
- `SpaceCorporation.csproj`: `EnableDefaultCompileItems=false`, glob'ы `scripts/**/*.cs`, `tests/**/*.cs` — **перемещение файлов между папками безопасно для сборки**; Godot подхватывает новые `.uid` сам.
- Ссылки в `.tscn` идут по `path` + иногда `uid` (см. `scenes/Ship.tscn`). При перемещении файла, на который есть `uid://`-ссылка, надо обновить путь в `.tscn` (Godot обычно чинит сам при перемещении через редактор, но мыmove'им через git — значит правим `.tscn` руками и/или потом `mcp_godot_update_project_uids`).
- Автостилы: `res://scripts/core/NetworkManager.cs` и др. прописаны в `[autoload]` project.godot — их пути менять нельзя (или менять вместе с project.godot).

## 1. Целевая структура папок

```
scripts/
  core/                  # домен без сети: FlightModel, CollisionDamage, CaptureRules,
                         # Docking, PilotAim, SpatialIndex(cleanup), Faction, CollisionLayers,
                         # InputFrame, ModuleCatalog, ShipTypes, ShipLoadout, AttachPoint
  net/                   # ← НОВЫЙ: сетевой слой (уже namespace SpaceCorporation.Net
                         # у BroadcasterCore/NetTypes/ShipRecordCodec/SpatialIndex —
                         # физически переезжают сюда)
      NetworkManager.cs (+ partials, см. §3)
      NetState.cs NetTypes.cs BroadcasterCore.cs SnapshotBroadcaster.cs
      ShipRecordCodec.cs SpatialIndex.cs
  entities/              # ← НОВЫЙ: живые узлы сцены
      Ship.cs (+ partials, см. §2)
      Player.cs Station.cs WorldSpawner.cs
      ShipHealthState.cs StationState.cs TargetState.cs
  ui/                    # без изменений
  debug/                 # без изменений
```

Мелкие файлы (`ShipHealthState` 100 строк и т.п.) переезжают пачкой. Namespace НЕ переписываем везде — `SpaceCorporation` остаётся общим (частичный `SpaceCorporation.Net` уже есть). Папки = физическая группировка, namespace = логическая; расхождение папка/namespace в C# допустимо и не ломает ничего.

Порядок переезда (от безопасного к рискованному):
1. `net/`: `NetTypes, NetState, BroadcasterCore, SnapshotBroadcaster, ShipRecordCodec, SpatialIndex` — на них почти нет `.tscn`-ссылок с uid (NetState имеет uid в `Ship.tscn`/др. — правим пути в tscn).
2. `entities/`: `Ship, Player, Station, WorldSpawner, *State`. **Кроме autoload'ов.**
3. `[autoload]` в `project.godot`: `NetworkManager` и `DebugInfoBus` — либо оставляем в `core/`, либо переносим и правим project.godot в том же коммите. Решение: NetworkManager переезжает в `net/`, DebugInfoBus остаётся в `core/` (он и есть шина core).

## 2. Декомпозиция `Ship.cs` (4532 → ~6 partial-файлов)

`public partial class Ship : RigidBody3D` остаётся корневым; по существующим `// ───` границам:

| Файл | Что переносим | ~строк |
|---|---|---|
| `entities/Ship.cs` (ядро) | поля, свойства, ship-type/loadout (`ApplyShipTypeSpec`, `RebuildHullAccessories`, `SetShipTypeLocal`, `RequestShipType`+RPC, `EquipModule`/`UnequipModule`, `RebuildFlight`), `_Ready/_ExitTree`, `ApplyPilot/ApplyAuthority`, `ControlMode`, god/interpolation toggle | ~900 |
| `entities/Ship.Physics.cs` | `_IntegrateForces`, `LocalVelocity`, capture-frame helpers, `IsCruisingParent` | ~350 |
| `entities/Ship.Net.cs` (секция «Шаг 2.3 кадры ввода») | `RpcSubmitInput`, `SubmitInputToServer`, `EnqueueInputForSend`, `FlushSendBuffer`, `EnqueueSnapshotFromNet`, `ApplyBatchRecord`, `ResetInputQueue`, `_PhysicsProcess/_Process`, `DrainRecvBuffer*`, `AdaptInterpDelay`, `ReconcileWith`, `ReplayInputsAfter`, `SetBodyToPrediction`, `ReplayAllApplied/ReplayAppliedFramesFrom` | ~1300 |
| `entities/Ship.Capture.cs` | `TickCaptureRule`, `SetCapture`, capture-replica поллинги | ~250 |
| `entities/Ship.Docking.cs` | `TickDockPhases`, `HandleStationContacts`, `RequestDocking/SubmitDockToServer/RpcRequestDocking`, `TickDockWindow`, dock-replica | ~450 |
| `entities/Ship.Combat.cs` | `HandleMouseMotion`, `RequestFire`, `SubmitFireToServer`, `RpcRequestFire`, `ProcessPendingFires`, `RpcOnHitConfirmed`, `ShowHitFlash`, `ShowAimingBeam`, `ServerDieShip` | ~700 |
| `entities/Ship.PilotCamera.cs` | секции «камера-преследование», «сборщик мыши» | ~250 |
| `entities/Ship.Debug.cs` | `BuildDebugButtons/RemoveDebugButtons`, все `Test*`-хуки (швы автотестов) | ~400 |

Правила переноса, чтобы не сломать Godot:
- **Один класс — один из файлов несёт `[GlobalClass]`/XML-doc шапку**; остальные только `partial`.
- Приватные поля остаются в ядре; partial'и обращаются к ним напрямую (в одном классе это легально). `private`-методы можно переносить куда угодно — вызовы не меняются.
- `override _Ready/_PhysicsProcess/_Process/_IntegrateForces/_UnhandledKeyInput` — **по одному override на класс**: остаются в указанных файлах выше (по одному каждому, конфликтов нет).
- Тестовые хуки (`TestFire`, `TestSubmitInput`, ...) сводим в `Ship.Debug.cs` — они уже помечены «швы автотестов».

Объём работы в `Ship.cs` = физический cut&paste по уже размеченным границам; ни одной сигнатуры не меняем.

## 3. Декомпозиция `NetworkManager.cs` (1573 → 4 partial-файла)

Autoload-класс остаётся `NetworkManager` в `scripts/net/`:

| Файл | Секции (по существующим разделителям) | ~строк |
|---|---|---|
| `net/NetworkManager.cs` (ядро) | константы, синглтон, состояние, `_Ready`/запуск/события соединения, диагностика/счётчики трафика | ~450 |
| `net/NetworkManager.Registries.cs` | AttachPoints, реестр кораблей, мишеней, станций; `NextNetId/AssignNetId*` | ~350 |
| `net/NetworkManager.Docking.cs` | `_dockRequests`: BeginDock/SetDockPhase/AbortDock/NoteDockRejection | ~150 |
| `net/NetworkManager.Identity.cs` | playerKey, таблицы входа (`_peerPlayers/_lastSeen/_peerKeys/_pendingAttach`), `HandleRequestSpawn`, `SpawnPlayerForPeer`, `OnPeerLeft`, пилоты (`SetPilot/ReleasePilot/RpcSetPilot/TryGetPilot`) | ~600 |

Второй шаг (по желанию, уже не pure-refactor): реестры-словари можно обернуть в `Registry<T>` (везде ~одинаковые Register/Unregister/Find/Assign). Это изменение API — вынести в отдельный этап, тесты Registry-поведения уже есть в Stage2/4.

## 4. Декомпозиция `Player.cs` (614 → 3 файла)

| Файл | Содержимое |
|---|---|
| `entities/Player.cs` (ядро) | поля, `_Ready/_ExitTree/_PhysicsProcess`, камера (`TogglePilotCameraView`, `GetActiveCamera`) |
| `entities/Player.Attach.cs` | `AttachToDefaultPoint/AttachTo/Detach/MoveInAttachedPlane`, клиентские `Request*`, серверные `AttachServerSide/DetachServerSide`, `NearestAttachPoint`, `IsRequestFromOwner` |
| `entities/Player.Debug.cs` | debug-кнопки `OnButtonAttach/Detach` |

## 5. `WorldSpawner.cs` (662 → 2 файла)

| Файл | Содержимое |
|---|---|
| `entities/WorldSpawner.cs` (ядро) | `_Ready/_ExitTree/OnRoleChanged`, `SpawnWorld/StartSinglePlayerWorld`, fallback-камеры |
| `entities/WorldSpawner.Spawns.cs` | `SpawnShip*`, `SpawnStation`, `SpawnTarget/ClearTargets/TargetSpawnPosition`, `SpawnPlayerAt`, `FindOfflinePlayer/FindShipByName/UniqueChildName` |
| (в ядро) кнопочные хуки `OnSpawnTargetButton/OnClearTargetsButton` — мелкие |

## 6. Тесты — отдельный, необязательный этап

Тестовые драйверы уже разрезаны для Stage4/5. Дотянуть тот же образец:
- `Stage2Test.cs` (1380) → `Stage2Test.cs` + `Stage2Test.Helpers.cs`
- `Stage3Test.cs` (890) → `+ Stage3Test.Helpers.cs`
- `NetTest.cs` (872) → `+ NetTest.Helpers.cs`
- `Stage1Test.cs` (714) → опционально
- `tests/probes/*` — отсортировать по подпапкам (`flight/`, `sync/`, `hud/`) или оставить: это одноразовые зонды, не трогаем.

## 7. Порядок выполнения и проверки (каждый шаг — зелёная сборка)

Инкременты идут **по одному**, каждый заканчивается `./run-tests.sh` (dotnet build + headless Stage1Test) и минимум одним профильным тестом этапа (например после переезда docking-частей — `Stage5Test.tscn`).

1. **Этап A — partial-декомпозиция без перемещений** (внутри `scripts/core/`):
   A1 `Ship.cs` → 7 partial-файлов → build + `Stage2Test` (сеть/ввод) + `Stage5Test` (docking) + `CaptureLiveTest`.
   A2 `NetworkManager.cs` → 4 файла → `NetTest`.
   A3 `Player.cs`, `WorldSpawner.cs` → `Stage1Test`, `Stage4Test`.
2. **Этап B — перемещение файлов в новые папки** (`net/`, `entities/`):
   - `git mv`, правим пути в `.tscn` где есть `path="res://scripts/core/X.cs"` (Ship/Station/Player/AttachPoint/NetState/*State/WorldSpawner/Hud-скрипты в сценах), правим `[autoload]` в project.godot для NetworkManager.
   - `.uid`-файлы перемещаем вместе с `.cs` (Godot 4.4+: uid внутри .cs.uid, пути в tscn с uid резолвятся по uid — но `path`-атрибут всё равно должен существовать).
   - После переезда: прогнать все тесты `./run-tests.sh` для каждого Stage*.
3. **Этап C (опционально, не pure-refactor)**: `Registry<T>` в NetworkManager; вынос capture/dock-правил из Ship в чистые функции (часть уже в `CaptureRules.cs`/`Docking.cs`).

## 8. Риски и как их снять

- **Godot + partial**: partial-класс в нескольких файлах одного namespace — штатный сценарий Godot C# (тесты Stage4/5 уже так живут). Риск только в дубликатах override-методов — контроль: один override = один файл.
- **Переезд файлов и uid**: сначала рефакторинг (этап A), потом переезд (этап B) — переезд не смешиваем с резкой; если uid-ссылка в tscn сломается, лечится `mcp_godot_update_project_uids` или ручной правкой пути.
- **Autoload NetworkManager** — путь в project.godot; менять только в этапе B отдельным коммитом.
- **Каждый partial-файл < ~700 строк**; имена `Ship.Net.cs` сохраняют «карту» — порядок секций в исходном `Ship.cs` соответствует номерам шагов плана этапов (2.3, 2.2.3...), комментарий-шапку каждого файла переносим из разделителя.
- Ничего из публичного API не меняется → тесты не правятся (кроме этапа C).

## 9. Ожидаемый результат

`Ship.cs` 4532 → ядро ~900 + 6 файлов 250–1300; `NetworkManager.cs` 1573 → 4 файла ≤600; сеть (`net/`), сущности (`entities/`), UI и debug разведены по папкам; каждый тестовый драйвер — карта фаз + helpers. Сборка и все headless-тесты зелёные после каждого инкремента.
