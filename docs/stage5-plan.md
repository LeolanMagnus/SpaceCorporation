# Стадия 5 — «Стыковка с серверной авторитетностью»: детальный план

Требования: `prototype-plan.md:102-118` (Этап 5). Предыстория: `stage4-plan.md`
(В19, завершён: HP/таран/урон станции, `StationState` + `StationNetId`,
`StationReplication.tres`), `net-refactor-plan.md` (R-план, R7 — долг по гейтингу
событий по интересу), `DEVELOPMENT.md:442-470` (итоги Этапа 4).

Статус до стадии (green baseline, `DEVELOPMENT.md:446-456`): Stage1Test 104,
Stage2Test 82, Stage3Test 78, Stage4Test 84, NetTest 123 assert'ов; CodecTest
103046, BroadcasterTest 27, SpatialIndexTest 25, StandTest 11. Демо-замеры
`LATENCY=100 ./run-net-demo.sh 2`: `rec/s = 80`, `KB/s ≈ 7.6`.

Ссылки вида `Ship.cs:691` — фактические номера строк рабочего дерева на момент
составления плана; перед правкой сверять grep'ом, а не номером.

---

## 0. В20 — разрешаемое противоречие: «клиент жмёт F, сервер решает» против
том, что у корабля уже есть привязка к станции и у станции нет никакой
реплицируемой логики стыковки

Текст задачи (`prototype-plan.md:107`) предполагает, что в мире уже есть:
зона `DockingZone`, станция с адресом, состояние корабля, «огни» и «захват».
Код даёт половину, вторая половина противоречит тексту прямо.

| что требует текст | что в коде сейчас | противоречие |
|---|---|---|
| «клиент в зоне `DockingZone`» — зона должна быть измерима | `CollisionLayers.DockingZone = 1u << 4` (`CollisionLayers.cs:13`) объявлена и нигде не используется; в `scenes/Station.tscn` есть только `CollisionShape3D` (цилиндр r=60, h=40, `collision_layer = 2` = Station) и узел-закладка `DockingPoint : AttachPoint` (`Kind = 2` = Hatch, `NetId = 0`, **без какой-либо collision-формы и без `transform`** — то есть в локальных координатах станции, `(0,0,0)`) | «в зоне» неоткуда брать: формы/сигнала зоны не существует |
| «скорость ниже порога» | порога стыковки нет вовсе; есть пороги тарана: `CollisionDamage.Threshold = 12` м/с, `DamagePerMps = 2`, `BounceRestitution = 0.3` (`CollisionDamage.cs:27-35`) и серверный учёт контактов `HandleStationContacts` (`Ship.cs:834`) | та же самая стенка: попытка сесть на станцию на 12+ м/с сейчас = урон + отскок (В19), то есть «стыковка» физически невозможна без исключения из ветки урона |
| `rpc_id(1, "RequestDocking")` | RPC-гейты в проекте устоялись: `Multiplayer.GetRemoteSenderId()` + гейт авторитета (образцы `Ship.cs:1178-1215` `AcceptFireFrom`, `NetworkManager.cs:723-740`) | новый RPC обязан соблюдать В17 (гейт по отправителю) и В14=B (решает только сервер); в тексте про гейты ни слова |
| «огни — синхронизированное свойство `DockingApproved = true`» | у станции есть `StationState.cs` + `StationReplication.tres` (`MaxHp`/`NetPosition`/`NetRotation`, все свойства `replication_mode = 0` = Never) | `DockingApproved` надо добавлять в конфиг станции; у станции **`spawner = null`** — реплика доходит только тем, кому узел уже известен |
| «сервер применяет пружину/Joint3D к авторитетному кораблю», «клиенты не симулируют пружину локально» | корабли в этом проекте не используют физический шаг движка: `CustomIntegrator = HasAuthority` (`Ship.cs:715`) и `Freeze = !HasAuthority; FreezeMode = Static` (`Ship.cs:690-691`), движение считает `FlightModel.Step` в `_IntegrateForces` (`Ship.cs:757`) и в `StepPrediction` (`Ship.cs:948`); отскок станции — тоже наш собственный (`CollisionDamage.ApplyBounce`) | `Joint3D` = «красивое» решение, которое у нас не работает: серверный шаг — наш собственный интегратор, он `Joint3D` не читает; пружина обязана быть функцией в том же `FlightModel`-шаге (или рядом с ним) |
| «клиенты видят движение корабля через синхронизацию (интерполированно)» | путь позы корабля — упакованный батч (`SnapshotBroadcaster.RpcSnapshotBatch`, канал 2; `ShipReplication.tres` после R6 = `Never` + `spawn = true`, В18), станция — только спавн-снимок; константы: `InterpDelay = 0.1` (`Ship.cs:65`), публикация снимка раз в `NetPublishEveryN = 2` физшага = 30 Гц (`Ship.cs:381`, `1857`) | стыкуемый корабль продолжает слать позу батчем — ОК; но клиент обязан перестать **предсказывать** (пилилируемый пир в dock-фазе не имеет права на локальную пружину), иначе реконсиляция даст `hardRollbacks` |
| «сервер ставит `Freeze = true` и синхронизирует состояние `Docked`» | никакое «состояние корабля» не реплицируется вовсе: у `RigidBody3D` есть только встроенный `Freeze`/`FreezeMode`, а `ApplyAuthority` (`Ship.cs:686-693`) ставит `Freeze = !HasAuthority` **локально на каждом пира**; enum'а состояний корабля в проекте нет — есть только `ControlMode { Pilot, Interaction }` (`Ship.cs:473`) и `NetworkManager.NetRole` (`NetworkManager.cs:32`); `NetState.cs` имеет `NetPosition/NetRotation/NetLinearVelocity/NetAngularVelocity/SimTick/AppliedTick/AppliedMask/ShipNetId` | «синхронизированное состояние `Docked`» = новое реплицируемое поле (у корабля и/или у станции), а не локальный `Freeze`: `Freeze` у не-авторитета и так `true` всегда (В14=B), значит по нему стыковку отличить нельзя |
| «в дебаг-панель: `Teleport to Docking Zone` (только сервер/host)» | дебаг-кнопки есть (`DebugInfoBus`, `Ship.cs` регистрирует кнопки и info-источники) | «только сервер/host» против того, что панель локальна: у клиента кнопку тоже видно, если не гейтнуть; и телепорт обязан идти через серверный адрес, а не через локальный `GlobalPosition` |
| (критерий, `prototype-plan.md:118`) «клиент отвалился в середине стыковки → сервер завершает или отменяет» | обрыв пира: `NetworkManager.OnPeerLeft` (`NetworkManager.cs:~693-740`) + `ReleasePilot` (`:~1098`), пилот-владелец в `_pilots` | `OnPeerLeft` сегодня не знает ни о какой dock-фазе; у dock-фазы должен быть владелец-пира и явная отмена/завершение |

