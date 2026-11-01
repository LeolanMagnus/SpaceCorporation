# План стадии 4 — Станция + урон от столкновений (серверный расчёт)

> Источник требований: `prototype-plan.md`, «Этап 4» (строки 85–100) и §0 «Сетевая
> архитектура» (строки 3–20: «сервер никогда не доверяет клиентским позициям»).
> Источник конвенций (RPC-гейтинг, каналы, адреса, реестры, тесты):
> `stage0-stage1-rework-plan.md` §2.3/§2.5/§3.9; `stage2-plan.md` §0 (В14);
> `stage3-plan.md` §0 (В15/В16/В17); `net-refactor-plan.md` §1 (В18) и §3 (R0–R10).
> Статус стадии 3: закрыта (`DEVELOPMENT.md` «Этап 3» ✅; Stage1Test 104, Stage2Test 82,
> Stage3Test 78, NetTest 123 — 0 провалов; CodecTest 103046, BroadcasterTest 27,
> SpatialIndexTest 25, StandTest 11).

---

## 0. В19 — разрешаемое противоречие (читать ПЕРЕД кодом)

**Конфликт.** Текст Этапа 4 («Станция существует на сервере и реплицируется
клиентам») и его реализация через уже готовый транспорт стадии 2–3 расходятся
в четырёх местах:

| | `prototype-plan.md` Этап 4 (строки 85–100) | реальность кода после стадий 2–3 + R-плана (в `net-refactor-plan.md` отмечены выполненными R0, R5, R6, R8; R1–R4 по факту уже лежат в коде — `ShipRecordCodec.cs`, `SpatialIndex.cs`, `SnapshotBroadcaster.cs` + BroadcasterTest 27, `DEVELOPMENT.md` «шаг R3», §«батч-транспорт»; не сделаны R7, R9, R10) |
|---|---|---|
| Транспорт состояния | «реплицируется клиентам» → читается как `MultiplayerSynchronizer` | В18 (`net-refactor-plan.md` §1): состояние симулируемых объектов идёт **серверным батчем** `SnapshotBroadcaster` (канал 2, Unreliable); у синхронизатора в `scenes/ShipReplication.tres` все `replication_mode = 0` (R6, выполнено) |
| Урон «в `_IntegrateForces()`» | сервер «в `_IntegrateForces()` … вычисляет импульс» | интегратор (`Ship.cs:691-722`) пишет `sd.Transform/LinearVelocity/AngularVelocity` **целиком сам** и отменяет пред-интегрирование движка (`start.Origin -= v0*dt`); контактные силы/импульсы движок в CustomIntegrator-теле не считает, а `BodyEntered3D` у `Ship` **не подключён вовсе** (поиска `BodyEntered` в `Ship.cs` нет) |
| `ShipHealth` | отдельное поле HP корабля | у `Ship` HP **нет**: `Hp/MaxHp` живут в `TargetState` (`TargetState.cs`) и есть только у мишеней; `NetState.cs` полей HP не содержит |
| `Toggle God Mode (только сервер)` | «в дебаг-панель» | debug-кнопка существует локально в каждом процессе (`DebugInfoBus.RegisterDebugButton`, `NetworkManager.cs:758-760`); у dedicated-сервера GUI-панели нет вообще (headless) |

**Почему нельзя оставить как есть.** (а) Батч — единственный транспорт позы корабля;
если станцию «реплицировать» отдельным синхронизатором с дельтами, мы вернём
дубль-транспорт, который R6 убивал с эмпирической проверкой (`repl/player/s 30 → 3–4`).
(б) Если писать урон из `BodyEntered3D`, который у корабля никто не слушает, код
не выполнится никогда; если из `RpcOnCollision` — это клиентская геометрия (запрет
§0). (в) Если HP повесить на `NetState`, он попадёт в запись батча (`FullRecordSize = 21`,
`PilotRecordSize = 25`, `FarRecordSize = 14`, `ShipRecordCodec.cs:20-25`) — +4 байта
это +19 % бюджета полной записи на каждый репликационный тик, хотя HP меняется в
разы реже позы.