### Почему нельзя оставить как есть

1. Физически: без collision-формы зоны и без исключения станции из ветки урона
   «сесть» нельзя — попытка = урон и отскок (В19).
2. Сетевые: `Joint3D` с `custom_integrator` = мёртвый код; «клиенты не симулируют
   пружину» невозможно обеспечить, если серверная пружина живёт в чистом физическом
   шаге, которого у нас нет.
3. Assert'ы: добавление `Docked` в `ShipState` без реплицируемого поля оставит
   клиентов с локально-выдуманным состоянием — ровно тот грех, который стадии 2–4
   ловят assert'ами (В18).

### Решение В20

1. **Зона — измеримая, но не физическая.** Зону задаёт не `Area3D`, а
   константа + узел, который уже есть: «корабль в зоне» =
   `ship.GlobalPosition.DistanceTo(dockPoint.GlobalPosition) ≤ DockRange`
   (`DockRange = 90` м при радиусе станции 60 м, то есть 30 м запаса снаружи
   корпуса). Геометрический гейт, а не сигнал `body_entered`: сигнал не
   гарантирует порядка/авторитета и не воспроизводится в headless-тесте так же
   чисто, как дистанция (тот же выбор, что `IntersectShape`-путь В19 и
   `probe_station_pen` в stage4 §4.4). `Station.tscn` обязан задать
   `DockingPoint` **смещением** (например `transform` = `(0, 0, 70)` — вдоль +Z
   от центра, снаружи цилиндра r=60): сейчас у него нет `transform`, точка
   совпадает с центром станции, и «в зоне» означало бы «внутри стены».
   Слой `DockingZone` в этой стадии остаётся неиспользуемым (запись в §3, чтобы
   это не выглядело упущением): физическая зона не нужна, а `CollisionLayers.cs`
   её явно помечает как «зарезервирован под Этап 5+» — резерв подтверждается
   комментарием в `Station.cs`.
2. **Dock-фаза — наш собственный серверный шаг, не `Joint3D`.** Сервер в
   `_IntegrateForces` (`Ship.cs:757`) на фазах `Approved/Capturing` вместо тяги
   игрока считает пружину-ПД к `DockingPoint` (позиционный член `k·Δx` и
   демпфирование `-c·v`, кламп по ускорению), пишет результат в ту же
   `FlightModel`-модель (`StepPrediction` → `FlightModel.Step`, `Ship.cs:948-960`)
   и публикует позу тем же батчем (В18/R4-R5, `NetPublishEveryN = 2`). Клиент на
   `Capturing/Latched/Docked` **локальное предсказание не применяет** — корабль
   идёт по интерполяции батча (`InterpDelay = 0.1`): требование
   `prototype-plan.md:113` («клиенты не симулируют пружину локально») выполнено
   буквально. `Joint3D` в проекте не появляется (запись в §3).
3. **Порог скорости — отдельный от порога урона, и на dock-фазе урон станции
   выключается.** `DockSpeedLimit = 8` м/с по **нормальной** составляющей `vn`
   (ту же величину считает `CollisionDamage.ApproachSpeed`) — ниже
   `CollisionDamage.Threshold = 12`, чтобы валидный запрос физически не мог
   нанести урон. Кроме того, на фазах `Approved/Capturing/Latched/Docked`
   `HandleStationContacts` (`Ship.cs:834`) пропускает контакт с **той станцией,
   к которой идёт захват** (по `StationNetId == DockStationNetId`): иначе пружина
   сама себя таранит — сервер тянет корабль в cylinder и В19 начнёт капать урон
   каждый кадр, пока не убьёт корабль. К другой станции урон остаётся.
4. **Состояжение живёт в двух местах, и оба реплицируются.** Новый общий enum
   `DockState { None, Requested, Approved, Capturing, Latched, Docked }`
   (`DockStateValue : byte` как `[Export] int`-совместимое свойство — Godot не
   реплицирует enum-свойства). У станции — `StationState.DockApproved` +
   `StationState.DockShipNetId` (для «огней» и «одновременно садится только один
   корабль»), у корабля — `NetState.DockStateValue` + `NetState.DockStationNetId`
   (для гейта предсказания пилота). В `StationReplication.tres` — `DockApproved`
   (`spawn = true`, `OnChange`), в `ShipReplication.tres` — `DockStateValue` +
   `DockStationNetId` (`OnChange`, `spawn = true`). Имя из текста
   `DockingApproved` в коде называется `DockApproved` — фиксируем здесь, чтобы
   `grep DockingApproved` не выдавал «не найдено».
5. **Dock-сделка принадлежит пиру и отменяема.** Сервер держит связь
   `shipNetId → (requestingPeer, stationNetId)`; при `OnPeerLeft`
   (`NetworkManager.cs:693`, рядом с `ReleasePilot`, `:382`) незавершённая фаза
   (`Requested/Approved/Capturing/Latched`) **отменяется**: сброс `DockApproved`,
   `DockState → None`, снятие заморозки, возврат обычного полёта (корабль
   дрейфует, как после `Detach` в BUG-009); уже завершённая `Docked`
   **остаётся** (посадка без пилота — норма: `Freeze` у сервера, корабль никуда
   не девается). Правило «отменить vs завершить» детерминированно проверяется
   тестом (п.16/п.17).

### Что ломается и как чинится

| что ломается | почему | как чинится (шаг) |
|---|---|---|
| попытка сесть = урон + отскок | В19 ветка `HandleStationContacts` (`Ship.cs:834`) | 5.10: пропуск контакта с «нашей» станцией на dock-фазе (assert п.26) |
| строка debug-info `mode = PILOT/INTERACT` | dock-фазу в панели не видно (`Ship.cs:546-572`) | 5.13: в тот же источник добавляется `dock = <состояние>`; существующие assert'ы (`Stage3Test` п.26, `Stage4Test` п.26) не должны сломаться — **добавляем ключ, не переписываем прежние** |
| `rec/s` / `KB/s` | новые реплицируемые свойства | 5.3: только `OnChange` (не `Always`); замеры до/после в 5.14 (образец: Stage4 §4.12 `rec/s = 80`, `KB/s ≈ 7.6`) |
| реконсиляция пилота в dock-фазе | сервер ведёт корабль пружиной, клиент предсказывает свою тягу → `hardRollbacks` | 5.9: на dock-фазе клиент не предсказывает (assert п.24), эталон позы = батч + интерполяция |
| пустой `.tres` = «огни не загораются» | ловушка стадий 2–4 (`replication_mode = 0` / путь без двоеточия) | 5.3 + риск §3.1: тест читает `.tres` и печатает список путей при старте |
| у клиента видна кнопка «Teleport to Docking Zone» | панель локальна (`DebugInfoBus.RegisterDebugButton`, `DEVELOPMENT.md:127-128` уже обещает эту кнопку с пометкой «только сервер/host») | 5.12: кнопка регистрируется только при `HasAuthority`, у копии её нет вовсе (тот же приём, что `Toggle God Mode`, assert п.33) |
| `Freeze = !HasAuthority` перетирает dock-заморозку | `ApplyAuthority` (`Ship.cs:686-693`) — единственная точка записи `Freeze`, вызывается и при смене авторитета, и при `ApplyPilot` | 5.10: dock-заморозка учитывается в том же `ApplyAuthority` (`Freeze = !HasAuthority || DockLatched()`), assert п.29 |

---

## 1. Термины и константы, которые вводит стадия

Новые величины — в одном месте: `scripts/core/Docking.cs` (чистые функции + enum +
константы), по образцу `scripts/core/CollisionDamage.cs` (стадия 4: там же живут
`Threshold = 12`, `DamagePerMps = 2`, `BounceRestitution = 0.3`). Имена фиксируются
до кода, чтобы тесты, панель и план говорили одними словами.