**Решение В19 (принято в этой стадии):**

1. **Станция — статика, холодный путь.** `StaticBody3D` не симулируется, ей нужен
   только спавн-снимок: спавн сервером через `MultiplayerSpawner` (как `Target`,
   гейт `IsServer` в `WorldSpawner.cs:90`) + `StationState`-узел с `StationNetId`
   (тот же общий счётчик `NetworkManager.NextNetId()`, см. `TargetState.cs`),
   реестр `RegisterStation/FindStation` по образцу `_targets`
   (`NetworkManager.cs:219-255`, включая `GD.PushError` на коллизию id) и тот же
   монотонный счётчик `NetworkManager.NextNetId()` (`NetworkManager.cs:108`,
   как `AssignTargetNetIdIfUnset`, `NetworkManager.cs:224-228`). Никакого батча и
   никаких дельт: поза станции
   не меняется, `NetPosition/NetRotation` со `spawn = true` достаточно. В
   `SpatialIndex`/`SnapshotBroadcaster` станция не участвует.
2. **Урон — только из серверного интегратора.** Источник истины — `sd` внутри
   `_IntegrateForces` (его уже имеет только сервер, `CustomIntegrator = HasAuthority`,
   `Ship.cs:649`): перед финальной записью состояния сервер делает
   `PhysicsDirectSpaceState3D.IntersectRay` (или `ComputePenetration`) между
   «предыдущей» и «новой» позицией тела по маске `CollisionLayers.Station`
   (маска `ShipMask = Station | Target`, `CollisionLayers.cs`, уже объявлена) и
   считает `impulse = m × |Δv_n|` — нормальную составляющую изменения скорости
   (`m = Mass`, `RigidBody3D.Mass`, в `scenes/Ship.tscn` `mass = 1200.0`). `BodyEntered3D` не
   используется: он срабатывает на клиентских (замороженных) телах тоже и даёт
   двойной учёт; он допускается как **дополнительный** сигнал на сервере, с тем же
   гейтом `HasAuthority`.
3. **HP корабля — отдельный реплицируемый узел, а не поле батча.** Новый
   `ShipHealthState : Node` (по образцу `TargetState`: `[Export] Hp/MaxHp/GodMode`),
   дочерний у `Ship`, со своим `.tres`-конфигу репликации (пути через двоеточие:
   `ShipHealthState:Hp`) и `replication_mode = OnChange`,
   `spawn = true` — то есть горячий путь урона это **дельта-транспорт поверх В18**
   (по частоте: `Hp` меняется раз в столкновение, не в 20 Гц). Единственный
   писатель — сервер.
4. **God Mode — серверный флаг, кнопка — серверная.** Кнопка
   `DebugInfoBus.RegisterDebugButton("Toggle God Mode", …)` появляется только
   если `NetworkManager.Instance is { IsServer: true }`; у dedicated-сервера
   переключатель — CLI-флаг/консольная команда (headless без GUI), и оба пути идут
   в один серверный метод. `GodMode` реплицируется как свойство (видно клиенту,
   «я неуязвим» горит в UI), но менять его вправе только сервер.
5. **Формула урона — одна функция без Godot-типов** (по образцу `FlightModel` /
   `ShipRecordCodec` / `BroadcasterCore`): `CollisionDamage.Compute(mass,
   deltaVNormal, threshold, maxDamage) -> int`. Тогда все критерии готовности
   («на одинаковой скорости — одинаковый урон», «медленное касание — 0») проверяются
   чистым тестом без физики.

**Что это ломает и как чинится (обязательный список, а не «потом»):**

| ломается | где | как чинится в этой стадии |
|---|---|---|
| `TestStand.tscn` (стенд стадии 1) содержит статику, об которую теперь больно | `scenes/TestStand.tscn` (`collision_layer = 2`, то есть **уже на слое `Station`**) | учёт урона привязан не к слою, а к реестру: урон наносит только то, что разрешается в `FindStation` (стенд в реестр станций не внесён); иначе assert'ы стадий 1–3 начнут сыпаться уроном — проверить полным прогоном наборов стадий 1–3 (шаг 4.12) без изменения числа assert'ов |
| У мишени `Hp` в `TargetState`, у корабля будет в `ShipHealthState` → два имени одного смысла | `TargetState.cs` | не унифицировать силой: у мишени `Hp` — часть её «замороженной» реплики, у корабля — отдельный узел, потому что батч не должен расти (см. В19 п.3). Различие фиксируется комментарием в обоих классах |
| Коснувшись станции, серверный интегратор может «застрять» в геометрии и урон капает каждый кадр | `_IntegrateForces` | урон гейтится парой `(StationNetId, PhysicsFrame)` + кулдаун `CollisionCooldownSec = 0.5f` (по образцу `FireCooldownSec`), и после урона скорость гасится коэффициентом отскока (см. шаг 4.7) — иначе «касание» = фонтан урона |
| Поздно зашедший пир не увидит станцию | `WorldSpawner.cs` | станция спавнится **до** принятия пира (первая сцена, не по кнопке) + спавн-снимок с позой; assert в тесте: у позднего пира `FindStation(id) != null` |
| `Show HP` в debug-info | `DebugInfoBus.RegisterInfoSource`, `Ship.cs:501` | строка `HP: …` добавляется в существующий источник `Ship#N`, отдельный ключ не плодить (§3.11 п.9) |

---

## 1. Термины и константы, которые вводит стадия

| имя | значение | где живёт |
|---|---|---|
| `StationNetId` | адрес станции из общего счётчика `NextNetId()` | новый `StationState.cs` (по образцу `TargetState.cs`) |
| `Hp` / `MaxHp` корабля | 100 / 100 (по умолчанию) | новый `ShipHealthState.cs` |
| `CollisionDamageThreshold` | 12 м/с — ниже него урона нет (мягкое касание) | константа `CollisionDamage` |
| `DamagePerMps` | 2 ед. урона на 1 м/с нормальной Δv сверх порога (→ 62 м/с = 100 урона, кламп) | там же |
| `CollisionCooldownSec` | 0.5 с между двумя учётами урона одним контактом | `Ship.cs` |
| `BounceRestitution` | 0.3 — доля нормальной скорости, возвращаемая кораблю | `CollisionDamage` (чистая функция) |
| `GodMode` | `bool`, пишет только сервер | `ShipHealthState` |

Формула (В19 п.2/п.5, чистая функция):
`vn = dot(v_before − v_after_required, n)`; если `|vn| ≤ Threshold` → урон 0;
иначе `damage = clamp((|vn| − Threshold) × DamagePerMps, 0, MaxHp)`;
новая скорость = `v_after = v_after_model − (1 + BounceRestitution) × vn × n`
(то есть сервер гасит нормальную составляющую и частично отскакивает — иначе
корабль «врезается» в станцию снова каждый кадр).

---

## 2. Шаги

### Блок A — станция как статичный авторитетный объект