| имя | значение | смысл |
|---|---|---|
| `DockRange` | 90 м | «корабль в зоне»: расстояние от `DockingPoint` до корабля (станция r = 60 → 30 м запаса снаружи корпуса) |
| `DockSpeedLimit` | 8 м/с | «скорость ниже порога»: `\|vn\|` к станции (та же величина, что `CollisionDamage.ApproachSpeed`). Заведомо **ниже** порога урона 12, чтобы валидный запрос не успел натаранить |
| `DockWindowSec` | 5 с | жизнь разрешения: `Approved` без начала захвата → авто-сброс в `None` (иначе «огни горят вечно») |
| `SpringK` | 6.0 1/с² | жёсткость позиционной пружины: `a += k·Δx` |
| `SpringC` | 4.0 1/с | демпфирование: `a -= c·v` (без него пружина = вечный осциллятор, картинка «качается») |
| `MaxSpringAccel` | 25 м/с² | кламп ускорения: запрет «пружина рванула через станцию» (иначе одномоментный пролёт и повторный контакт) |
| `AlignK` / `AlignDamp` | 3.0 / 3.0 1/с², 1/с | доворот оси носа (`-Z`) к оси стыка — «захват» без ориентации = сесть хвостом |
| `LatchDistance` | 3.0 м | `Capturing → Latched` («фиксация») |
| `LatchAlignCos` | 0.9 (≈26°) | угловое условие лatch'а: нос смотрит в точку стыка |
| `DockDistance` / `DockSpeedEps` | 0.5 м / 0.3 м/с | `Latched → Docked`: корабль «на замке» → `Freeze = true` (сервер) |
| `DockerPeerNone` | 0 | нет заявителя |

Enum (один на станцию и корабль, значения по порядку — не переставлять, они
попадают в репликацию как `byte`):

```
public enum DockState : byte
{
    None = 0,       // стыковки нет
    Requested = 1,  // запрос принят, сервер ещё не разрешил (редкая, переходная)
    Approved = 2,   // «огни» горят, ждём входа в док-окно по скорости
    Capturing = 3,  // сервер ведёт корабль пружиной
    Latched = 4,    // механическая фиксация, дотягивание/дожим
    Docked = 5,     // финал: Freeze у сервера, скорости обнулены
}
```

Реплицируемые свойства (все обязаны быть `[Export] public T … { get; set; }` —
иначе синхронизатор их не видит, правило `StationState.cs:19-22`):

| свойство | узел-носитель | конфиг | режим |
|---|---|---|---|
| `StationState.DockApproved : bool` | станция | `StationReplication.tres` | `spawn = true`, `OnChange` (2) |
| `StationState.DockShipNetId : int` | станция | то же | `spawn = true`, `OnChange` |
| `StationState.DockStateValue : byte` | станция | то же | `spawn = true`, `OnChange` |
| `NetState.DockStationNetId : int` | корабль | `ShipReplication.tres` | `spawn = true`, `OnChange` |
| `NetState.DockStateValue : byte` | корабль | `ShipReplication.tres` | `spawn = true`, `OnChange` |

Godot не реплицирует свойства-`enum`, поэтому в буфере лежит `byte`
(`DockStateValue`), а типизированный доступ — через свойства-обёртки
`Station.DockingState` / `Ship.DockingState` (не `[Export]`).

Чистые функции (`Docking.cs`, без узлов и `GD.Print` — чтобы гонять в юните по
образцу `CollisionDamage.ComputeDamage`):

| функция | контракт |
|---|---|
| `static bool InZone(Vector3 shipPos, Vector3 dockPos)` | `shipPos.DistanceTo(dockPos) <= DockRange` |
| `static bool AllowDock(float vnAbs)` | `vnAbs <= DockSpeedLimit` |
| `static Vector3 CaptureAccel(Vector3 dx, Vector3 v)` | `clamp(k·dx - c·v, MaxSpringAccel)` |
| `static float AlignError(Quaternion shipRot, Vector3 dockAxisWorld)` | угол между носом (`-Z`) и осью стыка, радианы |
| `static bool IsLatched(Vector3 shipPos, Vector3 dockPos, float alignErr)` | `|Δ| <= LatchDistance && alignErr <= acos(LatchAlignCos)` |
| `static bool IsDocked(Vector3 dx, Vector3 v)` | `|dx| <= DockDistance && |v| <= DockSpeedEps` |

---

## 2. Шаги

Порядок: сначала состояние и его транспорт (блок A — без него нечего проверять),
затем запрос и решение сервера (блок B), затем физика захвата (блок C), затем
обрыв пира (блок D), затем дебаг/метрики/фиксация (блок E). Каждый шаг —
observable increment: после каждого шага `./run-tests.sh` (Stage1 104/0) и
`res://tests/Stage2Test.tscn` (82/0), `Stage3Test` (78/0), `Stage4Test` (84/0),
`NetTest` (123/0) остаются зелёными, плюс новые проверки Stage5Test.

### Блок A — состояние стыковки и его репликация

- [ ] **5.1 — `Docking.cs` (чистые функции + enum + константы).** Новый файл
      `scripts/core/Docking.cs` по образцу `CollisionDamage.cs`: `enum
      DockState : byte { None, Requested, Approved, Capturing, Latched, Docked }`,
      константы §1 и функции §1. Никаких узлов, `GD.Print` и `NetworkManager` —
      иначе функции нельзя проверить без дерева сцены (тот же приём, что
      `CollisionDamage.ComputeDamage`, покрытый Stage4Test п.13-п.16 без 3D).
      Проверка: новый `tests/Stage5Test.cs` п.1 — `InZone((0,0,80))` = true,
      `InZone((0,0,120))` = false; `AllowDock(7.9f)` = true, `AllowDock(8.1f)` =
      false; `CaptureAccel` при `dx = (0,0,10)`, `v = 0` даёт `+z` и
      `|a| <= MaxSpringAccel`; `IsLatched`/`IsDocked` на граничных значениях.
      Порог/зона в тестах читаются из констант, а не дублируются числами (кроме
      явных границ).
      Файл: новый `scripts/core/Docking.cs`, новый `tests/Stage5Test.cs` +
      `tests/Stage5Test.Helpers.cs` + `tests/Stage5Test.tscn` (корень `Node` +
      инстанс `res://scenes/Main.tscn`, копия устройства `tests/Stage4Test.tscn`).