- [x] **4.1 — Сцена станции.** `scenes/Station.tscn`: корень `Station : StaticBody3D`
      (новый класс `scripts/core/Station.cs`, `CollisionLayer = CollisionLayers.Station`,
      `CollisionMask = CollisionLayers.Ship`), дочерний `CylinderShape3D`
      (`CollisionShape3D`, радиус 60 м, высота 40 м) + `MeshInstance3D` (цилиндр
      тот же, `MaterialOverride` с emissive — огни ВЫКЛЮЧЕНЫ: `EmissionEnergy = 0`,
      узлы `Light/…` существуют, но `Enabled = false`), дочерний `StationState`
      (шаг 4.2) и дочерний `DockingPoint : AttachPoint` (`Kind = Hatch`,
      `NetId = 0`; класс `AttachPoint.cs` уже поддерживает `Hatch` и читает
      владельца-предка с `NetState` — значит у станции обязан быть узел `NetState`
      либо `StationState` должен решать `OwnerNetId` сам: **проверить
      `AttachPoint.ResolveOwnerNetId` (`GetNodeOrNull<NetState>("NetState")`) и
      расширить его поиском `StationState`, иначе `OwnerNetId == -1` и точка не
      попадёт в реестр**).
      Проверка (`tests/Stage4Test.tscn`/`.cs`, п.1): сцена грузится,
      `station.GetCollisionLayer() == CollisionLayers.Station`, форма —
      `CylinderShape3D`, `GetNodeOrNull<AttachPoint>("DockingPoint") != null`
      и его `Kind == KindOf.Hatch`.

- [x] **4.2 — `StationState` + реестр станций.** Новый узел
      `scripts/core/StationState.cs` (по образцу `TargetState.cs`):
      `[Export] StationNetId`, `[Export] NetPosition`, `[Export] NetRotation`,
      `[Export] MaxHp` (станцию можно будет таранить — Этап 5), `spawn = true` для
      всех; `_EnterTree` → `NetworkManager.AssignStationNetIdIfUnset` (гейт
      `Role == Client` — не выдавать), `_Ready`/`_ExitTree` →
      `RegisterStation`/`UnregisterStation`. В `NetworkManager.cs` — реестр
      `Dictionary<int, StationState>` + `FindStation(int)` ровно по образцу
      `_targets` (`NetworkManager.cs:219-255`, включая `GD.PushError` на коллизию id).
      Отдельного батча нет: нужен `MultiplayerSynchronizer` + `.tres` (пути вида
      `StationState:NetPosition`) с `replication_mode = 0` (Never — дельт нет) и
      `spawn = true` (В19 п.1) — станция приходит только спавн-снимком. Тот же
      механизм, что `spawn = true` у `NetState:NetPosition` в `ShipReplication.tres`
      после R6.
      Проверка: п.2 — после спавна `StationNetId != 0` (гонка адреса, риск §4.2);
      п.3 — `FindStation(id)` возвращает тот же узел, `FindStation(0)` и
      `FindStation(id+1000)` → `null`; п.4 — два спавна не дают коллизию id
      (счётчик монотонен).

- [x] **4.3 — Спавн станции сервером (пир, зашедший в середине, обязан её видеть).**
      В `WorldSpawner.cs`: зарегистрировать `Station.tscn` в спавнабельных сценах
      (рядом с `Ship`/`Target`, `_spawner.AddSpawnableScene(...)`, `WorldSpawner.cs:70,75`)
      и спавнить **на старте сервера** (не по debug-кнопке), выдав адрес до `AddChild`
      (правило `SpawnTarget`, `WorldSpawner.cs:326-334`: гейт `IsServer` +
      `GD.PushWarning` при вызове не на сервере). Контейнер тот же, что у кораблей и
      мишеней: `GameRoot` (скрипт `WorldSpawner.cs`) с дочерним `MultiplayerSpawner`
      `Spawner`, `spawn_path = NodePath("..")` (`scenes/Main.tscn:43-47`) — то есть
      спавн станции вешать внутрь `SpawnWorld()` (`WorldSpawner.cs:262`, уже вызывается
      из-под гейта `IsServer`, `WorldSpawner.cs:89-90`).
      Проверка: п.5 — offline: ровно одна станция в дереве; п.6 — сетевой стенд
      (`./run-net-demo.sh 2` + assert в новом прогоне): пир, подключившийся через
      3 с после спавна, имеет `FindStation(serverId) != null` ДО первого батча
      (холодный путь, как с `NetPosition` у корабля).

- [x] **4.4 — Станция НЕ участвует в батче и в `SpatialIndex`.**
      В `SnapshotBroadcaster` / `SpatialIndex` (шаги R2/R3) — зафиксировать
      фильтром: в батч идут только симулируемые `Ship` (в коде батча `TargetState`
      не участвует — мишени уже едут дельтой синхронизатора по
      `TargetReplication.tres`, `replication_mode = 2`), статика — спавн-снимком.
      Метрика не должна вырасти.
      Проверка: п.7 — `rec/s` до/после добавления станции одинаков (замер в
      Stand/демо: `rec/s` не изменился после 4.3); п.8 — `SpatialIndex.QueryTiers`
      не возвращает станцию ни в одном тире.

### Блок B — HP корабля и серверный урон

- [x] **4.5 — `ShipHealthState` + конфиг репликации.** Новый узел
      `scripts/core/ShipHealthState.cs`: `Hp = 100`, `MaxHp = 100`, `GodMode = false`,
      `DamageTakenCount`, `LastDamage`. Дочерний у `Ship` (в `scenes/Ship.tscn`),
      адрес не нужен (идентифицируется через владельца по `ShipNetId`).
      Новый `scenes/ShipHealthReplication.tres` (обязательно путь ресурса с
      двоеточием внутри: `NodePath("ShipHealthState:Hp")`, иначе пустой ресурс —
      ловушка стадии 1) + `MultiplayerSynchronizer` в `Ship.tscn` с
      `RootPath = ".."`: у `Hp/MaxHp/GodMode/DamageTakenCount/LastDamage`
      `replication_mode = OnChange`, `spawn = true`. Писатель — только
      `HasAuthority` (страховка: setter печатает `GD.PrintErr` при записи от не-авторитета —
      по образцу проверки «сервер потерял истину», `Ship.cs:628-635`).
      В `_Ready` — `DebugInfoBus.RegisterInfoSource` НЕ плодить: строку `HP` даёт
      существующий источник `Ship#N` (`Ship.cs:501`).
      Проверка: п.9 — конфиг читается движком обратно:
      `cfg.PropertyGetReplicationMode(i) == ReplicationMode.OnChange` и
      `cfg.PropertyGetSpawn(i) == true` по всем пяти свойствам (читаются те же
      геттеры, что в NetTest п.14, `tests/NetTest.cs:323`, где после R6 проверяются
      `Never`); п.10 — у offline-корабля `Hp == 100`;
      п.11 — запись `Hp` на не-авторитете не проходит (`Hp` не изменился + PushError).

- [x] **4.6 — Чистая функция урона `CollisionDamage`.**
      `scripts/core/CollisionDamage.cs` (static, без Godot-типов, по образцу
      `FlightModel`/`ShipRecordCodec`):
      `ComputeDamage(int maxHp, bool god, float deltaNormal, float threshold,
      float damagePerMps) -> int` и
      `ApplyBounce(Vector3 v, Vector3 n, float vn, float restitution) -> Vector3`.
      Проверка: п.12 — `|vn| = 11 < Threshold = 12` → 0; п.13 — `|vn| = 30` → 36;
      п.14 — `|vn| = 500` → кламп в `maxHp` (100), не 976; п.15 — `god == true` → 0
      при любой скорости; п.16 — знак: `vn > 0` (сближение) и `vn < 0` (уже
      расходимся) дают одинаковый урон по модулю, но `ApplyBounce` при `vn < 0`
      скорость НЕ меняет (иначе станция «тянет» корабль).