- [ ] **5.2 — свойства станции.** `StationState.cs`: `[Export] public bool
      DockApproved`, `[Export] public int DockShipNetId`, `[Export] public byte
      DockStateValue` (+ XML-комментарий: «единственный писатель — сервер-авторитет
      корабля, станция приёмник»). `Station.cs`: не-реплицируемые обёртки
      `DockingState { get => (DockState)State!.DockStateValue; }` и
      `DockerNetId`, и серверный метод `SetDockStateServerOnly(DockState state,
      int shipNetId)` c гейтом: если `NetworkManager.Instance is { IsServer: false }`
      — `GD.PushError` и возврат (образец гейта записи `Hp` в
      `ShipHealthState.cs`, покрытый Stage4Test п.12).
      Проверка: п.2 — у станции из мира `DockApproved == false`,
      `DockingState == None`, `DockShipNetId == 0`; п.3 — `SetDockStateServerOnly
      (Approved, 7)` на сервере ставит `DockApproved = true`,
      `DockStateValue = (byte)Approved`, `DockShipNetId = 7`; п.4 — та же запись из
      копии без авторитета → `PushError`, значение не изменилось.

- [ ] **5.3 — свойства корабля + оба конфига репликации.** `NetState.cs`:
      `[Export] public byte DockStateValue`, `[Export] public int
      DockStationNetId`. `scenes/ShipReplication.tres`: три новых свойства
      (`DockStateValue`, `DockStationNetId`) c `spawn = true` и
      `replication_mode = 2 (OnChange)`; `scenes/StationReplication.tres`:
      `DockApproved`/`DockShipNetId`/`DockStateValue` там же (у станции и так все
      свойства `spawn = true`; добавляем `OnChange` — станция не в батче, дельта
      ей единственный транспорт после спавна). Пути — **с двоеточием**
      (`NodePath("StationState:DockApproved")`), иначе `.tres` молча пустеет
      (§4.1 стадий 2–4).
      Проверка: п.5 — тест читает `.tres` (`GD.Load<SceneReplicationConfig>`) и
      утверждает наличие нового пути с `replication_mode == 2` и `spawn == true`
      (образцы `CheckStationSpawnConfig` / `CheckHealthReplicationConfig` в
      `Stage4Test.Helpers.cs`); п.6 — offline/хост: сервер меняет
      `StationState.DockApproved` → после `Pump()` у копии (не-авторитет)
      `DockApproved == true`. При старте теста — печать всех путей конфига
      (страховка от «пустой tres = огни не горят»).

- [ ] **5.4 — реестр сделки на сервере + инвариант двух сторон.** Сервер держит
      единственную карту «активные стыковки»: `NetworkManager.DockRequests :
      Dictionary<int shipNetId, (int peerId, int stationNetId, DockState state)>`
      (живёт на autoload — переживает смену сцены, как реестры attach-точек,
      §3.4 п.1). Методы сервера: `BeginDock(shipNetId, peerId, stationNetId)`,
      `SetDockPhase(shipNetId, DockState)` (пишет И в `NetState`, И в
      `StationState` — единственный писатель), `AbortDock(shipNetId, string why)`.
      Инвариант: `NetState.DockStationNetId == stationNetId` ↔
      `StationState.DockShipNetId == shipNetId`, оба обнуляются в `AbortDock`.
      Один корабль — одна станция; одна станция — один захватчик
      (`StationState.DockShipNetId != 0 && != shipNetId` → отказ с печатью).
      Проверка: п.7 — после `BeginDock` обе стороны согласованы; п.8 —
      `AbortDock` обнуляет обе стороны и ставит `None`; п.9 — вторая попытка
      сесть на уже занятую станцию другим кораблём → отказ
      (`AllowDock == false` у второй сделки, счётчик отказов растёт).

### Блок B — запрос клиента и решение сервера