- [x] **4.7 — Серверный учёт контакта в `_IntegrateForces`.**
      В `Ship._IntegrateForces` (`Ship.cs:691-722`) — после `AdvanceFlight` и ДО
      финальной записи `sd.*` (иначе запись перетрёт отскок): проверка сближения с
      ближайшей станцией (шаг 4.7 — `PhysicsDirectSpaceState3D.ComputePenetration`
      между старым и новым `sd.Transform` по маске `CollisionLayers.Station`,
      либо сдвиг луча `prevPos → newPos`). Точный C#-биндинг (`ComputePenetration`
      vs `IntersectShape`/`IntersectRayWithResult` и чем достать `collider` станции)
      определяется пробой `tests/probes/probe_station_pen.*` (§4.4 — делается ДО
      этого шага) и фиксируется комментарием.
      При контакте: `vn` = нормальная составляющая (вдоль нормали станции) скорости,
      `ComputeDamage` → `Health.Hp -= dmg` (гейт `HasAuthority`),
      `Health.LastDamage/DamageTakenCount++`, `GD.Print` строки урона (по образцу
      печати попадания, `Ship.cs:1145`), затем `ApplyBounce` → `sd.LinearVelocity`.
      Кулдаун `CollisionCooldownSec` + запрет повторного учёта тем же
      `(stationNetId, frame)` (см. таблицу «ломается»).
      Если `dmg == 0` (мягкое касание) — урона нет, но отскок всё равно применяется
      (иначе корабль ползёт внутри цилиндра).
      Проверка (стенд с физикой: `TestStepForTest` + реальный `Space3D`, как в
      пробах `tests/probes/probe_ci_*.tscn`): п.17 — корабль летит 40 м/с прямо в
      станцию, после шага `Hp == 100 − 56 = 44`, `DamageTakenCount == 1`,
      `LinearVelocity` развернуто по нормали (отскок); п.18 — 2 м/с: `Hp == 100`,
      `DamageTakenCount == 0`; п.19 — второй кадр подряд в той же точке: счётчик не
      растёт (кулдаун); п.20 — `GodMode` → `Hp` не изменился, отскок есть.

- [x] **4.8 — `Hp <= 0` → серверная смерть корабля.** Сервер: вывод в «уничтожен»
      (по образцу ветки `target.Hp <= 0` у мишени, `Ship.cs:1152-1156`: `QueueFree` +
      `GD.Print`), снятие пилота с
      кресла (`Player` отвязывается от `Seat`, иначе капсула висит на удалённом
      теле — см. `Player.Detach`), respawn по кнопке/команде (или «мёртв, ждём
      респавн» — минимальная реализация, без инвентаря).
      Проверка: п.21 — после `Hp = 0` у сервера узел помечен к удалению,
      `PilotPeerId == 0`; п.22 — у не-авторитета `Hp == 0` и удаление приходит
      движковым путём (спавн-реплика `QueueFree`), процесс жив.

### Блок C — дебаг и критерии

- [x] **4.9 — `Toggle God Mode` (только сервер).** Серверный метод
      `ShipHealthState.SetGodModeServerOnly(bool)`; кнопка
      `DebugInfoBus.RegisterDebugButton("Toggle God Mode", …)` регистрируется
      только при `NetworkManager.Instance is { IsServer: true }` (у dedicated в
      headless панели нет — тогда путь: CLI-флаг `--god`/консольная команда,
      оба идут в один метод). С клиента запрос на god mode не принимается
      (своего RPC нет вовсе; только через кнопку серверного процесса).
      Проверка: п.23 — у клиента кнопка не зарегистрирована
      (`DebugInfoBus` не содержит «Toggle God Mode»); п.24 — вызов серверного
      метода ставит `GodMode == true` и он доходит до клиента через реплику
      (после `Pump` — `GodMode == true` у не-авторитета); п.25 — с включённым
      god mode столкновение 60 м/с → `Hp == 100`.

- [x] **4.10 — `Show HP` в debug-info.** В источник `Ship#N`
      (`Ship.cs:501-…`) добавить `HP: {Hp}/{MaxHp}` + `god=on|off` +
      `dmg#={DamageTakenCount}`.
      Проверка: п.26 — `DebugInfoBus.BuildInfoText()` содержит `HP:` с числом
      (по образцу Stage3Test п.26).

- [x] **4.11 — Клиент не считает урон (главный критерий).** Проверить
      обратное: у клиента (не-авторитет) нет ни одного пути изменить `Hp`:
      `BodyEntered` у клиента не подключён (в `Ship.cs` его нет — так и оставить),
      setter HP на не-авторитете пишет PushError (4.5), RPC с уроном не существует.
      Проверка: п.27 — «клиент попытался отменить урон»: на клиенте вызвать
      локальный `Health.Hp = 100`, затем дождаться реплики → значение равно
      серверному (меньшему); урон не отменяется.

- [x] **4.12 — Итоговая проверка и фиксация.** *(готово: все наборы зелёные —
      Stage1 104/0, Stage2 82/0, Stage3 78/0, Stage4Test 84/0, NetTest 123/0;
      CodecTest 103046/0 (R1), BroadcasterTest 27/0 (R3), SpatialIndexTest 25/0 (R2),
      StandTest R9 11/0. `LATENCY=100 ./run-net-demo.sh 2` — 4 критерия §2.6 PASS,
      трафик из-за станции не вырос: `rec/s = 80`, `KB/s ≈ 7.6`. Критерии Этапа 4
      (а/б/в) — Stage4Test п.17+п.22 / п.18+п.12+п.19 / п.27. `DEVELOPMENT.md`
      Этап 4 → ✅, `bugs.md` → OBS-004.)*
      `./run-tests.sh` (Stage1Test 104), `res://tests/Stage2Test.tscn` (82),
      `res://tests/Stage3Test.tscn` (78), `res://tests/NetTest.tscn` (123),
      новый `res://tests/Stage4Test.tscn` — без регресса во всех;
      CodecTest/BroadcasterTest/SpatialIndexTest/StandTest — без регресса;
      `LATENCY=100 ./run-net-demo.sh 2` — 4 критерия §2.6 `DEVELOPMENT.md` PASS и
      трафик (`KB/s`, `rec/s`) не вырос из-за станции (шаг 4.4).
      Критерии готовности Этапа 4 (`prototype-plan.md:100`) фиксируются числом в
      `DEVELOPMENT.md`:
      (а) «на одинаковой скорости тарана HP тратится одинаково у всех» —
      два замера (или два корабля/два прогона) с одинаковым `|vn|` дают
      идентичный `DamageTakenCount × урон` и одинаковый `Hp` у сервера и у
      не-авторитета после реплики (Stage4Test п.17 + сквозной прогон);
      (б) «при медленном касании урона нет» — п.18;
      (в) «клиент не может отменить урон» — п.27.
      `DEVELOPMENT.md`: Этап 4 → `[x]`, таблица assert'ов; `bugs.md` — OBS-запись,
      если всплывет (кандидат: `ComputePenetration` для `StaticBody3D`/`CylinderShape3D`
      возвращает ноль или неверную нормаль — проверить пробом до шага 4.7).
      Долг R7 (`net-refactor-plan.md` — гейтинг `RpcOnHitConfirmed` по интересу)
      остаётся вне этой стадии, если не мешает.

---

## 3. Чего в этой стадии НЕ делаем

- Стыковка/посадка на станцию, пружина «магнитного захвата» (`CustomIntegrator`-пружина) —
  Этап 5 (`prototype-plan.md:102+`); `DockingPoint` в этой стадии только существует
  как узел-закладка, никакой логики притяжения.
- Магнитные ботинки, выход игрока на палубу, шлюзы (`Kind.Hatch/Floor`) — Этап 6;
  `AttachPoint`-реестр станции используется только для `OwnerNetId != -1`.
- Разрушение станции, обломки, взрыв (слой `Debris` объявлен и не используется),
  регенерация HP, ремонт, щиты.