- [ ] **5.5 — `RequestDocking` (F) как RPC по образцу выстрела.** Путь клиента —
      копия связки `RequestFire`/`SubmitFireToServer`/`RpcRequestFire`/
      `AcceptFireFrom` (`Ship.cs:1131-1215`): локальный гейт
      `IsLocalPilot && !IsInteractionMode` (тот же `FireGatesNonAim`-принцип),
      затем `RpcId(1, nameof(RpcRequestDocking), Net.ShipNetId, stationNetId)`;
      атрибут — `AnyPeer`, `CallLocal = false`, надёжный, канал 1 (запросы редки,
      терять нельзя — как у выстрела, а не как у кадров ввода на канале 0).
      Серверная часть — `AcceptDockFrom(senderPeer, shipNetId, stationNetId)`
      (гейты В17): только `IsServer`; `Net.ShipNetId == shipNetId`;
      `senderPeer == PilotPeerId` (стыковку запрашивает **пилот**, не наблюдатель);
      `HasAuthority` (у корабля авторитет = сервер, В14=B); станция найдена по
      адресу (`NetworkManager.FindStation(stationNetId) != null`); корабль не
      мёртв (`ShipHealthState.Hp > 0`); нет активной сделки для этого корабля.
      Проверка: шов `TestRequestDock(int senderPeer, int stationNetId)` по образцу
      `TestFire` (`Ship.cs:1160-1167`) — «проверяется боевой код, а не копия».
      п.10 — пилот в зоне, `|vn| = 5` → принято, `DockApproved == true`,
      `DockState == Approved`; п.11 — не-пилот (peer ≠ PilotPeerId) → отказ,
      `DockApproved == false`; п.12 — чужой `shipNetId` в RPC → отказ;
      п.13 — вызов с клиента (`IsServer == false` на копии) → `false`, состояние
      не меняется (В14=B: клиент не может сам себя «одобрить»).

- [ ] **5.6 — гейты «в зоне» и «по скорости» на сервере.** В
      `AcceptDockFrom` после адресных гейтов — геометрические:
      `Docking.InZone(ship.GlobalPosition, dockPoint.GlobalPosition)` и
      `Docking.AllowDock(Mathf.Abs(vn))`, где `vn` считается как в В19:
      нормальная составляющая к станции
      (`CollisionDamage.ApproachSpeed(LinearVelocity, outward)`). Отказ — с
      `GD.Print` (`ship#N: стыковка отклонена: вне зоны / скорость 14.3 > 8`) и
      счётчиком `RejectedDockCount` (для assert'ов). Окно `Approved` живёт
      `DockWindowSec` (сброс по `Engine.GetPhysicsFrames()`-таймеру в
      `_PhysicsProcess` авторитета).
      Проверка: п.14 — корабль в 120 м от точки стыка → отказ «вне зоны»,
      `DockState == None`; п.15 — в 60 м, но `|v| = 20` вдоль оси → отказ «скорость»;
      п.16 — тайм-аут окна: `Approved` без перехода в `Capturing` через
      `DockWindowSec` → `AbortDock("окно истекло")`, `DockApproved == false`;
      п.17 — после `Teleport to Docking Zone` (шаг 5.12) + `|v| <= 8` → принято
      (связка с шагом 5.12).

- [ ] **5.7 — «огни»: визуал разрешения.** На `Approved` станция показывает
      разрешение видимым способом: в `scenes/Station.tscn` появляется
      `DockBeacon : MeshInstance3D` у `DockingPoint` (маленький светящийся
      маркер, `visible = false` по умолчанию), а `Station._Process` присваивает
      `_beacon.Visible = State.DockApproved`. Это **не второй транспорт и не
      RPC**: `visible` — производное от реплицируемого свойства (единственный
      источник истины — `StationState.DockApproved`), поэтому у клиентов маркер
      загорается сам, из реплики (п.6 блока A).
      Проверка: п.18 — у сервера после одобрения `DockBeacon.Visible == true`;
      п.19 — у копии (не-авторитет) после `Pump()` маркер тоже виден («клиенты
      видят огни» — часть критерия готовности (б)); п.20 — после `AbortDock`
      маркер гаснет на обеих сторонах.

### Блок C — мягкая стыковка (серверная пружина) и захват

- [ ] **5.8 — пружина в серверном шаге, а не `Joint3D`.** В `_IntegrateForces`
      (`Ship.cs:757`, только авторитет) на фазах `Capturing`/`Latched` вместо
      тяги пилота считается доводка: `a = Docking.CaptureAccel(target - pos, v)`
      + угловая доводка (`AlignK`/`AlignDamp`, доворот `-Basis.Z` к оси стыка),
      состояние продвигается тем же путём `FlightModel.Step`, но с нулевым
      кадром ввода. `Freeze` при этом не трогаем: сервер интегрирует через
      `CustomIntegrator` (§0 п.3), а не снимая `Freeze`. Поза уходит наружу
      обычным батчем (`NetPublishEveryN = 2`, `Ship.cs:1857`) — второго
      транспорта нет (В18).
      Проверка: п.21 — `SetDockPhase(Capturing)` и корабль в ~40 м от точки: за
      N физшагов расстояние строго убывает без осцилляции (после входа в док-окно
      `|v|` не растёт); п.22 — финальная поза сервера совпадает с `DockingPoint`
      с допуском 0.5 м; п.23 — у копии поза следует за батчем (после `Pump`
      `NetPosition` копии ближе к точке стыка, чем была).

- [ ] **5.9 — предсказание пилота выключается на dock-фазе.** Если
      `Net.DockStateValue >= (byte)Capturing`, клиент-пилот не отправляет кадры
      ввода и не применяет локальное предсказание (`StepPrediction`-путь
      пропускается, hull идёт по интерполяции). Возврат предсказания — при
      `AbortDock` (фаза снялась = полёт обычным, пилот снова предсказывает).
      Это буквальный перевод «клиенты не симулируют пружину локально»
      (`prototype-plan.md:113`) и одновременная защита от ожидаемых
      `hardRollbacks`: сервер ведёт корабль сам, предсказание пилота разошлось бы
      почти на 100%.
      Проверка: п.24 — у клиента-пилота в `Capturing` за 60 физшагов счётчик
      жёстких реконсиляций не растёт; п.25 — в dock-фазе hull клиента не
      двигается от локального ввода (разница hull ↔ интерполированной поза <
      0.05 м).

- [ ] **5.10 — урон станции не мешает стыковке (гейт В19) + финальный замок.**
      В `HandleStationContacts` (`Ship.cs:834`) на фазах `Approved`/`Capturing`/
      `Latched`/`Docked` контакт с той станцией, к которой идёт захват
      (`stationNetId == Net.DockStationNetId`), пропускается целиком (ни урона,
      ни отскока) — иначе серверная пружина сама себя таранит: корпус затягивает
      в цилиндр r=60, и урон капает каждый кадр до смерти корабля. Все прочие
      контакты (другая станция, корабль без активной сделки) — как в стадии 4.
      На `Docked` сервер обнуляет `LinearVelocity`/`AngularVelocity` и ставит
      `Freeze = true` у авторитета («сервер ставит Freeze = true»); снимается
      при отмене/смерти. Единственная точка записи `Freeze` — `ApplyAuthority`
      (`Ship.cs:690-691`), поэтому dock-заморозка пересчитывается именно в нём
      (`Freeze = !HasAuthority || DockIsLatched()`), иначе смена пилота
      (`ApplyPilot` → `ApplyAuthority`) её сотрёт.
      Проверка: п.26 — в `Capturing` корабль вошёл в цилиндр станции:
      `DamageTakenCount` не изменилось (защита от регресса В19); п.27 — `Docked`:
      у сервера `LinearVelocity.Length() == 0` и `Freeze == true`; п.28 — без
      активной сделки таран 20 м/с в ту же станцию по-прежнему наносит урон
      (проверки Stage4Test п.17/п.18 не сломаны); п.29 — `ApplyPilot` во время
      `Latched` не снимает `Freeze`.

### Блок D — обрыв клиента посреди стыковки

- [ ] **5.11 — `OnPeerLeft` отменяет незавершённую сделку.** В обработчике
      отключения пира (`NetworkManager.cs:693`, рядом с `ReleasePilot`, `:382`)
      — пройтись по активным сделкам: если `state < Docked` →
      `AbortDock(shipNetId, "пилот отключился")` (сброс `DockApproved`,
      `DockState → None`, снятие dock-заморозки, возврат обычного полёта:
      корабль дрейфует по инерции, как после `Detach` в BUG-009); если
      `state == Docked` → запись из карты удаляется, **но состояние остаётся**
      (посаженный без пилота корабль стоит на `Freeze` — норма). Правило
      «отменить до `Docked`, после — оставить» фиксируется комментарием в коде.
      Проверка: п.30 — `Capturing` + имитация отключения пира (шов, как в
      Stage4Test п.21 / NetTest-пути `OnPeerLeft`) → у сервера `DockState ==
      None`, `DockApproved == false`, `StationState.DockShipNetId == 0`, и у
      копии после `Pump()` те же значения; п.31 — `Docked` + отключение →
      `DockState` остаётся `Docked`, корабль жив, `Freeze == true` у сервера;
      п.32 — карта активных сделок после отключения пуста.

### Блок E — дебаг-панель, метрики, фиксация

- [ ] **5.12 — `Teleport to Docking Zone` (только сервер).** Кнопка
      `DebugInfoBus.RegisterDebugButton("Teleport to Docking Zone", …)`
      регистрируется только при `HasAuthority` (авторитет корабля — всегда
      сервер, В14=B; копия-клиент кнопки не видит вовсе — ровно тот же приём,
      что `Toggle God Mode`, Stage4Test п.23). Внутри — серверная перестановка
      (`GlobalPosition` = точка рядом со стыком, `LinearVelocity = 0`) в процессе
      авторитета: поза уходит тем же батчем, отдельного RPC нет. Для headless —
      CLI/шов `TeleportToDockForTest()`, ведущий в тот же метод (образец
      `--god` → `ToggleGodMode`, `Ship.cs:604-606`).
      Проверка: п.33 — у клиента `DebugInfoBus` не содержит «Teleport to Docking
      Zone»; п.34 — у сервера шов ставит корабль так, что `Docking.InZone(...)
      == true` и `|v| <= DockSpeedLimit`, после чего `TestRequestDock` проходит
      (п.17 построен на этой связке).

- [ ] **5.13 — `Docking State` в debug-info.** В существующий источник `Ship#N`
      (`Ship.cs:546-572`) добавить `dock = <имя DockState>` и
      `dockStn = <StationNetId | ->`; у станции — свой источник с `dock =`,
      `docker =`. Существующие ключи (`mode =`, `auth =`, `frozen =`, `HP:`) не
      переписываются — только добавление, иначе отвалятся assert'ы Stage3Test
      п.26 / Stage4Test п.26.
      Проверка: п.35 — `DebugInfoBus.BuildInfoText()` содержит `dock = None` до
      запроса и `dock = Docked` после фиксации; п.36 — прежние assert'ы стадий
      1–4 зелёные (общий прогон).

- [ ] **5.14 — Трафик/метрики и регресс-защита.** Все новые свойства — `OnChange`
      (не `Always`), значит `rec/s` не меняется: полный цикл стыковки даёт 4–6
      дельт. Прогон `LATENCY=100 ./run-net-demo.sh 2`: сверяем `rec/s = 80` и
      `KB/s ≈ 7.6` (числа Stage4 §4.12) — рост не выше шумовой погрешности; в
      метриках нет всплеска `hardRollbacks` (следствие 5.9).
      Проверка: п.37 — замеры записаны; CodecTest / BroadcasterTest /
      SpatialIndexTest / StandTest без регресса.

- [ ] **5.15 — Итоговая проверка и фиксация.** `./run-tests.sh` (Stage1 104/0),
      `Stage2Test` (82/0), `Stage3Test` (78/0), `Stage4Test` (84/0), `NetTest`
      (123/0), новый `res://tests/Stage5Test.tscn` — без регресса ни в одном;
      CodecTest / BroadcasterTest / SpatialIndexTest / StandTest — без регресса;
      `LATENCY=100 ./run-net-demo.sh 2` — критерии §2.6 `DEVELOPMENT.md` PASS и
      трафик не вырос. Критерии готовности Этапа 5 (`prototype-plan.md:118`)
      фиксируются числом в `DEVELOPMENT.md`:
      (а) «стыковка запускается клиентом, но выполняется сервером» —
      Stage5Test п.10 + п.13 + п.21–п.23;
      (б) «все клиенты видят одинаковое состояние» — п.19, п.23, п.30, п.35;
      (в) «при отключении клиента посреди стыковки сервер корректно завершает или
      отменяет процесс» — п.30 (отмена до `Docked`) + п.31 (завершённая посадка
      остаётся).
      `DEVELOPMENT.md`: Этап 5 → `[x]`, таблица assert'ов + строка «DockingState
      (5)» в разделе debug-info (`DEVELOPMENT.md:129` уже это обещает); `bugs.md` —
      OBS-запись, если всплывет (кандидаты: `ComputePenetration` у
      `CylinderShape3D`/`StaticBody3D` при малой скорости даёт нормаль `(0,0,0)`;
      реплика byte-свойства не доходит, если путь в `.tres` записан через `/`
      вместо `:` — ловушка §4.1 стадий 2–4).

---

## 3. Риски

1. **`.tres` молча пуст** (повторявшаяся ловушка стадий 2–4): путь без двоеточия
   или `replication_mode = 0` → «огни не загораются», «состояние не дошло», при
   этом код выглядит правильным. Страховка: 5.3 читает конфиг из теста и печатает
   список путей/режимов при старте Stage5Test.
2. **Пружина сама себя таранит** (В19): если 5.10 сделан неточно (сравнение id, а
   не объекта), урон капает во время `Capturing`, корабль гибнет в полёте к
   станции — симптом «стыковка началась и корабль умер». Assert п.26 это ловит.
3. **`Freeze` перезаписывается** при смене пилота/авторитета (`ApplyAuthority` —
   единственная точка записи, `Ship.cs:690-691`). Симптом: захваченный корабль
   снова «оживает» у сервера, если во время `Latched` сервер назначил пилота.
   Assert п.29.
4. **Осцилляция / пролёт сквозь станцию** при дисбалансе `k`/`c` или без
   `MaxSpringAccel`: картинка «качается у дока» либо мгновенный выстрел через
   цилиндр. Лечится параметрами §1; assert п.21 проверяет монотонность.
5. **Реконсиляции пилота в dock-фазе** (если 5.9 не сделан): `hardRollbacks`
   растут ровно на тех прогонах, где «всё правильно» (клиент пилотирует, сервер
   тянет). Assert п.24 — он же индикатор будущего регресса.
6. **Вечная `Approved`** при отсутствии `DockWindowSec`: состояние выглядит
   «застрявшим», повторный запрос клиенту не нужен. Assert п.16.
7. **Буквальное следование тексту плана** (`Joint3D`, физзона `DockingZone`) —
   прямой путь к неработающему `Joint3D` при `CustomIntegrator` и/или второму
   транспорту позы (RPC-стыковка); обе ошибки уже разбирались в В14 и В18 (§0).

## 4. Оценка

По образцу стадии 4 (12 шагов, 84 проверки): 5.1–5.4 — состояние и транспорт
(скучная, но важная часть), 5.5–5.7 — запрос/решение/визуал, 5.8–5.10 — серверная
доводка и гейт урона (самая тонкая физика, здесь же параметры §1), 5.11 — обрыв
пира (одна правка в `NetworkManager` + три assert'а), 5.12–5.15 — панель, метрики
и фиксация. Ожидается ~15 шагов и ~37 проверок Stage5Test. Главный риск переделки
— один (гейт `Freeze`): перед правкой нужно перечислить все вызовы
`ApplyAuthority` (инициализация и `ApplyPilot`, `Ship.cs:650-675`) и править
единственную точку записи, а не добавлять `Freeze = true` в трёх местах.