- Урон от столкновения корабль↔корабль: маска `ShipMask` не включает `Ship`,
  поэтому таран корабля кораблём в этой стадии не считается (решение фиксируется
  комментарием, чтобы не выглядело багом).
- Предсказание урона/отскока на клиенте: клиент ждёт реплику `Hp` и батч позы
  (инерция клиента идёт до прилетевшей позы из батча — В18).
- Interest management/гейтинг событий урона (у `GD.Print`-урона нет RPC-рассылки —
  HP летит репликой свойства, она и есть фильтр «только тем, кому интересен корабль»:
  синхронизатор шлёт дельту всем пирам, получившим узел; при росте мира — перенести на
  В18-путь, записать в `net-refactor-plan.md`).
- Headless-экспорт сервера — Этап 7 (`prototype-plan.md:140-143`).
- ИИ-корабли (R10) — после R9.

---

## 4. Риски и как они проверяются

1. **Пустой `ShipHealthReplication.tres`** (путь ресурса/путь без двоеточия) → `Hp`
   не доезжает, «HP у клиента не меняется» при живом серверном `Hp`. Проверка: п.9 +
   печать `replication_config.properties` при старте (ловушка уже случалась:
   `ShipReplication.tres`, `TargetReplication.tres` — stage3 §4.1).
2. **Урон капает каждый кадр при застревании в цилиндре.** Проверка: п.19
   (кулдаун) + assert «после отскока `dot(v, n) < 0`» (корабль удалился от стены);
   в живом прогоне — `DamageTakenCount` за 5 с касания ≤ 10 (0.5 с кулдаун).
3. **Урон в `BodyEntered3D` вместо интегратора** → двойной учёт (клиентские тела)
   или ноль (у `Ship` сигнала нет вовсе). Проверка: grep по `BodyEntered` в
   `scripts/` должен остаться пустым (шаг 4.7 использует space-state, а не сигналы);
   если сигнал всё же добавляется — обязателен гейт `HasAuthority` и тот же кулдаун.
4. **`ComputePenetration`/`IntersectRay` не видит `CylinderShape3D` станции**
   (неизвестное поведение движковой геометрии; пробы решают до кода). Проверка:
   проб `tests/probes/probe_station_pen.gd`/`.tscn` ДО шага 4.7: печатает
   глубину/нормаль при вдавливании сферы в цилиндр. Если не работает — путь
   «луч `prevPos → newPos` + радиус-смещение», он же в чистом тесте п.17.
5. **Отскок, который не записан в `sd`** → серверная траектория расходится с
   батчем, у пилота `hardRollbacks` сыпется (реконсиляция видит «телепорт»).
   Проверка: LATENCY=100-прогон с тараном: `hardRollbacks` за таран ≤ 1
   (сверка с таблицей §2.6), `predErr ≤ ReconcileThreshold = 0.3`.
6. **Дубль-транспорт за `Hp`** (реплика + будущий батч-путь R-плана). Проверка: п.7
   (`rec/s` не вырос) и эмпирика R6-образца: `repl/player/s` растёт только в момент
   урона (единичные дельты), `wire pkts/s` не меняется.
7. **Тела `TestStand.tscn` начинают наносить урон** → падение assert'ов стадий 1–3.
   Проверка: полный прогон всех четырёх наборов (4.12) без изменения количества
   assert'ов; при регрессе — править слои стенда, а не формулу урона.
8. **God Mode включается у клиента «визуально»** и игрок думает, что неуязвим.
   Проверка: п.23 (кнопки у клиента нет) + строка панели `god=on|off` приходит
   репликой, а не из локального чекбокса.
9. **Гонка `StationNetId` и спавна** (RPC/реестр раньше адреса) — тот же риск,
   что stage3 §4.2. Проверка: п.2 (адрес до `AddChild`) + п.6 (поздний пир).
