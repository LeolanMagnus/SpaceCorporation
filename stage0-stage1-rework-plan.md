# План: доработка стадии 0 + изменения имплементации стадии 1

Документ рабочий. Стадия 1 (`stage1-plan.md`) закрыта: 89 assert'ов, 0 ошибок,
стабильно 60 физ.шагов/с. Здесь описано, **что в стадии 0 было принято, но не
сделано**, и **что придётся переписать в имплементации стадии 1**, когда
появляется сеть.

Проверено по факту в этом проекте (Godot 4.7.2-stable_mono, .NET 8):

| Что | Реальное состояние |
|---|---|
| `scripts/core/NetworkManager.cs` | **нет файла** (в `stage1-plan.md` «Что уже есть» он значился) |
| `scripts/core/CollisionLayers.cs` | есть, 6 слоёв: `Ship Station Character Target DockingZone DebugHelper`; `Reserve` / `Debris` — **нет** |
| `scripts/core/DebugInfoBus.cs` | есть, **без** счётчика пакетов и `Net`-блока |
| `scripts/core/ShipStub.cs` | **нет файла** — удалён на шаге 1.5 стадии 1 (`stage1-plan.md:259`) |
| `scripts/core/ShipHull.cs` | **нет файла** (в плане стадии 0 — есть) |
| `scripts/core/Ship.cs` | есть (результат стадии 1), `RigidBody3D`, без сети |
| `scripts/core/AttachPoint.cs` | есть (стадия 1), без сети |
| `scripts/core/Player.cs` | есть (стадия 1), без сети |
| `scenes/Net/` | **нет папки** |
| `scenes/Ship.tscn` | `Hull (MeshInstance3D)` + 1 коллайдер (`BoxShape3D`) + `Muzzle` + 1 точка `Seat`, без `MultiplayerSynchronizer` |
| `scenes/Main.tscn` | `Main` + `Environment` + `Player` + `DebugLayer`, без `Net` |
| `project.godot` | `[network]` отсутствует; `[physics]` — только `3d/default_gravity=0.0` (физ.тик — значение по умолчанию 60, явно не задан); `main_scene = scenes/Main.tscn` |

### 0.1 Статус выполнения шагов §3.10 (обновляется по мере выполнения)

- [x] **Шаг 1** — `NetworkManager` + `NetRole` + CLI `-- --server/--join/--port` +
      `project.godot` (autoload перед `DebugInfoBus`, `3d/physics_ticks_per_second=60`).
      Проверено: `./run-tests.sh` зелёный (89/0).
- [x] **Шаг 2** — `NetState.cs` + `ShipHull` в `Ship.tscn` + `MultiplayerSynchronizer`
      (root `..`, interval 0.0333) + `ShipReplication.tres` (6 свойств, `NetState/...`,
      spawn=true, OnChange) + ветка по авторитету в `Ship.cs` (`ApplyAuthority`,
      `FreezeMode.Static`). Проверено: тест зелёный.
- [x] **Шаг 3** — `AttachPoint.NetId/OwnerNetId` + реестр `(owner,point)` +
      `RequestAttach/RequestDetach/ApplyAttach/ApplyDetach` в `Player.cs`.
- [x] **Шаг 3b** — смена авторитета `Ship` (В3=B): `PilotPeerId`,
      `AssignShipAuthority` + RPC `ApplyShipAuthority` + очередь `_pendingAuthority`,
      вызовы из `AttachServerSide/DetachServerSide`, RPC-репорт `NetState` на сервер
      (`Reliable`, гейтинг по `SimTick`), гейтинг `ApplyImpulseAuthorityOnly`/
      `ApplyTorqueImpulseAuthorityOnly` (п.10 §3.9).
- [x] **Шаг 4** — `Main.tscn`: `SpawnPoints` (Spawn0/Spawn1) + `GameRoot`
      (`scripts/core/WorldSpawner.cs`) + `MultiplayerSpawner` (`spawn_path=".."`,
      `AddSpawnableScene` Ship/Player); статичные `Ship`/`Player` из сцены убраны;
      `StartSinglePlayerWorld()` (корабль раньше игрока); `Player._worldRoot` —
      явный поиск верхнего `Node3D`-предка. Assert'ы `Stage1Test` обновлены
      (§3.8: GameRoot, SpawnPoints, ShipNetId=1, `Ship#1`, `>= 4` кнопок).
      Проверено: `./run-tests.sh` → checks=98 failures=0.
- [x] **Шаг 4b** — второй корабль + assert'ы §3.9 п.13–17. Создан
      `tests/NetTest.tscn` + `tests/NetTest.cs` (пока п.13–17; п.1–12 — шаг 7).
      `WorldSpawner.SpawnShipAtSpawnPoint(1,"Ship2")` (Spawn1 → `GameRoot/Ship2`,
      ShipNetId=2). `NetworkManager`: `CountShipsPilotedBy(peerId)`, `Ships`,
      `TestExtraPeers` (оффлайн-пиры 2/3 для валидации без UDP), гейтинг
      `AssignShipAuthority` (освобождает прежний корабль пилота перед назначением
      нового → инвариант «≤1 корабль на пира», п.17). `WorldSpawner.DespawnShip(ship)`
      — серверный путь удаления: `Detach` пристёгнутых игроков ДО `QueueFree`
      (Godot освобождает дерево снизу вверх, иначе игрок ушёл бы раньше
      `Ship._ExitTree`, п.16). Assert'ы: п.13 (PublicVisibility==true, нет
      фильтров, SetVisibilityFor не удаляет), п.14 (ReplicationConfig содержит
      ровно `NetState:NetPosition`, spawn=true), п.15 (2 корабля: разные NetId/пути,
      2 источника `Ship#1`/`Ship#2`, AssignShipAuthority(2,2) не трогает Ship1,
      attach к (2,0) → ShipNetId==2), п.17 (переход A→B: счётчик ≤1 всегда),
      п.16 (DespawnShip: игрок выжил, точка исчезла из реестра, FindShip(1)==null,
      перепришпиливается к Ship2 без падения). Переход Offline→Dedicated через
      `StartServer()` в headless (server без клиентов, Rpc no-op).
      Проверено: `NetTest` → checks=36 failures=0 (exit 0, без зависаний и
      ObjectDisposedException); регресс `Stage1Test` → checks=98 failures=0.
- [x] **Шаг 5** — кнопки сети + `Net`-строка + счётчики пакетов + переходы
      Offline↔Dedicated. Кнопки регистрирует сам `NetworkManager` через
      `DebugInfoBus.RegisterDebugButton` в `_Ready` (autoload стоит ВЫШЕ
      `DebugInfoBus`, поэтому кнопки копятся в очереди шины и сливаются при
      `AttachLayer`; `DebugLayer` про сети не знает, а в headless-сервере панели
      нет вовсе). Ровно три кнопки: `Start Server`→`StartServer()`,
      `Join`→`Join("127.0.0.1")`, `Disconnect`→`Disconnect()`; `Host` нет
      (listen-server запрещён, §4 В1). Источник `Net` = `GetNetInfoLine()`:
      роль/self/peers/players/pkts-s(tx/rx)/KB-s/RTT/err. Счётчики пакетов —
      `ENetMultiplayerPeer.Host.PopStatistic(HostStatistic.{Sent,Received}Packets,
      {Sent,Received}Data)` (PopStatistic ОБНУЛЯЕТ — читается ровно раз в интервал
      1 с в `_Process`); RTT по пирам — `ENetPacketPeer.GetStatistic(RoundTripTime)`,
      у клиента пир-сервер (id=1) спрашивается отдельно. `SetRole` сбрасывает
      счётчики и пишет переход в лог. Переходы Offline↔Dedicated (шаг 4, WorldSpawner):
      исправлено — `RemoveOfflinePlayer` и `FindOfflinePlayer` обходят ГРУППУ
      `player` (в offline игрок Reparent под точку крепления, детьми WorldRoot не
      является) и пропускают `IsQueuedForDeletion` (иначе StartServer+Disconnect в
      одном кадре оставили бы 0 игроков). Попутно: `SpawnPlayerAt`/`SpawnShipFromScene`
      ставят позицию ПОСЛЕ `AddChild` (вне дерева get_global_transform → ERROR);
      `SelfId`/`Player._Ready` не зовут `GetUniqueId()` при снятом peer (offline id=1).
      Assert'ы: `Stage1Test` — 3 сетевые кнопки по имени + отсутствие `Host` +
      строка `net: role = offline` в панели (checks=103); `NetTest` — п.5 переходов
      (StartServer Ok→Dedicated, повтор→AlreadyInUse, Disconnect→Ok-переиспользование
      порта, offline-игрок удалён в Dedicated и восстановлен, checks=46).
      Проверено: `./run-tests.sh` → checks=103 failures=0;
      `NetTest` → checks=46 failures=0 (без ERROR движка).
- [x] **Шаг 6** — `SpawnPoints` + `playerKey` (`user://identity.cfg`) + `RequestSpawn`
      (сейчас заглушка) + таблицы `peerId→Player`, `playerKey→LastSeenState`.
      Готово: `PlayerKey` (GUID в `user://identity.cfg`), `struct LastSeenState`
      (`Position`/`Rotation`/`OwnerNetId`/`PointNetId`/`WasAttached`), таблицы
      `_peerPlayers`/`_peerKeys`/`_lastSeen`/`_pendingAttach`; клиентский
      `RequestSpawn()` → `RpcRequestSpawn` (только сервер, авторитет ≥ 2), серверный
      шов `HandleRequestSpawn` со спавном по правилу §2.4 (восстановление из
      LastSeenState, отложенное крепление через `FlushPendingAttach`, идемпотентность,
      выдворение старого слота с записью LastSeenState ДО `DisconnectPeer(force:true)`);
      `OnPeerLeft` пишет LastSeenState перед удалением; `GetNetInfoLine` печатает
      `key = <первые 8 символов>`. Assert'ы §3.9 п.8 в `NetTest` (спавн не в Zero,
      возврат позиции ±0.01, `Velocity == Zero`, разные точки для k1/k2, идемпотентность,
      выдворение) — зелёные.
- [x] **Шаг 7** — `tests/NetTest.tscn` + `run-net-demo.sh` (файлов нет).
      Готово: `tests/NetTest.tscn` (инстансит `Main.tscn`, `process_mode = 3`) +
      `tests/NetTest.cs` (однопроцессный тест на `OfflineMultiplayerPeer`, assert'ы
      §3.9 п.8/13–17); `run-net-demo.sh` — два процесса: dedicated-сервер
      (`--headless -- --server --port`) + N клиентов (`-- --join 127.0.0.1`), число
      клиентов задаёт скрипт (`./run-net-demo.sh 3`), флаги строго после `--`,
      сервер убивается по PID (trap cleanup). Прогон: сервер печатает
      `PeerConnected id = …`, `net: role = dedicated, self = 1, key = …, peers = […]`,
      клиенты — `ConnectedToServer`. Критерий стадии 0 выполнен.
- [x] **Шаг 8** — реконсиляция/интерполяция `ShipHull` (ring buffer).
      Готово: не-авторитет интерполирует картинку `ShipHull` (Node3D), а не
      трансформ замороженного тела (§1.6). В `Ship.cs` — кольцевой буфер снимков
      `(localTime, NetPosition, NetRotation)` (`_interpBuf`), кадр рисуется на
      `InterpDelay = 0.1` с назад (между двумя ближайшими снимками, `Lerp`/`Slerp`);
      до самого старого снимка и после самого свежего — без экстраполяции (рисуем
      ближайший крайний, чтобы картинка не дёргалась при потере пакета). Откат ввода
      не нужен: не-авторитет не подаёт локального ввода на корабль (§2.3). Швы
      автотеста `TestPushSnapshot`/`InterpolateHull`/`TestHullGlobalPosition`
      (шов без UDP). Assert'ы §3.10 п.8 в `NetTest` (середина x≈1, границы без
      экстраполяции, `ShipHull` ≠ тело) — зелёные.

Все сетевые API ниже сверены с `GodotSharp.xml` установленной версии 4.7.2 —
в C# это `MultiplayerApi` (не `MultiplayerAPI`), `Node.SetMultiplayerAuthority(int, bool)`,
`MultiplayerSpawner.AddSpawnableScene(string)`, `MultiplayerSynchronizer.SetVisibilityFor(int, bool)`,
`SceneReplicationConfig.AddProperty(NodePath, int)`.

---

## 1. Что не так со стадией 0 (решения приняты, но не реализованы)

### 1.1 Dedicated server не выделен — хост = игрок
В `prototype-plan.md` (раздел «0. Сетевая архитектура») зафиксировано:
`NetworkManager` — автозагрузка, режимы `host / client / listen / dedicated`,
`--server` / `--headless`. Фактически ни одного режима нет. Пока сервер = первый
игрок, «сервер — источник истины» неотличимо от «клиент №1 — источник истины»,
и весь план авторитетности проверяется на себе же.

**Доработка:** отдельный сценарий запуска
`godot --headless --path . -- --server` (флаги пользователя — строго после `--`,
см. §2.1), который **не** создаёт `Player`, не создаёт `DebugLayer`, не рендерит
UI, и при этом прогоняет те же тесты (`tests/*.tscn`). Это же даёт способ гонять
CI-тесты без окна. Тот же режим `Dedicated` можно получить кнопкой из окна
(§4 В13) — там `DebugLayer` остаётся, но `Player` всё равно нет: разница только в
способе запуска, не в режиме.

Решение по В1 (§4): это **не** альтернатива «серверу-хосту», а единственная
модель. Серверный процесс никогда не является игроком; игрок, который его
запустил, подключается к `127.0.0.1` вторым процессом.

### 1.2 Нет канала «вход → игра»
`ConnectedToServer` / `RequestSpawn` / `PeerConnected` в плане есть, в коде — нет.
Без них непонятно, кто и когда спавнит игрока, и что происходит с тем, кто
зашёл в середину партии, или с тем, кто отключился и зашёл снова (вопрос
«где он появится» — §2.4, решение §4 В2). Отдельного этапа «переезд между
сценами» в `prototype-plan.md` нет: там 7 этапов, последний — «Этап 7.
Headless-сервер, интеграция и полировка», поэтому ссылка на «этап 8» в прошлых
ревизиях была битой и убрана.

### 1.3 Нет разделения «истина» и «картинка»
В плане: сервер гоняет физику, клиент интерполирует. В имплементации стадии 1
`Ship` — обычный `RigidBody3D`, который симулируется **везде**, где узел существует.
Если сейчас просто добавить `MultiplayerSynchronizer` на `GlobalPosition`, получится
классический конфликт: локальная физика и входящие реплики дерутся каждый кадр.

### 1.4 Нет тестового стенда для сети
В плане: «два клиента + сервер на одной машине, `127.0.0.1`». В проекте нет ни
скрипта запуска, ни теста, который проверяет, что RPC дошёл. Тесты стадии 1
однопроцессные.

### 1.5 Нет бюджетов и диагностики
В плане: «бюджет пакетов: < 50 пакетов/с на игрока на сцене». `DebugInfoBus`
в стадии 1 показывает FPS/физшаги/скорость корабля, но не пакеты и не RTT.
Без счётчиков бюджеты непроверяемы.

### 1.6 Нет `ShipHull`
Стадия 0 обещала `ShipHull` как узел-носитель геометрии и точек крепления, чтобы
стадия 1 работала с ним. Стадия 1 вместо этого прилепила всё прямо к `Ship`
(`RigidBody3D` + `CollisionShape3D` + `AttachPoint` в одном узле). Это работает,
но мешает: см. §3.3 (нужен отдельный узел под реплику поворота, чтобы не трогать
`RigidBody3D.Rotation` напрямую).

### 1.7 Нет решения про `Reserve` / `Debris`
`CollisionLayers` в стадии 0 описан как `World / Ship / Character / Target /
Reserve / Debris`. В коде — 6 слоёв (`Ship Station Character Target DockingZone
DebugHelper`), `Reserve` и `Debris` отсутствуют. Набор при этом другой: есть
`Station`/`DockingZone`, которых в описании стадии 0 нет. Решение
нужно сейчас, а не на этапе 5: слои в `.tscn` уже проставлены, и сдвиг нумерации
потом тронет все сцены и тесты.

### 1.8 Нет решения про `MultiplayerSpawner`
В плане он упомянут один раз («`MultiplayerSpawner` / `MultiplayerSynchronizer`»).
В имплементации стадии 1 узлы расходятся вручную: `TestStand` спавнит `Ship`
локально на каждом процессе, `Player` спавнит `Target`. В сетевой версии это
гарантированный рассинхрон (у каждого процесса свой корабль со своим `InstanceId`).

### 1.9 Нет решения про `Rpc` в C#
В плане написано «`ShipStub.Rpc` + `SimStep`», но в C# нет атрибута `[Rpc]` —
есть `[RpcAttribute]` (в 4.7 есть `Mode` / `CallLocal` / `TransferMode` /
`TransferChannel`) и прямой вызов `MultiplayerApi.Rpc(int, GodotObject, StringName, Array)`.
Типы параметров тоже не названы: `Mode` — это `Godot.MultiplayerApi.RpcMode`
(`AnyPeer` / `Authority` / `Disabled`), `TransferMode` —
`Godot.MultiplayerPeer.TransferModeEnum` (`Reliable` / `Unreliable` /
`UnreliableOrdered`). Это надо зафиксировать как конвенцию, иначе каждый новый
RPC будут писать по-своему.

### 1.10 Нет решения про `physics_ticks_per_second` и сеть
Стадия 1 подняла физ.тик до 60. Сеть при этом: 30 Гц репликации — это 2 пакета
на один физ.шаг. Нужно либо гонять репликацию на 60 Гц, либо явно принять, что
физика сервера дискретна 1/60, а интерполяция клиента сглаживает. Без этого
пункта «стабильные 60 физ.шагов» и «< 50 пакетов/с» противоречат друг другу.

### 1.11 Нет решения про `MultiplayerSynchronizer` и `RigidBody3D`
Ключевой технический риск стадии 0, который не проговорён: репликация
`RigidBody3D.GlobalPosition`/`GlobalRotation` на клиенте конфликтует с локальной
симуляцией. Нужен явный паттерн (см. §2.5).

---

## 2. Доработки стадии 0: что сделать, чтобы она стала тем, что задумано

Порядок внутри раздела = порядок выполнения. Каждый пункт закрывается тестом или
проверяемой командой, а не «галочкой в чек-листе».

### 2.1 `NetworkManager` как autoload с тремя режимами

Файл `scripts/core/NetworkManager.cs`, autoload **выше** `DebugInfoBus`
(иначе `DebugLayer` при старте читает ещё не готовый синглтон).

Режимов **три**, см. решение В1 (§4): listen-server в проекте нет, процесс,
поднявший сервер, игроком не является. Перечисление
`NetRole { Offline, Dedicated, Client }`:

| Режим | Как включается | Что создаёт |
|---|---|---|
| `Offline` | нет peer'а (`MultiplayerApi.HasMultiplayerPeer() == false`) | всё как сейчас, стадия 1 работает без изменений |
| `Dedicated` | CLI `-- --server` (`OS.GetCmdlineUserArgs()`) **или** кнопка `Start Server` в дебаг-панели (§4 В13) | только мир + `GameRoot` + спавнеры + тесты; без `Player`. `DebugLayer` не создаётся при CLI-запуске и остаётся при запуске кнопкой |
| `Client` | CLI `-- --join <ip>` или кнопка `Join` в дебаг-панели | мир + чужие игроки; **свой** игрок приходит репликой спавнера (§2.4) |

Игрока-хоста нет ни в одном режиме: игрок, запустивший сервер, отдельно
поднимает клиент и делает `Join("127.0.0.1")`. Отсюда два правила, которые
больше нигде не обсуждаются:

- `MultiplayerApi.GetUniqueId() == 1` бывает только у серверного процесса, и
  узлы с авторитетом `1` — это серверные тела (мишень, планета, `Ship` с
  свободной кабиной по В3=B), но **никогда** не пир-игрок;
- код «мой игрок» обязан определяться как
  `player.GetMultiplayerAuthority() == MultiplayerApi.GetUniqueId()`, а не
  `== SelfId && IsServer` и не «первый спавненный».

Два способа стать dedicated-сервером различаются только моментом создания UI
(оба разрешены решением В13, §4): CLI `-- --server --headless` (UI не создаётся
никогда — это основной путь для стенда и CI) и кнопка `Start Server` в уже
запущенном окне (путь игрока и отладки: `DebugLayer` к этому моменту уже создан
и остаётся, мир рендерится, но `Player` не создаётся вовсе). Оба дают один и тот
же код `IsServer == true` и `SelfId == 1`, и это важно: серверная ветка не должна
знать, каким способом её запустили.

Константы в одном месте: `Port = 24567`, `MaxPlayers = 8`, `ReplicationHz = 30`.

Методы (сигнатуры сверены с `GodotSharp.xml` 4.7.2):

```csharp
public Error StartServer(int port = Port, int maxPlayers = MaxPlayers)
    // var peer = new ENetMultiplayerPeer();
    // peer.CreateServer(port, maxPlayers, channelCount: 3, inBandwidth: 0, outBandwidth: 0);
    // MultiplayerApi.MultiplayerPeer = peer;   // вызывается ТОЛЬКО из Dedicated-ветки

public Error Join(string ip, int port = Port)
    // var peer = new ENetMultiplayerPeer();
    // peer.CreateClient(ip, port, channelCount: 3, inBandwidth: 0, outBandwidth: 0, localPort: 0);
    // MultiplayerApi.MultiplayerPeer = peer;

public void Disconnect()   // MultiplayerApi.MultiplayerPeer?.Close(); MultiplayerApi.MultiplayerPeer = null;
public NetRole Role      // Offline / Dedicated / Client — единственный источник
                         // правды о режиме; наружу отдаётся и в DebugLayer, и в
                         // проверки (§3.11 п.10), потому что наличие панели UI о
                         // режиме ничего не говорит
public bool IsServer   => MultiplayerApi.IsServer;
public bool IsClient   => !IsServer && MultiplayerApi.HasMultiplayerPeer();
public bool IsActive   => MultiplayerApi.HasMultiplayerPeer();
public int  SelfId     => MultiplayerApi.GetUniqueId();
```

Метод называется `StartServer`, а не `Host`: слово «host» в этом проекте
означает «listen-server», которого мы сознательно избегаем. Кнопки `Host` в
дебаг-панели нет (§3.7, §4 В13) — есть `Start Server`, которая делает **этот же
процесс** dedicated-сервером; игрока в этом окне не будет.

`Disconnect()` обязан возвращать в `Offline`: после `StartServer` из UI игрок
должен иметь возможность выйти из серверного режима и продолжить одиночную игру
в том же окне, не перезапуская процесс (assert в §3.9 п.5).

Отдельный случай, который надо решить в коде сразу: `StartServer` нажимается из
режима `Offline`, где `Player` **уже** создан. Роль `Dedicated` означает «игроков
нет» (§2.4), поэтому существующий одиночный `Player` при переходе в `Dedicated`
удаляется, а `Disconnect` возвращает `Offline` и спавнит одиночного игрока
заново. Оставить его «как декорацию» нельзя: это ровно тот иллюзорный
«игрок-сервер», от которого отказались в §1.1 и §4 В1, и он начнёт слать ввод на
сервере.

Разбор аргументов (`--server`, `--join <ip>`, `--port <n>`) — по
`OS.GetCmdlineUserArgs()`. Важно: пользовательские аргументы идут **после**
разделителя `--` (в `--help` 4.7: «Separator for user-provided arguments.
Following arguments are not used by the engine, but can be read from
`OS.get_cmdline_user_args()`»). То есть строка запуска сервера:

```
godot --headless --path . -- --server
godot --path . -- --join 127.0.0.1          # второй процесс = первый игрок
```

`--headless` — движковый флаг (он же `--display-driver headless
--audio-driver Dummy`), он остаётся **до** `--`; наши флаги — после. Если
написать `--server` до `--`, движок его проигнорирует, и процесс стартует как
обычный клиент с UI: это типичная причина «сервер не поднялся, но ошибок нет».

`channelCount = 3` — три канала ENet: 0 = снимки состояния (unreliable),
1 = ввод и события (reliable), 2 = редкие крупные (спавн-данные, чат).
`MultiplayerPeer.TransferChannel` / `TransferMode` задают канал по умолчанию для
узла, `[Rpc(TransferChannel = ...)]` — для конкретного метода.

`CreateServer` / `CreateClient` в 4.7 (сигнатуры взяты из `GodotSharp.xml`,
число параметров 5 и 6 — это **не** старый вариант с `channelMode`):

```
ENetMultiplayerPeer.CreateServer(port, maxClients, channelCount, inBandwidth, outBandwidth) -> Error
ENetMultiplayerPeer.CreateClient(address, port, channelCount, inBandwidth, outBandwidth, localPort) -> Error
```

`inBandwidth` / `outBandwidth` — байт/с, `0` = без лимита; они же задают размер
окна для надёжных пакетов, то есть напрямую влияют на «бюджет < 50 пакетов/с».
`CreateServer` возвращает `Error.AlreadyInUse`, если peer уже открыт (нужен
`MultiplayerPeer.Close()` перед повторным `StartServer`), и `Error.CantCreate`,
если порт занят. Молча игнорировать код возврата нельзя: при dedicated-запуске
неудача `StartServer` должна завершать процесс с `SetExitCode(1)` и текстом
«port busy?», иначе в скрипте-стенде молча висит процесс без сервера.

### 2.2 Подписка на события соединения

В 4.7 события висят на `MultiplayerApi` — в `GodotSharp.xml` у него ровно пять:
`ConnectedToServer`, `ConnectionFailed`, `ServerDisconnected`, `PeerConnected`,
`PeerDisconnected`. В C# подписка через `+=`
(`MultiplayerApi.PeerConnected += OnPeerConnected;`). У `MultiplayerPeer` своих
событий два (`PeerConnected`, `PeerDisconnected`) — дублировать их не нужно.

Обработчики обязаны:
- `ConnectedToServer` (на клиенте) → роль `Client`, запросить спавн (§2.4).
  Без этого обработчика клиент «подключился», но в мир не вошёл. Именно этот
  сигнал ловит и игрок, запустивший dedicated-сервер: он подключается к своему
  же порту как обычный клиент, и путь у него тот же, что у всех (§4 В1).
- `PeerConnected` (на сервере) → создать игрока этого пира (§2.4), выдать авторитет;
- `PeerDisconnected` → удалить узлы игрока, освободить слот (на сервере — по id
  пира, на клиенте — единственный случай это сам сервер). На сервере порядок
  внутри обработчика жёсткий: **сначала** записать `LastSeenState` по
  `playerKey` этого слота, **потом** удалять узлы — после `QueueFree` читать
  трансформ не с чего (§2.4, §4 В2 п.5);
- `ConnectionFailed` (на клиенте) → вернуть роль в `Offline` и напечатать причину;
- `ServerDisconnected` (на клиенте) → «сервер пропал»: это не то же самое, что
  `ConnectionFailed` (соединение было установлено). Возврат в `Offline` +
  уведомление в `DebugLayer`.

Два факта про сами сигналы, которые экономят время на отладке стенда:

- у клиента `PeerConnected` **не** приходит на самого сервера — этот пир у
  клиента не появляется в списке. Единственный сигнал «я соединился с сервером»
  — `ConnectedToServer`; путать их значит получить «клиент подключился, но
  ничего не запросил»;
- у сервера `ConnectedToServer` не срабатывает никогда (он не клиент), поэтому
  вся серверная логика входа висит только на `PeerConnected`.

Отдельно решить, что делает сервер при `MultiplayerPeer.RefuseNewConnections = true`
(слотов нет) — отказать до соединения, а не после.

### 2.3 RPC-конвенция и `InputFrame`

В C# атрибут — `[Rpc]` (`Godot.RpcAttribute`, конструктор
`RpcAttribute(Godot.MultiplayerApi.RpcMode)`; свойства `Mode`, `CallLocal`,
`TransferMode`, `TransferChannel`). Вызов — `Node.Rpc(StringName, params Variant[])`
и `Node.RpcId(long peerId, StringName, params Variant[])` (в 4.7 у `Node` есть
оба; `RpcId` принимает **`long`** ID пира). Зафиксировать три шаблона:

```csharp
// 1. клиент → сервер, ненадёжно, часто (ввод на каждый физ.шаг)
[Rpc(Mode = MultiplayerApi.RpcMode.AnyPeer, CallLocal = false,
     TransferMode = MultiplayerPeer.TransferModeEnum.Unreliable)]
public void SubmitInput(uint tick, uint buttons, Vector2 look) { ... }
// вызов на клиенте: SubmitInput.rpc_id(1, tick, buttons, look);
// в C#: RpcId(1L, nameof(SubmitInput), tick, buttons, look);

// 2. сервер → все, надёжно, редко (состояние стыковки, смерть)
[Rpc(Mode = MultiplayerApi.RpcMode.AnyPeer,
     TransferMode = MultiplayerPeer.TransferModeEnum.Reliable)]
public void AnnounceState(...) { ... }              // .rpc(...)

// 3. только авторитетный пир выполняет (телепорт, выдача авторитета)
[Rpc(Mode = MultiplayerApi.RpcMode.Authority)]
public void AssignAuthority(...) { ... }            // .rpc_id(peerId, ...)
```

Важно: `prototype-plan.md` пишет «серверные апдейты — rpc с пометкой authority».
В Godot `RpcMode.Authority` означает «метод выполнится только на том пире, у
которого узел имеет авторитет», а не «только на сервере». Для рассылки состояния
с сервера всем нужен `AnyPeer` + гейтинг внутри (`if (!MultiplayerApi.IsServer) return;`).
Проговорить это в конвенции, иначе половина кода будет молча игнорировать реплики.

`InputFrame` — упакованный ввод: `uint buttons` (битовая маска W/S/A/D/Space/
Ctrl/Z/X/Q/E/огонь) + `Vector2 look` + `uint tick`. Никаких `float`-осей и
`String` — размер пакета важен. `tick` = `Engine.GetPhysicsFrames()` (в 4.7 есть
`Engine.GetPhysicsFrames()`), он же используется для реконсиляции.

Правило: **не-авторитетный клиент никогда не отправляет позицию, скорость, урон
или «я попал»**. Только намерение + тик. Проверка — grep по коду на
`GlobalPosition` в аргументах `rpc`.

Исключение, введённое §4 В3 = B: **авторитетный пир обязан репортить состояние
тела, которым он владеет.** Сервер не интегрирует `Ship` под пилотом (§2.5 п.4),
и без гарантированного канала у сервера нет никакой картины мира:
`MultiplayerSynchronizer` отправляет свойства только с авторитетного пира (§3.3),
а авторитет — клиент, то есть собственный синхронизатор сервера молчит, а чужой
может смениться в середине полёта. Репорт — это `NetState`-снимок
(`NetPosition`/`NetRotation`/`NetLinearVelocity`/`SimTick`), 15–20 Гц (частота
валидации, а не картинки, §2.9), `Mode = Authority`, шлётся `RpcId(1L, ...)`
строго тем пиром, у которого `IsMultiplayerAuthority() == true`. Формулировка
правила поэтому такая: позицию отправляет тот, кто её вычислял, и только он;
остальные — никогда.

### 2.4 Вход в игру: `ConnectedToServer` → `RequestSpawn` → спавн

Порядок один для **всех** игроков, включая того, кто поднял dedicated-сервер
(§4 В1): серверный процесс сам никого не спавнит «для себя», игрок появляется
только как реакция на подключение клиента.

- Клиент после `ConnectedToServer` шлёт `RequestSpawn(playerKey).rpc_id(1)` (не
  после `PeerConnected` — у клиента этот сигнал про сервер не приходит, см. §2.2);
- сервер в `PeerConnected(id)` резервирует слот, а спавнит по `RequestSpawn`
  от этого `id`: проверяет слот, затем
  `var p = scene.Instantiate<Player>(); root.GameRoot.AddChild(p);
  p.SetMultiplayerAuthority(peerId);`
  (`Node.SetMultiplayerAuthority(int authority, bool recursive = true)`);
- `MultiplayerSpawner` (`SpawnPath = ../GameRoot`) с
  `AddSpawnableScene("res://scenes/Player.tscn")` повторяет узел у всех пиров;
- сервер ставит стартовую позицию **до** `AddChild`, чтобы первый же снимок
  реплики был корректным;
- кто зашёл в середине партии, получает полное состояние через спавн-данные
  (`MultiplayerSpawner.Spawn(data)` + `SpawnFunction` — если нужен кастомный узел).

#### Объекты мира спавнятся раньше игроков (следствие §4 В4 = B)

`Ship` и `Station` — не собственность игрока. Их спавнит **только сервер**, при
старте мира или по игровому событию, и **не** на каждого игрока: корабль один на
всех и переживает отключение любого пира. Игрок — пассажир, он появляется и
исчезает, корабль остаётся. Отсюда два порядка, которые надо соблюдать:

1. **При старте сервера: сначала корабли и станция, потом игроки.** Игрок, у
   которого `LastSeenState.WasAttached == true`, ссылается на точку крепления
   через `(OwnerNetId, PointNetId)` (§3.4); если корабль ещё не заспавнен,
   реестра точек просто нет, и сервер уходит в ветку «стартовая точка» вместо
   возврата в кабину (§2.4 п.4). Спавн мира — часть `StartServer()`/`StartSingle
   PlayerWorld()`, а не обработки `RequestSpawn`.
2. **При удалении корабля: сначала переписать `LastSeenState`, потом удалять
   узел.** Сервер обходит всех пристёгнутых к этому кораблю игроков и либо
   сбрасывает им `WasAttached = false` (возврат — на стартовую точку), либо
   пересаживает на ближайшую валидную точку. `QueueFree` раньше этого шага
   означает, что при возврате игрок ссылается на несуществующую точку.

Тот же `MultiplayerSpawner` и тот же `AddSpawnableScene` покрывают и `Player`, и
`Ship`, и `Station` — инфраструктура одна, меняется только то, кто инициирует
спавн (игрока — реакция на `RequestSpawn`, объекты мира — сервер сам).

#### Идентичность: `playerKey`, а не `peerId` (решение §4 В2)

`peerId` (`unique_id` ENet) выдаётся заново при каждом переподключении, поэтому
он не годится как ключ «где этот игрок был». Идентичность — строковый
`playerKey`: GUID, созданный клиентом один раз и лежащий в
`user://identity.cfg`. Клиент шлёт его в `RequestSpawn(playerKey)`, сервер
хранит две таблицы:

```
peerId    → Player          // живой слот, живёт до PeerDisconnected
playerKey → LastSeenState   // позиция/крепление, живёт до рестарта процесса
```

`LastSeenState { Vector3 Position; Basis Rotation; int OwnerNetId; int PointNetId;
bool WasAttached; }` (`Basis`, а не `Quaternion`: в C#-обёртке `Transform3D.Basis`
имеет тип `Godot.Basis`, `Quaternion` там появляется только через
`Basis.GetQuaternion()` — при сериализации в RPC-словарь это разные типы).
Это **серверное** знание: клиент не вправе запросить
произвольную позицию, иначе вся модель авторитетности (§2.5) обходится одним
RPC. Аутентификации ключа на стадии 0 нет — подделать `playerKey` = занять чужой
слот, это принятая цена (§4 В2).

Минус `user://` как хранилища ключа: папка `user://` привязана к имени проекта и
к бинарнику. Проверено прогоном (`tests/probe_paths.gd`, Godot 4.7.2 mono):
`OS.GetUserDataDir()` = `C:/Users/<user>/AppData/Roaming/Godot/app_userdata/SpaceCorporation`,
и **редактор, и CLI-запуск `godot --path .` дают одну и ту же папку** — значит
один и тот же `playerKey` работает и в редакторе, и в стенде из скрипта. В
собранном билде папка может отличаться (зависит от настроек экспорта), поэтому
проверять «возврат на место» между редактором и билдом нельзя — это два разных
«я», и вывод о «сервер забыл позицию» будет ложным. Дешёвая защита: в `Net`-строке панели печатать
`playerKey` коротко (первые 8 символов, §3.7) — тогда сразу видно, что два
процесса считают себя разными игроками, и это перестаёт выглядеть как «сервер
потерял мою позицию».

#### Где спавнится игрок (решение §4 В2)

Правило: **возврат туда, откуда вышел; если данных нет — стартовая точка.**

1. есть `LastSeenState` и `WasAttached == true` → спавн **в точку крепления**:
   сервер после спавна вызывает `AttachTo` по паре `(OwnerNetId, PointNetId)`
   (§3.4), мировая позиция берётся из точки. Это предпочтительный случай:
   координата «где стоял» внутри движущегося корпуса через минуту уже внутри
   геометрии, а точка — единственный адрес, который остаётся валидным;
2. есть `LastSeenState`, игрок был свободен → `GlobalPosition` = сохранённая,
   `GlobalRotation` = сохранённая, **скорость обнуляется** (`LinearVelocity =
   Vector3.Zero`, `AngularVelocity = 0`). Восстановление скорости даёт импульс
   при возврате в движущийся корабль, и сервер потом тратит кадры на его
   «оправдание»;
3. сохранённой позиции нет (первый заход) или точка крепления пропала (корабль
   удалён) → **стартовая точка** из `Main/SpawnPoints/*` (§3.6): сервер берёт
   первую свободную — ту, у которой сфера спавна не пересекается с существующими
   телами, — и перебирает список по кругу. Хардкод `Vector3.Zero` запрещён:
   стартовая позиция обязана быть данными сцены;
4. точка крепления на момент возврата принадлежит кораблю, который ещё не
   заспавнен (порядок спавна `Ship`/`Player`) → сервер откладывает крепление до
   регистрации точки в реестре `(ownerNetId, NetId)` (§3.4), а не спавнит игрока
   в пустоту.

Позиция всегда ставится **до** `AddChild` (см. выше): иначе первый снимок реплики
приходит с нулевым трансформом, и клиент на кадр рисует игрока в центре мира.

#### Смена роли и повторный вход

`Disconnect` на клиенте → сервер получает `PeerDisconnected(peerId)` и пишет
`LastSeenState` по `playerKey` этого слота **до** удаления узлов игрока: после
`QueueFree` читать позицию не с чего. Тот же игрок, зашедший второй раз в этом
же серверном процессе, попадает в пункт 1 или 2. Рестарт dedicated-процесса
обнуляет обе таблицы — все на стартовые точки (в память, на диск не пишем, §4 В2
п.6).

Следствия dedicated-модели, которые надо зафиксировать в коде сразу:

- `SetMultiplayerAuthority(1)` для `Player` не вызывается **никогда**: авторитет
  игрока — всегда `id ≥ 2`. Проверка «это мой игрок» на клиенте —
  `GetMultiplayerAuthority() == MultiplayerApi.GetUniqueId()`, на сервере —
  `GetMultiplayerAuthority() == peerIdOf(slot)`. Ветка «авторитет = 1 = я»
  отсутствует как класс;
- серверный процесс обязан уметь жить без `Player`: `GameRoot`, `MultiplayerSpawner`,
  `Ship`-мишени и тесты на сервере есть, а локального игрока нет;
- `RequestSpawn` идемпотентен: повторный запрос от того же пира не создаёт
  второго игрока (сервер хранит `peerId → Player`);
- `playerKey` уже занят другим живым `peerId` (оборванное соединение ещё не
  отвалилось по таймауту ENet) — сервер обязан выбрать одно из двух явно:
  выгнать старый слот (`MultiplayerApi.MultiplayerPeer.DisconnectPeer(oldPeerId,
  force: true)` — метод на `MultiplayerPeer`, не на `MultiplayerApi`; сигнатура
  `DisconnectPeer(int peer, bool force = false)` сверена с `GodotSharp.xml`) и
  спавнить заново) или ответить `SpawnDenied.reason = "already-in-game"`. Для
  `force: true` сигнал `PeerDisconnected` по этому пиру **не** приходит
  (`GodotSharp.xml`: «If force is true the PeerDisconnected signal will not be
  emitted for this peer»), значит `LastSeenState` надо записать **до** выгонания,
  иначе позиция теряется;
- если слотов нет, сервер отвечает `SpawnDenied.reason` тому же пиру, а не молчит,
  иначе клиент зависает в «подключён, но без игрока».

### 2.5 Паттерн «истина / картинка» для `RigidBody3D` (главный риск)

Схема, которую надо принять до правки сцен:

```
Ship (RigidBody3D)              ← авторитет (пилот или сервер): симулируется;
│                                  все остальные пиры: Freeze = true
├── CollisionShape3D            ← есть у всех: авторитету — для интегрирования,
│                                  серверу — для контактов с миром (у замороженного
│                                  тела контакт остаётся), остальным — не мешает
├── NetState (Node, скрипт)     ← свойства Position/Rotation/Linear/Angular — в SceneReplicationConfig
└── ShipHull (Node3D)           ← картинка: у авторитета = трансформ тела,
                                   у остальных = интерполяция к NetState (буфер ~100 мс)
```

Правила:
1. `MultiplayerSynchronizer.RootPath` = `Ship`; реплицируются свойства **`NetState`**,
   а не `GlobalPosition` тела. Иначе физика клиента и реплика дерутся каждый кадр.
2. `MultiplayerSynchronizer.ReplicationInterval` = 1/30, `DeltaInterval` —
   отдельный интервал для «дельта»-свойств; `PublicVisibility = true` для
   кораблей, `false` + `SetVisibilityFor(peer, true)` для чужих игроков в
   невидимых зонах.
3. Авторитет `Ship` — **один на все времена, но не всегда `1`** (§4 В3 = B):
   занятая кабина ⇒ авторитет = пир-пилот, кабина свободна ⇒ авторитет = `1`
   (сервер). Тело всегда симулирует ровно один пир, остальные — только
   интерполируют картинку, поэтому предсказание с откатом кораблю не нужно ни в
   одном из двух режимов. Предсказание с откатом нужно **игроку** (`CharacterBody3D`,
   авторитет = пир): ring buffer введённых `InputFrame` (120 записей = 2 с при
   60 Гц), откат и переигровка при расхождении с репликой > 0.05 м.
   При dedicated-модели (§4 В1) это относится и к игроку, запустившему сервер:
   он такой же клиент с авторитетом `id ≥ 2`, поэтому rollback у него работает
   и наблюдаем так же, как у всех. Отдельной «нулевой задержки хоста», при
   которой откат не срабатывает никогда, в проекте не существует.
4. На **любом не-авторитете** — `Freeze = true`, `FreezeMode = FreezeModeEnum.Static`,
   `NetState` → интерполяция в `ShipHull`, физика не участвует в картинке. При
   В3=B в это множество попадает и сервер, пока корабль под пилотом: серверное
   тело заморожено и только получает репорты `NetState`. `Static`, а не
   `Kinematic`: кинематическое замороженное тело при перемещении расталкивает по
   пути то, чего на авторитете нет.
5. Авторитет назначается по одному правилу на тип узла, и слово «корабль» в
   тексте больше не двусмысленно:
   * **`Ship` (тело `RigidBody3D`)** — авторитет = пир-пилот, если кабина занята,
     иначе `1`. Кабина считается занятой ровно тогда, когда узел `Player`
     закреплён (`Reparent`) за `AttachPoint` этого корабля и этот `Player` имеет
     авторитет данного пира. «Корабль-кабина» = то же самое, что `Ship`: у
     корабля одна кабина, отдельного узла «кабина» нет, поэтому и отдельного
     второго авторитета нет;
   * **`Player` (`CharacterBody3D`)** — авторитет = собственный пир, всегда;
     он не меняется от того, пристёгнут игрок к кораблю или нет. Именно поэтому
     авторитет корабля выводится из факта крепления пилота, а не наследуется от
     игрока: игрок может быть пристёгнут к чужому кораблю, и тогда пилотом он не
     становится;
   * **станция, мишени, обломки, серверные триггеры** — авторитет `1` всегда.

   Смена авторитета `Ship` выполняется только сервером и только через
   `AssignShipAuthority` (§4 В3 п.3): `SetMultiplayerAuthority(peerId, recursive:
   true)` на всех пирах, потому что сама по себе смена авторитета другим пирам
   не реплицируется и дочерним узлам не наследуется.

   В dedicated-процессе `GetMultiplayerAuthority() == 1` у серверных тел означает
   «источник истины — я», а у клиента то же значение означает «источник истины —
   сервер, я только интерполирую». Различает эти две ситуации не номер авторитета,
   а `NetworkManager.Role` (`Dedicated` против `Client`), поэтому в коде тела
   проверяют `IsMultiplayerAuthority()`, а не `GetMultiplayerAuthority() == 1`.
   При В3=B это правило критично: у сервера корабль под пилотом имеет авторитет
   `id ≥ 2`, а у клиента тот же корабль имеет авторитет, равный его собственному
   `id` только если пилот — он сам.
6. Кто дёргает `ApplyImpulse` (и вообще любой силовой вызов у `RigidBody3D`):
   **только тот пир, у которого `IsMultiplayerAuthority() == true`**, и только
   локально, без RPC. Порядок для корабля под пилотом:

   ```
   пилот: SubmitInput(tick, buttons) ──rpc_id(1)──▶ сервер
   сервер: гейтинг (слот, «этот пир пристёгнут к этому кораблю», кулдаун) ─▶
           rpc_id(pilotId, "ApplyIntent") ─▶ пилот: ApplyImpulse/ApplyTorque локально
   пилот: репорт NetState ──rpc_id(1)──▶ сервер (и синхронизатор пилота → всем)
   ```

   Сервер не вызывает `ApplyImpulse` на своём теле, пока оно заморожено, и не
   «догоняет» его импульсом после репорта: это дало бы двойное интегрирование.
   Сервер держит **намерение** (`IntentLog`: `tick`, маска, `SimTick` применения)
   и сверяет по нему репорты — расхождение по дельте скорости больше допуска
   означает отзыв авторитета, а не коррекцию тела.
   В offline (`MultiplayerPeer == null`) вся цепочка сворачивается в один
   локальный вызов: авторитет = `1`, `IsMultiplayerAuthority() == true`,
   `Rpc` без peers не работает, поэтому `Ship.RegisterDebugButton("Nudge Ship")`
   продолжает дёргать `ApplyImpulse` напрямую, и все 89 assert'ов стадии 1
   остаются зелёными.

### 2.6 Тестовый стенд сети (обязателен, а не опция)

Поскольку dedicated — единственная модель (§4 В1), стенд из двух и более
процессов становится основным способом проверить стадию 0: без второго процесса
в проекте вообще нет ни одного клиента, и авторитетность проверить нечем.

- `scenes/Net/NetStand.tscn` — минимальная сцена стенда: `Net` (NetworkManager-узлы) +
  `GameRoot` (Node3D) + `MultiplayerSpawner` + дебаг-панель. Название
  **не** `Lobby`: сцены входа в игру нет по решению §4 В2, а `Lobby.tscn` в
  проекте означало бы лобби, которого мы сознательно не делаем.
  В мире стенда сервер спавнит **два** корабля (`Ship#1`, `Ship#2`) и `SpawnPoints`
  с `>= 2` маркерами: с одним кораблём нельзя проверить ни адресацию по
  `ShipNetId`, ни переход игрока между кораблями, ни коллизию имён источников в
  панели (§3.9 п.15–17, §4 В4 п.4).
- `tests/NetTest.tscn` + `tests/NetTest.cs`: поднимает `OfflineMultiplayerPeer`
  (в 4.7 тип `Godot.OfflineMultiplayerPeer` есть) и проверяет логику без UDP:
  авторитет, гейтинг ввода, работу `NetState`. Это однопроцессный тест, он
  проверяет **код**, а не соединение.
- `tests/NetLoopbackTest.tscn`: два `MultiplayerApi` с разными
  `OfflineMultiplayerPeer` и `ObjectConfigurationAdd/Remove` — проверка, что
  RPC-пути и `NodePath` совпадают в двух «процессах».
- `run-net-demo.sh`: запускает **два** процесса — сервер
  (`--headless -- --server`) и клиента (`-- --join 127.0.0.1`), печатает ID пиров.
  Критерий стадии 0 из `prototype-plan.md`: «два экземпляра игры соединяются,
  дебаг-панель показывает роль и список пиров». Число клиентов задаёт **сам
  скрипт** (`./run-net-demo.sh 3` = сервер + 3 клиента), а не флаг Godot: лишние
  процессы нужны, чтобы ловить баги «у меня был только один клиент» (обход
  `GetPeers()`, рассылка `AnyPeer`, интерполяция двух чужих игроков).
- Флаги передаются **только после `--`**: `OS.GetCmdlineUserArgs()` их вернёт, без
  разделителя аргумент уезжает в `GetCmdlineArgs()` и до `NetworkManager` не
  доходит. Проверено на 4.7.2 прогоном `tests/probe_args.gd`:
  `--headless --path . -s res://tests/probe_args.gd -- --server` →
  `USER_ARGS=["--server"]`, а без `--` → `ENGINE_ARGS=[..., "--server"]`,
  `USER_ARGS=[]`. Тот же probe стоит оставить как регрессию на будущее.
- Сервер в скрипте убивается по PID (`kill`), а не «сам когда-нибудь умрёт»:
  dedicated-процесс крутится вечно, и висящий процесс с занятым портом ломает
  следующий прогон (`Error.AlreadyInUse`, см. §2.1).
- Стенд из скрипта и запуск кнопкой `Start Server` из UI (§3.7, §4 В13=A) —
  **один и тот же** режим `Dedicated`, различается только способ попасть. CI
  гоняет скрипт (headless, без окна), игрок жмёт кнопку. Отсюда полезная
  проверка: серверная ветка не имеет права знать, как её запустили, — если в
  коде встречается `if (headless)` или «если сервер запущен из UI», это ошибка.
  Проверить просто: один и тот же `tests/NetTest.tscn` должен проходить и в
  `-- --server --headless`, и в окне после нажатия `Start Server`.

### 2.7 Бюджеты и диагностика

`DebugInfoBus` дополнить источником `"Net"`: роль, `SelfId`, `GetPeers()`,
пакеты/с (отправлено/получено), байты/с, RTT по каждому пиру. API в 4.7:
- **по хосту целиком** (пакеты/байты): `ENetMultiplayerPeer.Host` → `ENetConnection`,
  затем `PopStatistic(ENetConnection.HostStatistic.SentPackets | ReceivedPackets |
  SentData | ReceivedData)` — метод **забирает и обнуляет** счётчики, поэтому читать
  его надо ровно раз в интервал пересчёта, иначе часть пакетов потеряется из вида;
- **по конкретному пиру** (RTT, потери): `ENetMultiplayerPeer.GetPeer(id)` →
  `ENetPacketPeer`, затем `GetStatistic(ENetPacketPeer.PeerStatistic.RoundTripTime |
  LastRoundTripTime | PacketLoss | ...)`. `ENetPeer` в 4.7 называется
  `ENetPacketPeer`, отдельного `PopStatistic(peer, channel, ...)` **нет** —
  в прошлой ревизии этого плана это было записано неверно.

В dedicated-процессе, запущенном из CLI, `DebugLayer` не создаётся (§2.1), поэтому
счётчики на сервере пишутся в stdout раз в секунду той же службой `DebugInfoBus`
(у неё нет зависимости от UI), иначе серверную статистику в headless посмотреть
негде. При запуске сервера кнопкой (§4 В13) панель на месте и счётчики видны в
ней, но stdout-печать остаётся: один и тот же прогон должен одинаково читаться и
в окне, и в CI-логе.

Порог из плана: < 50 пакетов/с
на игрока. Обновлять раз в секунду, не в `_Process`.

После В3 = B в таблице счётчиков появляется ещё одна строка — **репорт корабля
на сервер** (RPC от пилота, §2.3): у пира-пилота она есть, у остальных нет. Считать
её надо отдельно от репликации синхронизатора, иначе «пакеты/с» у пилота окажутся
вдвое выше, чем у соседей, и это не баг, а следствие выбора В3. Отдельно
печатается возраст последнего принятого сервером снимка (`report age ms`) — по
нему видно, что серверная физика корабля под пилотом жива (§3.11 п.14).

### 2.8 Слои физики: закрыть вопрос с `Reserve` / `Debris`

Сейчас: `1 Ship, 2 Station, 3 Character, 4 Target, 5 DockingZone, 6 DebugHelper`.
В `prototype-plan.md` — `World / Ship / Character / Target / Reserve / Debris`.
Решение принять **сейчас**, пока сцен с новыми слоями нет:
- оставить текущую нумерацию (она уже вшита в `CollisionLayers.cs`, `project.godot`
  и `.tscn`), а `Debris` завести на `7`, `World`/`Environment` — не вводить вовсе
  (статика мира — это `Station`-слой или отдельный `8`);
- либо перенумеровать сейчас, пока тестов 89, а не 890.
Первый вариант дешевле: добавить `Debris = 1u << 6` и `World = 1u << 7` в
`CollisionLayers` и `3d_physics/layer_7/8` в `project.godot`, ничего не сдвигая.

### 2.9 Физический тик vs частота репликации

Принять явно: физика сервера — 60 Гц (в `project.godot` сейчас стоит **значение по
умолчанию** 60, `3d/physics_ticks_per_second` явно не прописан — прописать, чтобы
никто не сбил), репликация — 30 Гц (`ReplicationInterval = 1/30`), ввод клиента —
60 Гц (`InputFrame` на каждый физ.шаг, unreliable). Итог: 30 снимков/с +
60 пакетов ввода/с на игрока. Если бюджет < 50 пакетов/с считается по всем
пакетам, а не только по снимкам, то ввод надо слать не каждый шаг, а каждый
второй (`tick % 2 == 0`) — зафиксировать это числом в `NetworkManager`.

После В3 = B появляется четвёртая частота, и её нельзя выводить из первых трёх:
**частота RPC-репорта корабля на сервер** (только от пира-пилота). Она ниже
частоты картинки: серверу нужны темп для валидации и актуальность для серверных
проверок, а не плавность. Ориентир — 15–20 Гц при допуске «возраст снимка» 200 мс
(§2.7, §3.11 п.14). Частоты репорта и `ReplicationInterval` настраиваются
раздельными константами в `NetworkManager`, иначе при попытке «сэкономить трафик»
вместе с картинкой начинает деградировать и серверная физика корабля.

### 2.10 Критерий готовности доработанной стадии 0

1. `./run-net-demo.sh` — dedicated-сервер (headless) + 2 клиента, в логе три
   разных ID: `1` у серверного процесса и два `≥ 2` у клиентов. Серверный лог при
   этом **не** содержит ни одного `Player`-источника `DebugInfoBus` — это и есть
   доказательство, что сервер не играет (см. также §4 В1).
2. Дебаг-панель клиента показывает: `role = client, id = <n>, peers = [...]`.
   У клиента, запустившего сервер, — ровно та же строка, никаких `role = host`.
3. Кнопка `Spawn Target` у клиента создаёт мишень у **всех** (не только у себя).
4. `tests/NetTest.tscn` в headless: 0 ошибок, assert'ы на авторитет и гейтинг.
5. Стадия 1 (`tests/Stage1Test.tscn`) продолжает проходить в режиме `Offline`
   (однопроцессный режим остаётся — это не «сервер в том же окне», а отсутствие
   `MultiplayerPeer`; см. §2.1).
6. Ни в одном логе нет `SetMultiplayerAuthority(1)` для `Player` — авторитет
   игрока всегда `≥ 2` (§2.4). Для `Ship` это верно с точностью до наоборот:
   `SetMultiplayerAuthority(1)` — штатное состояние «кабина свободна» (§4 В3=B),
   поэтому в логах он быть обязан; запрещён только `SetMultiplayerAuthority(1)`
   у `Player`.
7. Корабль под пилотом: у пилота `Ship.Freeze == false`, у сервера и у второго
   клиента — `Freeze == true`, `FreezeMode = Static`; в панели у всех трёх
   процессов `auth` у `Ship`-строки показывает один и тот же `id` пилота
   (значит `AssignShipAuthority` доехал до всех, §2.5 п.5). Выход пилота из
   кабины за ≤ 1 с возвращает `auth = 1` и `Freeze == false` на сервере.
   Обрыв пилота (`PeerDisconnected`) — то же самое без ручных действий.
8. Корабль под пилотом продолжает двигаться у всех: второй клиент видит плавное
    движение (интерполяция `ShipHull`), сервер — движение в своей копии
    (`NetPosition` меняется, `GlobalPosition` замороженного тела — нет).
    `|err|` в панели у не-авторитета не растёт монотонно (иначе репорт не
    догоняет, и В3=B выбран зря).
9. Кнопка `Start Server` в окне (не CLI) переводит процесс в `Dedicated`: в
   панели `role = dedicated, players = 0`, одиночный `Player` удалён, мир
   продолжает рендериться; `Disconnect` возвращает `role = offline` и возвращает
   одиночного игрока (§2.1, §3.7, §4 В13=A).
10. Возврат на место: клиент отсоединился и подключился заново **в том же
   серверном процессе** — игрок появился там, где был (или в той же точке
   крепления), а не на стартовой; второй клиент, зашедший впервые, появился на
   стартовой точке из `SpawnPoints`. Проверка — координаты из `Net`-строки панели
   до отключения и после подключения (или assert в `NetTest`, §3.9 п.8).
11. В мире **два корабля** (§4 В4=B): панель показывает `Ship#1` и `Ship#2` с
   разными `auth`/`|err|`; вход пилота в кабину второго меняет `auth` только у
   `Ship#2`, у первого `auth` остаётся `1`; игрок перешёл из кабины первого в
   кабину второго **одной** кнопкой и не повис непристёгнутым (§3.4 п.3, §3.9
   п.15, п.17). Второй клиент, зашедший **в полёте** первого, через 1 с видит тот
   же `auth` у обоих кораблей, что и первый (гонка спавн ↔ авторитет, §3.11 п.16).
12. Удаление корабля, к которому пристёгнут игрок: игрок не «висит» на удалённом
   узле, `LastSeenState.WasAttached == false`, повторный вход даёт стартовую
   точку, в логе нет `null`-пути (§3.9 п.16, §3.11 п.19).

---

## 3. Изменения имплементации стадии 1: что переписать под сеть

Раздел отвечает на вопрос «какие файлы стадии 1 трогаем и как». Порядок — сверху
вниз: сначала данные и новые узлы, потом сцены, потом тесты. Каждый пункт —
коммит, после которого `./run-tests.sh` остаётся зелёным (в offline-режиме).

### 3.1 `scripts/core/Ship.cs` — роль узла вместо «симулируемся везде»

Сейчас `_Ready()` проставляет слои/массу/затухание, `_EnterTree()` регистрирует
источник отладки и кнопку `Nudge Ship`. В сети этого мало: не-авторитет не имеет
права ни двигать тело, ни слать импульс, а после §4 В3 = B авторитет ещё и
**меняется в рантайме** (заняли кабину / вышли / пилот отвалился).

Правки:
1. В `_Ready()` после слоёв — определить роль:
   ```csharp
   bool authority = IsMultiplayerAuthority();
   ```
   В offline `GetMultiplayerAuthority()` возвращает `1`, а `IsMultiplayerAuthority()`
   — `true` (у offline-peer `GetUniqueId() == 1`), поэтому текущее поведение
   сохраняется целиком: **все 89 assert'ов стадии 1 проходят без изменений.**
   Проверять надо именно `IsMultiplayerAuthority()`, а не
   `GetMultiplayerAuthority() == 1`: при dedicated-модели (§4 В1) значение `1`
   есть и у серверного тела (там «я авторитет» = истина), и у клиента (там то же
   `1` означает «истина на сервере, я только интерполирую»). Разница — в роли,
   не в номере. При В3=B номер тоже не стабилен: тот же корабль у разных пиров
   и в разное время имеет то `1`, то `id ≥ 2`, так что единственная корректная
   проверка — `IsMultiplayerAuthority()`.
2. Роль нельзя читать один раз в `_Ready()`: вынести в метод
   `ApplyAuthority()`, который вызывается из `_Ready()` **и** из обработчика
   смены авторитета (`AssignShipAuthority`-RPC, §4 В3 п.3). Он делает три вещи
   сразу и на всех пирах одинаково:
   ```csharp
   bool authority = IsMultiplayerAuthority();
   Freeze = !authority;
   FreezeMode = FreezeModeEnum.Static;   // не Kinematic: см. §2.5 п.4
   SetProcess/PhysicsProcess — только интерполяция картинки у не-авторитета;
   ```
   Если этого не делать, после смены пилота часть пиров оставляет тело
   замороженным (или, наоборот, интегрирует чужое) и картинка замирает.
3. На не-авторитетном пире — `Freeze = true; FreezeMode = FreezeModeEnum.Static;`
   иначе чужое тело толкает локальные узлы и участвует в контактах, которых нет у
   авторитета. Внимание: при В3=B не-авторитетом может быть **сервер**.
4. Кнопку `Nudge Ship` регистрировать только если `authority` на момент
   `_EnterTree()`: не-авторитет не имеет права вызывать `ApplyImpulse` (§2.5 п.6).
   Вместо неё — `SubmitInput` (§2.3). В offline игрок/тест — авторитет, кнопка
   остаётся на месте, assert `vbox` не падает. При смене авторитета в рантайме
   кнопку нужно перерегистрировать/снять вместе с `ApplyAuthority()` — иначе
   у клиента остаётся кнопка, которая шлёт импульс в никуда.
5. Репорт `NetState` на сервер (`rpc_id(1)`) — только если `authority`; приём
   репорта на сервере — только если `!authority` и `SimTick` новее последнего
   принятого. Это единственное место, где позиция вообще летит по сети «снизу
   вверх», и летит только от того, кто её вычислял (§2.3).
6. `RegisterInfoSource("Ship", ...)` оставить везде, но добавить в строку
   `auth = {GetMultiplayerAuthority()}`, `frozen = {Freeze}` и
   `|err| = |NetPosition - GlobalPosition|` — без этих цифр реконсиляцию и смену
   авторитета отлаживать нечем.
   **Ключ источника меняется на `Ship#{ShipNetId}`** (§4 В4 п.5): когда кораблей
   несколько, два узла с именем `"Ship"` зальют в `DebugInfoBus` два источника с
   одним именем, и в панели останется последний — то есть половина мира станет
   невидимой для отладки ровно в тот момент, когда она станет нужна. В offline
   `ShipNetId` присваивает себе сам единственный корабль (счётчик тот же,
   значение `1`), поэтому строка панели выглядит как `Ship#1` и в одиночном
   режиме — assert стадии 1 на имя источника менять нельзя, он проверяет
   `Ship#1` после правки сцены (§3.8).
7. `CustomIntegrator` (этап 5) включать только авторитетному пиру, и сбрасывать
   при `ApplyAuthority()`.
8. В `_Ready()` — проверка адреса: `ShipNetId != 0`, иначе ошибка в
   `DebugInfoBus` (§3.2). Корабль с нулевым адресом физически живёт в сцене, но
   все RPC (`AssignShipAuthority`, репорт `NetState`, `RequestAttach`) уходят в
   никуда, и выглядит это как «физика сломалась», а не как отсутствие инициализации.
   Проставляет его сервер **до** `AddChild` (§4 В4 п.3), а в offline — сам
   `NetworkManager` при спавне одиночного мира; сам узел себе адрес не выдаёт,
   иначе два пира выдадут разные номера одному кораблю.

### 3.2 Новый файл `scripts/core/NetState.cs` — буфер «истины»

```csharp
public partial class NetState : Node
{
    [Export] public Vector3 NetPosition { get; set; }
    [Export] public Quaternion NetRotation { get; set; }
    [Export] public Vector3 NetLinearVelocity { get; set; }
    [Export] public Vector3 NetAngularVelocity { get; set; }
    [Export] public int SimTick { get; set; }        // физ.шаг снимка у авторитета (пилот или сервер, §4 В3=B)
    [Export] public int ShipNetId { get; set; }      // выдаёт сервер до AddChild (§4 В4 п.3); 0 = не назначен
}
```

Только то, что нужно картинке и предсказанию. Никаких `GlobalPosition`/
`GlobalRotation` самого тела в репликации — с ними `MultiplayerSynchronizer`
конфликтует с локальной физикой (§1.11). Свойства обязаны быть `[Export]`:
`SceneReplicationConfig.AddProperty(NodePath, …)` работает по пути к узлу и имени
свойства, а C#-поле без атрибута Godot свойством не считает, и реплика молча не
придёт (риск §3.11 п.2).

`ShipNetId` — не «картинка», а адрес узла в сети: он нужен, чтобы `AssignShipAuthority`,
репорт `NetState` и `RequestAttach` ссылались на корабль, а не на путь. Раз он уже
в `NetState`, то едет к пиру вместе со спавн-снимком бесплатно (§3.3). Узлы, у
которых его нет (игрок, станция), получают свой адрес так же: `Player` — по
`peerId` авторитета, `Station` — по `StationNetId` из того же счётчика (§3.4 п.2).
Правило «`0` означает не назначен» обязательно: `Ship._Ready()` при `ShipNetId == 0`
печатает ошибку в `DebugInfoBus`, потому что молча живущий корабль с нулевым
адресом — это RPC, которые уходят в никуда (риск §3.11 п.15).

### 3.3 `scenes/Ship.tscn` — `ShipHull`, `NetState`, `MultiplayerSynchronizer`

Сейчас: `Ship(RigidBody3D)` → `Hull (MeshInstance3D)`, `CollisionShape3D`,
`Muzzle (Node3D)`, `AttachPoints/Seat (Node3D + AttachPoint.cs)`. Целевая
иерархия:

```
Ship (RigidBody3D, скрипт Ship.cs)
├── CollisionShape3D            (BoxShape3D, как сейчас)
├── ShipHull (Node3D)           ← НОВЫЙ узел-обёртка
│   └── Hull (MeshInstance3D)   ← существующая картинка переезжает под него
├── Muzzle (Node3D)             ← остаётся под Ship: ствол привязан к телу
├── AttachPoints/Seat           ← см. «открытый вопрос» ниже
├── NetState (Node, скрипт NetState.cs)          ← НОВЫЙ
└── Synchronizer (MultiplayerSynchronizer)       ← НОВЫЙ
        RootPath = ".."                    (путь относительно самого узла)
        ReplicationInterval = 1/30
        ReplicationConfig = SceneReplicationConfig ← свойства NetState/*
        PublicVisibility = true            (шлём всем: картинка нужна всем)
```

Про `RootPath` и `ReplicationConfig` — по факту 4.7: у `MultiplayerSynchronizer`
в C# есть `RootPath` (путь к узлу, чьи свойства реплицируются; по умолчанию —
родитель узла синхронизатора), `ReplicationInterval`, `DeltaInterval`,
`VisibilityUpdateMode` (`Idle`/`Physics`/`None`), `PublicVisibility` и свойство
`ReplicationConfig` типа `SceneReplicationConfig`. Списка «watchers» в API нет:
круг получателей задаётся `PublicVisibility` + `SetVisibilityFor(peer, bool)` +
`AddVisibilityFilter(Callable)`. Конфиг свойств строится так:

```csharp
var cfg = new SceneReplicationConfig();
// путь в AddProperty — относительно RootPath, а не относительно синхронизатора:
// RootPath = Ship, свойства живут на NetState ⇒ путь "NetState/<Prop>".
cfg.AddProperty(new NodePath("NetState/NetPosition"));
cfg.PropertySetReplicationMode(new NodePath("NetState/NetPosition"),
    SceneReplicationConfig.ReplicationMode.OnChange);
cfg.PropertySetSpawn(new NodePath("NetState/NetPosition"), true);  // снимок при появлении узла
// то же для NetState/NetRotation, NetState/NetLinearVelocity,
// NetState/NetAngularVelocity, NetState/SimTick, NetState/ShipNetId
synchronizer.ReplicationConfig = cfg;
```

Путь `NetState/NetPosition`, а не `NetPosition`: `AddProperty(NodePath, int)`
задаёт путь **от узла `RootPath`** (в `GodotSharp.xml`: «The node from which the
properties will be synchronized» — это про `RootPath`, а конфиг к нему
привязывается). Если написать `NetPosition`, свойство на `Ship` с таким именем
отсутствует, и реплика **молча не приходит** — тот же класс тихой ошибки, что и
отсутствующий `[Export]` (§3.11 п.2). Дешёвая страховка: assert в `NetTest`, что
после изменения `NetState.NetPosition` на авторитете у не-авторитета значение
меняется (п.14).

`SimTick` — `OnChange` + `PropertySetSync(false)` (он меняется каждый кадр,
дельта и так есть). `AddProperty(NodePath, int index)` — второй аргумент это
индекс элемента для массивов, не флаги; для скалярных свойств его не передают.

Следствие §4 В4 = B (корабль спавнится, а не лежит в сцене):

- **`PropertySetSpawn(true)` обязателен для `NetPosition`/`NetRotation`/`ShipNetId`.**
  Узел появляется в рантайме, и пир, зашедший позже, обязан получить снимок вместе
  с узлом. В статичном варианте A этого механизма нет и пришлось бы писать
  собственное сообщение «начальное состояние корабля» — в B оно не нужно.
  Проверка: пир, подключившийся после старта мира, в первом же кадре имеет
  `NetPosition != Vector3.Zero` у уже летящего корабля (§3.9 п.14).
- **`ShipNetId` живёт в `NetState`** (`[Export] public int ShipNetId { get; set; }`,
  §3.2) и реплицируется со `PropertySetSpawn(true)`, `Sync = false` (значение не
  меняется никогда). Сервер заполняет его **до** `AddChild`, иначе пир получит
  узел с `ShipNetId == 0` и не сможет найти корабль в реестре (§4 В4 п.3).
- **`PublicVisibility = true` и никаких фильтров видимости** — см. §3.6 «Порядок
  спавна и запрет на видимость для кораблей»: для спавнера снятая видимость =
  удаление узла у пира, а корабль удаляет только сервер.

**Ключевое следствие В3=B, которое надо проверить числом, а не верой:**
направление репликации у синхронизатора привязано к авторитету узла, а не к
серверу. Формулировка доки (4.4+, в 4.7 та же): «Synchronizes properties from the
multiplayer authority to the remote peers» и «By default, MultiplayerSynchronizer
synchronizes configured properties to all peers». То есть получатель — любой
удалённый пир, сервер в их числе: когда авторитет `Ship` = пир-пилот, его
синхронизатор рассылает `NetState` всем остальным — включая сервер. Это ровно то,
что нужно (сервер получает истину от пилота), но поведение надо зафиксировать
assert'ом в `tests/NetTest.tscn` (§3.9 п.11), потому что при В3=B у сервера
собственный синхронизатор оказывается выключенным из передачи, и если в 4.7 есть
нюанс (например, серверский синхронизатор продолжает слать старое значение), это
вылезет как «корабль дёргается между двумя источниками». Подстраховка, которая
снимает зависимость от этого поведения, — явный RPC-репорт `NetState` от
авторитета на сервер (§2.3, §3.1 п.5): сервер принимает позицию по нему, а не
по синхронизатору. Синхронизатор тогда нужен только для картинки клиентам.

`ShipHull` — обёртка, а не переименование: `Hull` остаётся `MeshInstance3D`,
чтобы ссылки на `Ship/Hull` не исчезли; assert теста обновляется на
`Ship/ShipHull/Hull` (§3.8).

Зачем нужен отдельный узел: интерполировать на клиенте надо **картинку**, а не
`RigidBody3D.Transform` — прямая запись в трансформ тела ломает интегрирование и
столкновения. `ShipHull` — обычный `Node3D`, его `Transform` можно писать сколько
угодно. Это же закрывает §1.6.

Открытый вопрос, решить до правки: `AttachPoint` остаётся под `Ship` (игрок в
кабине получает точный мировой трансформ тела, но «дёргается» относительно
картинки) или переезжает под `ShipHull` (игрок всегда совпадает с картинкой, но
его физика отстаёт от корпуса на величину интерполяции). Для этапа 6
(`MoveInAttachedPlane`, стыковка) важнее второе: **точки переносим под `ShipHull`**,
коллизию корпуса держим на теле, `Muzzle` — на теле.

### 3.4 `scripts/core/AttachPoint.cs` — стабильный `NetId` вместо `NodePath`

`Player.AttachTo` сейчас принимает сам узел. Через сеть узел не передать: путь
`Ship/AttachPoints/Seat` у сервера и клиента совпадает только пока никто не делал
`Reparent`. Поэтому:

1. `[Export] public int NetId { get; set; }` — уникальный в пределах владельца
   номер точки (у `Seat` в `Ship.tscn` — `0`).
2. Реестр `(ownerNetId, NetId) → AttachPoint`: регистрация в `_Ready()`, снятие в
   `_ExitTree()`. Хранить на `NetworkManager` (он autoload и живёт дольше сцены).
   Реестр обязан выдерживать **N владельцев** (§4 В4 п.4): корабли появляются и
   исчезают в рантайме, их несколько, плюс станция. Поэтому:
   - `ownerNetId` — не имя узла и не путь, а серверный `ShipNetId` /
     `StationNetId` из **одного монотонного счётчика** на все спавнящиеся объекты
     мира. Одна схема нумерации обязательна: иначе `ownerNetId = 1` окажется
     одновременно у первого корабля и у станции, и игрок уедет «в кабину» станции;
   - ключ реестра — кортеж, при коллизии `(ownerNetId, NetId)` уже занятого —
     громкая ошибка в `DebugInfoBus`, а не перезапись записи: перезапись означает,
     что игрок пристегнётся к чужому кораблю;
   - при удалении корабля (`QueueFree`) `_ExitTree()` снимает все его точки, и
     сервер **до** удаления обязан переписать `LastSeenState` пристёгнутых к нему
     игроков (§2.4 «Объекты мира спавнятся раньше игроков»), иначе возврат ведёт в
     несуществующую точку;
   - точка станции — это обычный `AttachPoint` в `Station.tscn`, отдельного
     механизма для станции нет. Разница только в авторитете: у станции он всегда
     `1` (§2.5 п.5), поэтому «станция как пилотируемый объект» не появляется.
3. RPC-форма крепления: `RequestAttach(int ownerNetId, int pointNetId)`
   (клиент → сервер, reliable) и `ApplyAttach(int ownerNetId, int pointNetId)`
   (сервер → все, reliable). Сервер находит узлы по реестру и вызывает локальные
   `AttachTo` / `Detach`. После `AttachTo`/`Detach` на сервере — смена авторитета
   корабля (`AssignShipAuthority`, §2.5 п.5, §3.5 п.8): `ApplyAttach` без этого
   шага даёт игрока в кабине без пилота, то есть корабль, который никто не
   интегрирует, и `Freeze == true` у всех.
   Переход «из корабля в корабль» — это **один** `RequestAttach` к другой точке, а
   не `Detach` + `Attach` двумя сообщениями: между ними игрок повиснет
   непристёгнутым, и авторитет обоих кораблей станет `1` на неопределённое время.
   Сервер в одном обработчике делает `Detach()` и `AttachTo()` локально, затем
   `AssignShipAuthority(старый, 1)` и `AssignShipAuthority(новый, pilotPeerId)` —
   в этом порядке, чтобы не было момента, когда у двух кораблей один пилот.
   Переход на станцию отличается только вторым вызовом: у станции авторитет
   не меняется (всегда `1`), меняется только крепление игрока.
4. Сигнатуры `AttachTo(AttachPoint)` / `Detach()` **не менять**: это единственное
   место, где происходит `Reparent`. RPC лишь вызывает их на каждом процессе.
   Иначе `Reparent` появится в трёх местах и разъедет дерево. При спавне через
   спавнер родителем игрока становится `GameRoot`, поэтому `_worldRoot` берут
   явно, а не из `GetParent()` — см. §3.5 п.1.

### 3.5 `scripts/core/Player.cs` — ввод, авторитет, камера

1. `_worldRoot = GetParent() as Node3D` в `_Ready()` — при спавне через
   `MultiplayerSpawner` родителем станет `GameRoot`, это работает. Но если игрок
   спавнится уже внутри `ShipHull` (вариант §3.3), `GetParent()` укажет не на
   сцену. Решение: `_worldRoot` брать явно (`GameRoot` / `GetTree().CurrentScene`),
   а не из `GetParent()`.
2. Камера и ввод — только локально-авторитетному игроку:
   ```csharp
   bool local = IsMultiplayerAuthority();   // == (SelfId == GetMultiplayerAuthority())
   ```
   В offline `SelfId == 1` и авторитет `1` → `local == true`, assert
   `cam.Current` не падает. У чужих игроков принудительно `Camera.Current = false`:
   в `Player.tscn` стоит `current = true`, и вторая камера перехватит viewport.
   В dedicated-процессе своих игроков нет вовсе, поэтому `local == false` у всех
   спавненных `Player` — камера на сервере не нужна и не создаётся.
3. Дебаг-кнопки `Detach Player` / `Attach Player to Seat` — только если `local`;
   вместо прямых `AttachTo`/`Detach` — `RequestAttach`/`RequestDetach` (§3.4).
   При нескольких кораблях (§4 В4 = B) вторая кнопка теряет смысл «пристегнуть к
   Seat первого»: она крепит к **ближайшей точке из реестра** (`NetworkManager`
   перебирает `(ownerNetId, NetId)` и берёт минимум по расстоянию до игрока), а
   в `DebugInfoBus` печатает, куда именно: `attach → ship#2/point#0`. Иначе с
   двумя кораблями кнопка всегда возит игрока в один и тот же корабль, и
   «переход между кораблями» вручную не проверить.
4. `_PhysicsProcess`: автопристёгивание к `DefaultAttachPoint` — только на сервере
   и в offline (`MultiplayerApi.IsServer || !MultiplayerApi.HasMultiplayerPeer()`).
   На клиенте крепление приходит репликой, и `_awaitingDefaultPoint` не должен
   заставлять клиента крепиться самому.
5. `MoveInAttachedPlane` — публичный метод без изменений сигнатуры; вызывает его
   сервер из принятого `InputFrame`, клиент — только для предсказания на своём
   игроке.
6. `AttachTo`/`Detach` меняют дерево (`Reparent`) → это единственный момент, где
   путь узла расходится между процессами. Поэтому `Reparent` выполняется на
   сервере и повторяется каждым клиентом по RPC «attach to point #N» (§3.4), а не
   по `NodePath`.
7. В dedicated-процессе узла `Player` нет вообще (§2.4), поэтому `Player` не
   должен ничего предполагать о «локальном игроке» статически: всё, что нужно
   другим узлам (`Ship` для кабины, `TestStand` для теста), ищется через реестр
   `peerId → Player` у `NetworkManager`, а не через жёсткий `NodePath("Player")`.
   Иначе серверная ветка кода будет падать на `null` ровно там, где она раньше
   не выполнялась (listen-server маскировал это, §4 В1).
8. Кабина и авторитет корабля (§2.5 п.5, §4 В3=B): `AttachTo`/`Detach` сами по
   себе авторитет не меняют и менять не должны. Порядок такой:
   - сервер, выполнив `Reparent` игрока под `Seat` точки крепления, вызывает
     `AssignShipAuthority(shipNetId, player.GetMultiplayerAuthority())`;
   - при `Detach` — если этот игрок был пилотом данного корабля, там же
     `AssignShipAuthority(shipNetId, 1)`;
   - при `PeerDisconnected` — сначала `LastSeenState` (включая `AttachedTo`,
     §2.4/§4 В2), затем снятие авторитета с корабля, если пилот был этот пир.
     Иначе корабль остаётся с авторитетом отключённого пира, `Freeze == true`
     становится у всех, и мир замирает для всех, кроме серверных тел.
   Проверка «кто был пилотом» — не обход детей `AttachPoint`, а поле
   `Ship.PilotPeerId`, которое ведёт сервер при тех же вызовах: обход дерева в
   момент `PeerDisconnected` уже может не содержать узел игрока.

### 3.6 `scenes/Main.tscn` — состав сцены меняется

Целевая структура:

```
Main (Node3D)
├── Environment (instance)
├── TestStand (instance)      ← станция-платформа: статична, одинакова у всех
├── SpawnPoints (Node3D)      ← НОВЫЙ: маркеры стартовых точек (данные сцены)
│   ├── Spawn0 (Marker3D)
│   └── Spawn1 (Marker3D)
├── GameRoot (Node3D)         ← НОВЫЙ: сюда спавнится всё рантаймовое
│   └── Spawner (MultiplayerSpawner, SpawnPath = "..")
│                                ← спавнит Player, Ship, позже Station
└── DebugLayer (instance)
```

После старта мира и подключения двух пиров содержимое `GameRoot` у всех одинаково
по составу (пути при этом не являются адресами — адрес это `ShipNetId`/пара
`(ownerNetId, NetId)`):

```
GameRoot
├── Ship   (RigidBody3D, ShipNetId = 1)
├── Ship2  (RigidBody3D, ShipNetId = 2)   ← со второго корабля начинается «есть
│                                            из чего выбирать»
└── Player (CharacterBody3D, авторитет = свой пир)
```

`Player` и `Ship` из `Main.tscn` **убираются**: их создаёт сервер (при подключении
пира — игрока, при старте мира — корабль) и повторяет `MultiplayerSpawner` через
`AddSpawnableScene("res://scenes/Player.tscn")` /
`AddSpawnableScene("res://scenes/Ship.tscn")`. Иначе у каждого процесса свой
корабль со своим `InstanceId`, и `Synchronizer` не знает, куда класть реплику.

В offline (тесты стадии 1) игрок и корабль создаются тем же путём — через
`Spawner`, а не лежат в сцене: offline и сеть не должны разъезжаться (§1.8). Это
ломает assert'ы `Stage1Test` на `Main/Player` и `Main/Ship` → §3.8.

`TestStand` остаётся статичным узлом `Main`: он не спавнится в рантайме и
одинаков у всех, репликация ему не нужна. Станция как игровой объект
(`Station.tscn`, этап 6) — это **другой** узел, и он спавнится этим же спавнером;
`TestStand` — только стенд для тестов стадии 1, путать их не нужно (§4 В4 п.4).

#### Порядок спавна и запрет на видимость для кораблей (§4 В4 = B)

Сервер спавнит в таком порядке: **сначала объекты мира (`Ship`, `Station`), потом
игроков** — иначе `LastSeenState.WasAttached` ссылается на точку ещё не
заспавненного корабля (§2.4 «Объекты мира спавнятся раньше игроков»).

Один `Spawner` покрывает все спавнящиеся сцены: `AddSpawnableScene` вызывается по
одной раз на сцену (`Player.tscn`, `Ship.tscn`, позже `Station.tscn`), а не на
каждый экземпляр. Имена экземпляров задаёт спавнер (`Ship`, `Ship2`, …), поэтому
путь **не является** идентификатором корабля — адресом служит серверный
`ShipNetId` (§4 В4 п.3), все RPC про корабль идут по нему.

`Ship`-синхронизатор: `PublicVisibility = true` навсегда. `SetVisibilityFor` /
`AddVisibilityFilter` к нему применять **запрещено**: спавнер обрабатывает узлы
согласно видимости синхронизаторов, и снятая видимость для него означает «у этого
пира узла нет», то есть удаление корабля у пира. Корабль — не удаляемый узел, его
удаляет только сервер. Для чужих `Player`-синхронизаторов видимость включать
можно — там удаление узла у невидящего пира допустимо (§2.5 п.2). Проверка —
assert §3.9 п.13.

`SpawnPoints` — тоже статичный узел, репликации нет (позиции точек одинаковы во
всех процессах, серверу достаточно знать их порядок). Нужен он для правила спавна
из §4 В2: игрок без сохранённого состояния появляется на **первой свободной**
точке из этого списка, а не в `Vector3.Zero`. Порядок обхода — по имени
(`Spawn0`, `Spawn1`, …), чтобы он не зависел от порядка обхода дерева. Проверка
«свободна» — `PhysicsDirectSpaceState3D.IntersectShape(PhysicsShapeQueryParameters3D,
int)` (имя параметра-типа — `PhysicsShapeQueryParameters3D`, сверено с
`GodotSharp.xml`) сферой радиуса
`SpawnClearance` (константа `NetworkManager`, не магическое число в `Player`).
Если список пуст (узел удалён или пуст) — спавн в `GameRoot` с громкой ошибкой в
панель и в `DebugInfoBus`, см. §3.9 п.12: это единственный разрешённый случай
нулевой позиции.
Сервер читает эти точки при спавне, клиент — нет: на dedicated-процессе узел
`SpawnPoints` обязан существовать, потому что именно сервер выбирает позицию
(§2.4).

### 3.7 `scripts/debug/DebugLayer.cs` + `scenes/DebugLayer.tscn` — кнопки сети

Механику UI менять не нужно: кнопки уже идут через очередь
`DebugInfoBus.RegisterDebugButton`. Добавить три: `Start Server` →
`NetworkManager.StartServer()`, `Join` → `NetworkManager.Join("127.0.0.1")`,
`Disconnect` → `NetworkManager.Disconnect()`. Кнопки `Host` в панели **нет**:
listen-server в проекте нет (§4 В1), и слово «host» в этом проекте означает
именно его. Плюс источник
`RegisterInfoSource("Net", ...)` с ролью, `SelfId`, `GetPeers()`, RTT и
пакетами/с (§2.7); там же — `playerKey` коротко (первые 8 символов) и строка
`spawn = last-seen | attach:<owner>/<point> | start:<SpawnN>`, чтобы выбор точки
спавна был виден в момент входа, а не выводился по косвенным признакам (§2.4,
§4 В2).

Кнопка `Start Server` — это решение В13=A (§4): dedicated-сервер поднимается
**из интерфейса игры**, в этом же процессе. Никакого порождения дочернего
процесса (`OS.CreateProcess`) на стадии 0 нет — это отложенный вариант B того же
вопроса.

Что при этом происходит с окном, надо прописать в UI явно, потому что это
выглядит как поломка, если не подписать:

- процесс становится сервером: `IsServer == true`, `SelfId == 1`, `Player` не
  создаётся ни сейчас, ни позже;
- `DebugLayer` остаётся, мир рендерится дальше — окно работает как «сервер под
  наблюдением», и это главный плюс запуска кнопкой: серверную строку `role =
  dedicated, players = 0`, список пиров и пакеты/с видно прямо в панели, без
  чтения логов headless-процесса (§2.7);
- в этой же панели строка состояния обязана показывать `players = <N>`:
  «сервер поднят, игроков 0» — нормальное состояние, а не ошибка;
- `Disconnect` возвращает в `Offline`, и в одиночный режим можно вернуться, не
  закрывая игру (§2.1, assert §3.9 п.5);
- `Join` в этом же окне к этому же порту не нажимается: в одном процессе один
  `MultiplayerPeer`. Играть идёт второй экземпляр игры
  (`godot --path . -- --join 127.0.0.1` или `run-net-demo.sh`, §2.6). Именно эта
  невозможность «сыграть с собой в одном окне» и есть принятая цена В1
  (пункт «цена» в §4 В1); В13=A делает её видимой в UI, а не только в скрипте.
- лобби нет (§4 В2=B): после `Join` мир уже отрендерен, и у клиента есть
  переходное состояние «подключён, но ещё не игрок» — от `ConnectedToServer` до
  спавна своей реплики. В панели оно обязано быть подписано явно (`role = client,
  players = 0` при том, что мир виден), иначе оно выглядит как «зацепинг без
  игрока». Отдельного экрана входа в проекте нет, и `Net`-строка — единственное
  место, где это состояние видно.

При CLI-запуске (`-- --server --headless`) `DebugLayer` не инстанцируется вовсе,
а серверная строка уходит в stdout (§2.7) — тот же текст, что в панели, только
без панели.

### 3.8 `tests/Stage1Test.cs` — какие assert'ы придётся поменять

Тест проверяет структуру сцен, поэтому правки сцен ломают его намеренно.
Менять assert под новую структуру можно **только в том же коммите**, что и сцена.

| assert | сейчас | станет |
|---|---|---|
| `Player присутствует в Main` | `Main/Player` | `GameRoot/Player` (после спавна) |
| `SceneFilePath == res://scenes/Player.tscn` | ок | без изменений |
| `камера игрока — активная (Current)` | `cam.Current` | без изменений (в offline игрок локальный) |
| `Ship (RigidBody3D) присутствует в Main` | `Main/Ship` | `GameRoot/Ship` (после спавна, §4 В4=B) |
| `Ship/Hull` — `MeshInstance3D` | под телом | `GameRoot/Ship/ShipHull/Hull` |
| `Ship/AttachPoints/Seat` | под телом | под `ShipHull` (если принято §3.3), путь от корабля — `AttachPoints/Seat` |
| `DebugLayer присутствует в Main` | ок | без изменений |
| `vbox.GetChildCount() == 4` | 4 кнопки | `>= 4` (добавятся Start Server / Join / Disconnect) |
| `LayerName(1..4)` | Ship/Station/Character/Target | без изменений |
| — (нового assert'а нет) | — | новый: `Main/SpawnPoints` существует и содержит `>= 1` дочерний `Marker3D` с именем вида `Spawn<N>` (§3.6, §4 В2) |
| — (нового assert'а нет) | — | новый: у заспавненного корабля `NetState.ShipNetId != 0` (в offline — `1`), и в панели источник называется `Ship#1`, а не `Ship` (§3.1 п.6, §3.2) |
| — (нового assert'а нет) | — | новый: `Main/Ship` **отсутствует** — корабля в сцене больше нет, он спавнится (§4 В4=B). Assert нужен, чтобы «забыли убрать инстанс из `Main.tscn`» не выглядело как «корабль на месте» |

Поиск по дереву в тесте менять на `GetNodeOrNull("GameRoot/Ship")` нельзя «вслепую»:
в offline корабль появляется только после `StartSinglePlayerWorld()`, то есть после
`_Ready` сцены. Тест обязан дождаться факта спавна (сигнал `NodeTree.NodeAdded` на
`GameRoot` или один кадр `await`), иначе assert станет красным не из-за поломки, а
из-за порядка инициализации — и это самый вероятный ложноотрицательный прогон после
этого коммита.

Плюс новые assert'ы из §3.9.

### 3.9 Новый тест `tests/NetTest.tscn` + `tests/NetTest.cs`

Запускается headless **без** UDP (offline-peer) и проверяет не ENet, а поведение
кода при разных ролях:

1. В offline: `GetUniqueId() == 1`, `GetMultiplayerAuthority() == 1`,
   `IsMultiplayerAuthority() == true` → `Ship.Freeze == false`.
2. Сымитировать чужой узел: `SetMultiplayerAuthority(2)` на `Ship` **до**
   `AddChild` (то есть авторитет известен на момент `_EnterTree`) →
   `Freeze == true`, кнопка `Nudge Ship` не зарегистрирована, `ShipHull` не
   наследует трансформ тела напрямую. Смена авторитета **после** `AddChild` —
   отдельная проверка п.9, потому что там нужен `ApplyAuthority()`, а не только
   чтение роли в `_Ready()`.
3. `AttachPoint.NetId` уникален среди точек корабля; `RequestAttach` от
   не-сервера отклоняется, от сервера → `Reparent` и `AttachedTo != null`.
4. `Reparent` переживает `MultiplayerSynchronizer`: после attach/detach реплика
   продолжает приходить (путь `RootPath` не сломался).
5. `NetworkManager.StartServer()` при уже активном peer → `Error.AlreadyInUse`
   (`CreateServer` не вызывается повторно без `Close()`); `StartServer()` после
   `Disconnect()` → `Error.Ok`. Отдельный assert: `StartServer()` не создаёт
   ни одного `Player` (в dedicated-процессе их нет, §2.4), а если `Player` уже
   был создан в режиме `Offline`, он **удаляется** при переходе в `Dedicated`, и
   возвращается при `Disconnect()` (§2.1, §3.11 п.10).
6. `InputFrame` упакован/распакован без потерь (битовая маска + `Vector2` + tick).
7. CLI-разбор: `-- --server` даёт роль `Dedicated`, `-- --join 127.0.0.1` —
   `Client`, а `--server` **без** разделителя `--` не даёт ничего (аргумент
   уходит в `GetCmdlineArgs()`, проверено прогоном `tests/probe_args.gd`, §2.6).
   Assert на это нужен, потому что иначе стенд «молча не сервер» выглядит как
   поломка ENet.
8. Возврат на место спавна (§2.4, §4 В2=B) — три подпроверки на offline-peer,
   без UDP:
   - `RequestSpawn("k1")` → игрок на стартовой точке: `GlobalPosition` совпадает
     с первым свободным `Main/SpawnPoints/SpawnN`, а **не** с `Vector3.Zero`
     (гарантия, что стартовая позиция — данные сцены, а не хардкод);
   - сдвинуть игрока, вызвать серверный путь обработки `PeerDisconnected` для
     этого слота, затем `RequestSpawn("k1")` снова → игрок в сохранённой позиции
     (с точностью до `0.01`) и с `LinearVelocity == Vector3.Zero` (скорость не
     восстанавливается, §4 В2 п.7);
   - `RequestSpawn("k2")` → другой игрок, стартовая точка **отличается** от
     занятой первой (перебор свободных работает, два игрока не спавнятся в одной
     точке).
   Отдельно: `RequestSpawn("k1")` дважды от одного пира даёт **одного** игрока
   (идемпотентность), а `RequestSpawn("k1")` от другого пира при живом слоте с
   тем же ключом → `SpawnDenied.reason = "already-in-game"` (или выгнанный
   старый слот, если выбран этот вариант — но тогда assert на то, что
   `LastSeenState` записан до `DisconnectPeer(force: true)`, §2.4).
9. Смена авторитета `Ship` (В3=B) — четыре подпроверки на offline-peer:
   - `SetMultiplayerAuthority(2)` на `Ship` → `Freeze == true` **сразу**, без
     пересоздания узла: проверяется, что `ApplyAuthority()` привязан к смене
     авторитета, а не только к `_Ready()` (§3.1 п.2);
   - `SetMultiplayerAuthority(1)` обратно → `Freeze == false`, кнопка
     `Nudge Ship` снова зарегистрирована, `vbox`-assert стадии 1 не падает;
   - `FreezeMode == FreezeModeEnum.Static` в обоих замороженных состояниях
     (не `Kinematic`, §2.5 п.4);
   - `AssignShipAuthority(shipNetId, peerId)` от не-сервера отклоняется, от
     сервера меняет `GetMultiplayerAuthority()` у `Ship` **и** у его детей
     (`recursive: true`; у детей по умолчанию авторитет не наследуется,
     §2.5 п.5) — иначе `NetState` у детей остаётся с авторитетом `1` и
     синхронизатор шлёт данные из другого места.
10. Гейтинг силы: `ApplyImpulse`/`ApplyTorque` на не-авторитете не изменяют
    `LinearVelocity` (метод должен выходить до вызова физики), на авторитете —
    изменяет. В offline авторитет = `1`, поэтому существующий assert стадии 1
    (`Nudge Ship` меняет скорость) остаётся зелёным без правок (§3.1 п.4).
11. Репорт `NetState`: на не-авторитете `rpc_id(1)` не отправляется (счётчик
    отправленных репортов = 0), на авторитете — отправляется и `SimTick` в нём
    монотонен; приём репорта со старым `SimTick` игнорируется. Это страховка от
    «двух источников истины» при смене пилота (§2.3, §3.3).
12. `Main/SpawnPoints` пуст → сервер не падает, а спавнит в `GameRoot` и печатает
   ошибку в `DebugInfoBus` + строку в панели (`spawn = start:NONE`): отсутствие
   точек — ошибка конфигурации сцены, но не причина класть `null`-путь в
   серверную ветку (§3.11 п.9). Это **единственный** разрешённый случай позиции
   «в нуле»: запрет хардкода из §3.6 относится к штатному пути, а этот путь
   обязан быть громким, чтобы нулевая позиция в тесте означала поломку сцены, а
   не нормальный спавн.
13. Запрет видимости для кораблей (§4 В4 п.1, §3.6): у `Ship`-синхронизатора
   `PublicVisibility == true`, и `SetVisibilityFor` / `AddVisibilityFilter` к нему
   не применяются. Проверка на offline-peer: после `SetVisibilityFor(2, false)`
   (если код всё-таки это разрешает) узел корабля **не** помечен на удаление и
   `IsNodeInGroup`-подобного «despawn» не происходит. Формулировка assert'а —
   «у `Ship`-синхронизатора нет ни одного visibility-фильтра и `PublicVisibility
   == true`», потому что само удаление у не-сервера в offline не смоделировать.
   Для `Player`-синхронизатора видимость включать разрешено — отдельного assert'а
   на «разрешено» нет, но и общего запрета на все синхронизаторы быть не должно.
14. Спавн-снимок корабля (§4 В4 п.6, §3.3): `PropertySetSpawn(true)` для
   `NetState/NetPosition` работает. Проверка в offline: сервер меняет
   `NetState.NetPosition` на корабле, затем создаётся **второй** пир (offline-peer
   c `GetUniqueId() != 1` в том же процессе — так же, как в п.2), и его копия
   корабля в первом же кадре имеет `NetPosition` равный изменённому, а не
   `Vector3.Zero`. Отдельный assert на путь: `ReplicationConfig` содержит ровно
   `NetState/NetPosition` (не `NetPosition`) — иначе реплика молча не приходит и
   этот же assert «не ноль» падает без объяснения причины (§3.11 п.2).
15. N кораблей в мире (§4 В4 п.3–5): сервер спавнит два корабля → у них разные
   `ShipNetId` (`1` и `2`), разные пути (`GameRoot/Ship`, `GameRoot/Ship2`), в
   `DebugInfoBus` **два** источника (`Ship#1`, `Ship#2`), и `AssignShipAuthority(2,
   peerId)` меняет авторитет второго, не трогая первый. Тот же assert на
   `RequestAttach`: пара `(ownerNetId = 2, pointNetId = 0)` крепит именно ко
   второму (`AttachedTo` принадлежит кораблю с `ShipNetId == 2`). Коллизия ключа
   реестра `(ownerNetId, NetId)` → громкая ошибка, а не перезапись (§3.4 п.2).
16. Удаление корабля с пристёгнутым игроком (§2.4 «Объекты мира спавнятся раньше
   игроков», §3.4 п.2): сервер удаляет корабль, к которому пристёгнут игрок →
   `LastSeenState.WasAttached == false` у этого игрока **до** `QueueFree`, точка
   крепления исчезла из реестра, игрок не «висящий на несуществующем узле», и
   повторный `RequestSpawn` с тем же ключом даёт стартовую точку, а не падение.
   Порядок «сначала корабли, потом игроки» при старте мира проверяется здесь же:
   `RequestSpawn` с `WasAttached == true` до спавна корабля не должен кидать
   исключение.
17. Переход между кораблями (§3.4 п.3): один `RequestAttach` из точки корабля A в
   точку корабля B → `Detach` и `AttachTo` выполнены, `AssignShipAuthority(A, 1)`
   и `AssignShipAuthority(B, pilot)` выполнены, в **ни одном** промежуточном
   состоянии у обоих кораблей авторитет не равен `pilot` одновременно. Проверка —
   счётчик «сколько кораблей имеют `PilotPeerId == pilot`» ≤ 1 всегда.

Реальный сетевой прогон — отдельным скриптом, потому что в одном процессе
`ENetMultiplayerPeer` сам к себе не подключается (§3.10 шаг 6), а dedicated
требует минимум двух процессов (§4 В1).

### 3.10 Порядок правок (каждый шаг — рабочий коммит с зелёными тестами)

1. `NetworkManager` + `NetRole` + CLI `-- --server` / `-- --join` + `project.godot`
   (`[autoload]` перед `DebugInfoBus`, явный `3d/physics_ticks_per_second=60`).
   Сцен не касаемся → стадия 1 проходит целиком.
2. `NetState.cs` + `ShipHull` в `Ship.tscn` + `Synchronizer` + ветка по авторитету
   в `Ship.cs` + assert `Ship/ShipHull/Hull`. Тест зелёный.
3. `AttachPoint.NetId` + реестр + `RequestAttach`/`ApplyAttach`. Тест зелёный.
3b. Смена авторитета `Ship` (В3=B): `Ship.ApplyAuthority()` + `Ship.PilotPeerId` +
   `NetworkManager.AssignShipAuthority(shipNetId, peerId)` + вызовы из
   `ApplyAttach`/`ApplyDetach`/`PeerDisconnected` (§3.5 п.8) + RPC-репорт
   `NetState` от авторитета на сервер + гейтинг `ApplyImpulse` (§2.5 п.6).
   Offline-ветка при этом не меняется: авторитет `1`, `IsMultiplayerAuthority()`
   = `true`, кнопка `Nudge Ship` на месте → 89 assert'ов остаются зелёными без
   правок. Проверяется assert'ами §3.9 п.9–11 **до** шага 4: если смена
   авторитета не работает на статичном узле, переход в `GameRoot`/спавнер
   (шаг 4) только замаскирует проблему, а не решит её.
4. `GameRoot` + `MultiplayerSpawner` + перенос `Player`/`Ship` из `Main.tscn` +
   assert'ы §3.8. Сервер спавнит **сначала корабли, потом игроков** (§3.6),
   `ShipNetId` выдаётся до `AddChild` (§3.2), `AddSpawnableScene` вызывается один
   раз на сцену. Тест зелёный.
4b. Второй корабль в мире (§4 В4): `Ship#2` + `Ship#{NetId}` в `DebugInfoBus` +
   реестр на N владельцев + очередь отложенных `AssignShipAuthority` (§4 В4 п.2)
   + assert'ы §3.9 п.13–17. Этот шаг обязателен именно как отдельный коммит:
   на шаге 4 всё ещё один корабль, и «адрес = путь» случайно работает; второй
   корабль вскрывает и путь, и коллизию имён источников, и гонку спавн↔авторитет
   сразу в трёх местах, чинить которые потом по одному дороже.
5. `DebugLayer`: кнопки сети (`Start Server` / `Join` / `Disconnect`, без `Host`)
   + `Net`-строка + счётчики пакетов. Здесь же — переход `Offline → Dedicated`
   (удаление одиночного `Player`) и `Disconnect → Offline` (возврат), §2.1.
6. `Main.tscn`: `SpawnPoints` (маркеры) + выбор свободной точки при спавне.
   Затем `playerKey` (`user://identity.cfg`) + `RequestSpawn(playerKey)` +
   таблицы `peerId → Player` и `playerKey → LastSeenState` + запись позиции в
   `PeerDisconnected` до удаления узлов, §2.4. `GameRoot` и `MultiplayerSpawner`
   к этому моменту уже есть из шага 4. Тест зелёный (assert'ы §3.9 п.8).
7. `tests/NetTest.tscn` + `run-net-demo.sh` (headless-сервер + 2 клиента). Тот же
   `NetTest` прогоняется и вторым способом: в окне после кнопки `Start Server`
   (§2.6) — это проверка, что серверная ветка не зависит от способа запуска.
8. Реконсиляция и интерполяция `ShipHull` (ring buffer снимков, откат ввода) —
   последний шаг: он правит `Ship.cs` и требует готовых шагов 2–4.

### 3.11 Риски, которые проверяются до начала правок

1. **`Reparent` + `MultiplayerSynchronizer`.** `RootPath` задан относительно
   узла, но абсолютный путь узла при `Reparent` меняется; спавнер и синхронизатор
   обязаны это пережить. Проверяется отдельным тестом (§3.9 п.4) до того, как
   ляжет в игровой код.
2. **`SceneReplicationConfig` и C#-свойства.** Конфиг работает по `NodePath` +
   имени свойства. Нужно убедиться, что C#-свойство с `[Export]` видно Godot как
   свойство узла, иначе реплика молча не придёт. Минимальная проверка:
   «поставить на сервере → прочитать на клиенте».
3. **`MultiplayerSpawner.SpawnPath`** задаётся относительно самого спавнера
   (`".."` для `GameRoot`). Ошибка на одну ступень даёт «узел создан не там» без
   внятной ошибки.
4. **Две `Camera3D.Current = true`.** В `Player.tscn` камера включена в сцене;
   при спавне второго игрока viewport теряет камеру. Решается в §3.5 п.2.
5. **`physics_ticks_per_second` vs `ReplicationInterval`** (§2.9): если оставить
   ввод на каждый физ.шаг, бюджет пакетов не сходится.
6. **`SetMultiplayerAuthority` и `Reparent`:** в 4.7 в доке прямо сказано, что
   новый авторитет **не** реплицируется другим пирам и **не** наследуется
   дочерним узлам (в том числе newly added children). Значит после переноса
   игрока под `Seat` его авторитет формально не меняется (`Reparent(...,
   keepGlobalTransform: true)` авторитет не трогает), но после любой смены
   авторитета `Ship` детей надо обновлять явно (`recursive: true`), иначе
   `NetState`/`AttachPoint` останутся с прошлым авторитетом. Убедиться нужно и в
   том, и в другом (§2.5 п.5, §3.9 п.9).
7. **CLI-флаги до `--` теряются молча** (§2.1): движок их не отдаёт
   `GetCmdlineUserArgs()`, процесс стартует с UI и без сервера, ошибок нет.
   Проверено прогоном `tests/probe_args.gd` на 4.7.2; assert в `NetTest` (§3.9 п.7)
   нужен именно потому, что иначе это выглядит как поломка ENet.
8. **Висящий dedicated-процесс** с занятым портом делает следующий прогон
   `Error.AlreadyInUse` (§2.1): в `run-net-demo.sh` сервер убивается по PID и в
   успехе, и в провале (trap), иначе «второй запуск подряд» падает без связи с
   причиной. Это прямое следствие dedicated-модели: при listen-server процесс
   закрывался вместе с окном игрока.
9. **Сервер без `Player`.** Любой код, который находит игрока по пути
   (`Main/Player`, `GameRoot/Player`), на dedicated-процессе отдаёт `null`.
   Проверяется прогоном `-- --server --headless`: в логе не должно быть
   `NullReferenceException`, а количество `Player`-источников в `DebugInfoBus`
   должно быть **нулём** (§2.10 п.1).
10. **Сервер из UI (§4 В13=A) — состояние, которого раньше не было: окно живёт,
    а игрока нет.** Три конкретных способа споткнуться:
    - `DebugLayer` уже построил панель **до** нажатия `Start Server`, поэтому
      нельзя предполагать, что «раз UI есть, значит мы играем»: все проверки
      режима идут через `NetworkManager.Role`, а не через наличие панели;
    - `Disconnect` из серверного режима обязан вернуть `Offline` и, если в этом
      окне до сервера был одиночный игрок, восстановить его — иначе игрок,
      погонявший сервер из меню, получает мёртвый мир без возможности вернуться
      (assert §3.9 п.5);
    - `Start Server` при занятом порту (например, висит процесс из
      `run-net-demo.sh`) обязан показать причину в панели, а не «зависнуть»:
      `Error.AlreadyInUse` из §2.1 доходит до UI, роль остаётся прежней.
11. **Возврат на место спавна (§4 В2) — три способа сделать вид, что он работает:**
    - писать `LastSeenState` после `QueueFree` — трансформ уже нечитаем, и
      сервер «возвращает» игрока в ноль, что неотличимо от «стартовой точки» без
      проверки координат (порядок в обработчике зафиксирован в §3.9 п.8);
    - сравнивать позицию с `Vector3.Zero`, а не с маркером `SpawnPoints`: тест
      проходит на пустой сцене и падает, когда мир передвинули (первый подпункт
      assert'а §3.9 п.8 сравнивает именно с `SpawnN`);
    - хранить позицию по `peerId`: после переподключения ENet даёт новый id, и
      возврат «работает» только пока клиент не переподключался, то есть ровно
      тогда, когда его никто не проверяет. Отсюда отдельный `playerKey` (§2.4).
12. **В3=B: два источника истины при смене пилота.** Пока идёт цепочка
    «RPC пилота → сервер меняет авторитет → рассылка нового авторитета пирам»
    (§4 В3 п.6), разные пиры считают авторитет разным, и в этом окне возможны
    одновременно два интегрирующих пира (старый пилот ещё не заморозил тело,
    новый уже разморозил) или ноль (оба заморозили). Единственная защита —
    `SimTick` в репорте: снимки со старым тиком отбрасываются, а сервер при
    расхождении дельты скорости больше допуска отзывает авторитет в `1`, а не
    «чинит» тело. Проверяется в `run-net-demo.sh` вручную: вход/выход из кабины
    10 раз подряд без рывков картинки (§2.10 п.7).
13. **В3=B: серверный синхронизатор `Ship` перестаёт быть источником.** Направление
    репликации привязано к авторитету (§3.3: «from the multiplayer authority to the
    remote peers»), поэтому при авторитете-клиенте синхронизатор сервера ничего не
    отправляет, а данные серверу присылает синхронизатор пилота. Формально этого
    достаточно, но полагаться на это вслепую нельзя: у сервера появляется чужой
    источник и собственный выключенный, и если в 4.7 есть нюанс с пересечением этих
    двух состояний, получится «корабль дёргается между двумя источниками». Поэтому
    сервер обязан принимать позицию по RPC-репорту, а не по синхронизатору (§2.3,
    §3.9 п.11). Если репорт забыть или он потеряется (unreliable), сервер «видит»
    корабль стоящим на месте, и все серверные проверки (стыковка, попадания,
    `MoveInAttachedPlane`) работают по устаревшей позиции. Поэтому репорт —
    `Reliable` + `SimTick`, а в панели у сервера печатается возраст последнего
    принятого снимка в мс; растёт выше 200 мс — это и есть поломка.
14. **В3=B: `Freeze` на сервере ломает серверную физику корабля.** Замороженное
    тело не интегрируется, но контакт с миром у него остаётся — значит серверные
    триггеры (`DockingZone`, мишени) продолжают срабатывать по позиции из
    замороженного трансформа, а не из `NetPosition`. Правило: все серверные
    проверки, которым нужна позиция корабля под пилотом, читают `NetState`, а не
    `GlobalPosition`. Иначе стыковка «работает» с запаздыванием на величину
    заморозки, и это самый трудный для отладки баг всей схемы.
15. **В4=B: путь перестаёт быть идентификатором.** Пока корабль один и лежит в
    сцене, `Main/Ship` — валидный адрес, и код, который его использует, выглядит
    правильным. После перехода на спавнер у двух кораблей пути `GameRoot/Ship` и
    `GameRoot/Ship2`, имена даёт спавнер, и порядок обхода дерева не гарантирован.
    Любой оставшийся `NodePath("Ship")` начинает указывать на первый попавшийся
    корабль — ошибка не в падении, а в «игрок сел не в тот корабль». Поэтому
    адресом служат `ShipNetId` и пара `(ownerNetId, NetId)`, а grep по `NodePath`
    со словом `Ship` / `Player` — часть шага 4b (§3.10).
16. **В4=B: гонка «спавн узла ↔ RPC авторитета».** `AssignShipAuthority` приходит
    раньше, чем `MultiplayerSpawner` создал у пира узел корабля. Пир, пропустивший
    назначение, навсегда остаётся с авторитетом `1` и `Freeze == false`, то есть
    интегрирует чужое тело параллельно с пилотом, и картинка двоится. Лечится
    очередью отложенных назначений + применением из `_EnterTree` корабля (§4 В4
    п.2). Проверка — `run-net-demo.sh`: второй клиент заходит **в полёте** первого
    и через 1 с показывает тот же `auth`, что и первый (§2.10 п.7).
17. **В4=B: видимость как канал рассылки — ловушка, а не оптимизация.** Докa
    `MultiplayerSpawner` говорит, что спавнеры обрабатывают узлы согласно видимости
    синхронизаторов. Отсюда очень естественное «а давай снимем видимость пилоту,
    раз он и так авторитет» — и удаление корабля у этого пира. Восстановить его
    потом спавнер не обязан. Единственная защита — запрет в коде и assert
    `PublicVisibility == true` + отсутствие фильтров (§3.9 п.13).
18. **В4=B: `DebugInfoBus` с одним именем на два корабля.** Молчаливая потеря
    половины отладочной информации: панель показывает только последний
    зарегистрированный источник, и при отладке рассинхрона второго корабля выглядит
    так, будто второго корабля нет вовсе. Ключ — `Ship#{ShipNetId}` (§3.1 п.6),
    assert на два источника — §3.9 п.15.
19. **В4=B: удаление корабля с пристёгнутым игроком.** Если `QueueFree` раньше
    записи `LastSeenState`, возврат игрока ведёт в несуществующую точку, и сервер
    уходит в ветку «нет точки в реестре» — в лучшем случае стартовая позиция, в
    худшем `null`-ссылка в серверной ветке. Порядок: снять крепления → переписать
    `LastSeenState` → `QueueFree`. Assert — §3.9 п.16.

### 3.12 Что обновить в документации

- `DEVELOPMENT.md`: сейчас там только «Этап 1 ✅» и «Этап 2 ⏳», раздела про сеть
  нет вовсе. Завести «Этап 0 — Сетевая архитектура ⏳» со списком реальных файлов
  (`NetworkManager.cs`, `NetState.cs`, `NetTest.tscn`), зафиксировать RPC-конвенцию
  (§2.3) и правило «истина/картинка» (§2.5). Отдельно — абзац про вход в игру:
  лобби нет, вход = `RequestSpawn(playerKey)`, игрок возвращается туда, откуда
  вышел, иначе — на `SpawnPoints`; сохранённая позиция живёт до рестарта
  серверного процесса (§2.4, §4 В2). Отдельно — абзац про модель сервера:
  **dedicated, listen-server нет** (§4 В1), как запускать два процесса
  (`run-net-demo.sh`, флаги после `--`), и почему «сыграть с собой в одном окне»
  в этом проекте нельзя намеренно. Рядом — два способа стать сервером (§4 В13 = A):
  кнопка `Start Server` в окне и CLI `-- --server --headless`, и что делает
  `Disconnect` (возврат в `Offline` + возврат одиночного игрока).
- `DEVELOPMENT.md`, там же — абзац про владение кораблём (§4 В3 = B): «у `Ship`
  всегда ровно один авторитет; кабина занята — им является пир-пилот, кабина
  свободна — `1` (сервер)». Обязательно прописать две вещи, которые иначе
  ломаются при чтении кода: (а) `Freeze = true` у всех, кто не авторитет, включая
  сервер при пилоте; (б) сервер читает позицию корабля под пилотом из `NetState`
  (RPC-репорт), а не из `GlobalPosition` (§3.11 п.14). Отдельно — список из 4
  шагов смены авторитета и слово `AssignShipAuthority`, потому что
  `SetMultiplayerAuthority` само другим пирам не рассылается.
- `stage1-plan.md`: пометить, что `Ship.tscn`/`Main.tscn`/`Player.tscn` изменены
  сетью, и что assert'ы §3.8 — осознанные, а не регрессия.- `prototype-plan.md`: раздел «0. Сетевая архитектура» — вычеркнуть `ShipStub`
  (файл удалён на шаге 1.5 стадии 1) и заменить на `Ship` + `NetState`.

---

## 4. Спорные пункты: вопросы, рекомендации, решения

Пункты, где в плане есть развилка. Формат: вопрос → варианты → рекомендация →
**решение** (пусто = не выбрано; вписать букву или свой вариант, и правки по тексту
разделов делаются уже после выбора).

### В1. Кто играет на сервере (§1.1, §2.1)
- A — listen-server: хост = сервер и первый игрок; `--server` = dedicated без игрока.
- B — только dedicated; хост подключается к своему порту как обычный клиент.
- C — на стадии 0 только listen-server, dedicated отложить.

Рекомендация: **A** — `MultiplayerApi.IsServer` даёт серверную ветку бесплатно,
dedicated = тот же код плюс флаг; B удваивает число процессов в тестах.
**Решение: B — только dedicated.** Принято осознанно, вопреки рекомендации.
Следствия (все внесены в текст разделов):
1. режима `Server` (host-как-игрок) в `NetRole` **нет** — остаются
   `Offline / Dedicated / Client`, см. §2.1;
2. игрок хоста — обычный клиент, `Join("127.0.0.1")`; у **ни одного** игрока
   авторитет не равен `1`, авторитет `1` носят только серверные тела
   (§2.5 п.5, §3.1);
3. advantage хоста отсутствует по построению: у хоста-клиента те же RTT и
   интерполяция, что у остальных (на loopback RTT ≈ 0, но код тот же);
4. минимальный прогон = **2 процесса** (headless-сервер + 1 клиент), стенд
   §2.6 обязателен, а не опционален, и он же становится CI-прогоном (§2.10);
5. предсказание с откатом (В11) становится проверяемым: у хоста rollback
   не «не срабатывает никогда», как при listen-server, а срабатывает на
   равных со всеми.
Цена, принятая явно: в каждом сетевом прогоне минимум два процесса, и
оффлайн-режим стадии 1 остаётся отдельным (однопроцессным) режимом, а не
«сервером в том же окне».

### В2. Вход в игру: лобби или мгновенный спавн (§1.2, §2.4)
- A — `Lobby.tscn` (Start Server / Join, список пиров) → RPC `RequestSpawn` → спавн в `GameRoot`.
- B — спавн сразу в `PeerConnected`, лобби = дебаг-кнопки в той же сцене.

Рекомендация: **B** для стадии 0, но `RequestSpawn` остаётся отдельным RPC-шагом,
чтобы будущий переезд между сценами (в `prototype-plan.md` такого этапа пока нет —
ссылка на «этап 8» в прошлой ревизии была битой) не переписывал вход. После В1=B
вариант B уточняется: спавн идёт не «в `PeerConnected`», а в `PeerConnected` +
`RequestSpawn` от этого пира (§2.4), потому что у клиента `PeerConnected` про
сервер не приходит.

**Решение: B — лобби нет, вход = спавн в текущей сцене.** Плюс принято правило
позиции спавна: **игрок возвращается туда, где он вышел; если сохранённой позиции
нет — на стартовую точку.** Принято:
1. сцены входа нет: `Main.tscn` грузится сразу, мир (станция, корабль-мишень)
   рендерится, а вход — это кнопки `Start Server` / `Join` в `DebugLayer`
   (§3.7, §4 В13) и роль в `NetworkManager`. Отдельного состояния «я ещё не в
   игре» на уровне сцены нет — оно есть на уровне роли (`Offline` / `Client`) и
   должно читаться только из `NetworkManager.Role` (§3.11 п.10);
2. состояние «подключился, но ещё не игрок» обрабатывается явно: у клиента оно
   длится от `ConnectedToServer` до спавна своей реплики, и сервер обязан ответить
   `SpawnDenied.reason`, если слотов нет (§2.4), иначе клиент зависает в этом
   состоянии;
3. `RequestSpawn` остаётся отдельным RPC-шагом и единственным способом попасть в
   мир: сервер не спавнит никого по одному факту соединения (§2.4). Если лобби
   появится позже, оно добавится как ещё один вызывающий `RequestSpawn`, а не как
   новая схема входа;
4. **`peerId` не является идентификатором игрока.** ENet выдаёт новый
   `unique_id` при каждом переподключении, поэтому «где этот игрок был» нельзя
   хранить по `peerId`. Идентичность — строковый `playerKey` (GUID, один раз
   создаётся клиентом и лежит в `user://identity.cfg`), который клиент присылает
   в `RequestSpawn(playerKey)` (§2.4);
5. сохранённая позиция — **серверное** знание: сервер пишет
   `playerKey → (позиция, ориентация, к чему был пристёгнут)` в момент
   `PeerDisconnected` и читает его при следующем `RequestSpawn` с тем же ключом.
   Клиент не может запросить произвольную позицию — это обошло бы всю модель
   авторитетности (§2.5);
6. сохранность живёт **столько, сколько процесс сервера**: рестарт dedicated-процесса
   = все на стартовые точки. Запись на диск (`user://`) — вне стадии 0, это вопрос
   для стадии 2+ (сохранение мира вообще);
7. при возврате «откуда вышел» скорость **не** восстанавливается: игрок спавнится
   с `Vector3.Zero`. Иначе возврат в движущийся корабль даёт импульс, который
   сервер потом «оправдывает» ретрансляцией;
8. если игрок был пристёгнут к точке, восстанавливается **крепление**, а не
   мировая координата: `Reparent` к той же `AttachPoint` (по паре
   `(ownerNetId, pointNetId)`, §3.4), позиция берётся из точки. Это единственный
   безопасный случай: мировая координата «где стоял» внутри движущегося корпуса
   через минуту уже внутри геометрии;
9. стартовые точки — узлы `Main/SpawnPoints/*` (§3.6), сервер выбирает свободную
   (не пересекается с существующими телами) и перебирает по кругу. Жёсткий
   `Vector3.Zero` в коде спавнера запрещён: «стартовая точка» обязана быть данными
   сцены, иначе перенос мира ломает спавн молча.

Цена решения B: нет состояния «лобби» — игрок видит чужой мир до входа, и
«я подключён, но ещё не игрок» приходится показывать строкой в панели;
`playerKey` — самодельная идентичность без аутентификации (подделать ключ =
занять чужой слот), это принимается на стадии 0 и закрывается только вместе с
настоящей аутентификацией.

### В3. Кто владеет кораблём (§2.5 п.3, §3.1)
- A — авторитет `Ship` всегда `1` (сервер); клиент только интерполирует картинку.
- B — авторитет `Ship` = пир-пилот, сервер валидирует.

Рекомендация: **A** — предсказание с откатом нужно только `CharacterBody3D`-игроку;
у корабля откат по инерции выглядит хуже интерполяции.

**Решение:** **B** — авторитет `Ship` = пир-пилот. **Правило: пилота нет —
авторитет `1` (сервер).** То есть у корабля всегда ровно один авторитет, но им
является не сервер, а тот пир, который в данный момент управляет кабиной;
пока кабины никем не заняты (или пилот отключился) — авторитет возвращается `1`,
и корабль симулирует сервер.

Почему так, а не A: в A каждый импульс проходит путь «клиент → сервер → реплика»,
то есть один полный RTT на каждое нажатие. Для `RigidBody3D` с инерцией это не
«задержка картинки», а задержка управления: импульс приходит в тот момент, когда
нос корабля уже ушёл. В B пилот интегрирует тело локально и является истиной для
своего корабля, задержка управления равна нулю.

Цена решения (обязательные следствия, их надо делать, иначе B хуже, чем A):

1. **Свой синхронизатор сервера перестаёт быть источником `Ship`.**
   `MultiplayerSynchronizer` отправляет свойства **только с авторитетного пира**
   («from the multiplayer authority to the remote peers», §3.3): когда авторитет =
   клиент, синхронизатор сервера ничего не шлёт, а данные серверу присылает
   синхронизатор пилота. Формально этого хватает, но строить на этом единственную
   канал нельзя: у сервера оказывается выключенный собственный синхронизатор и
   чужой включённый, и при смене пилота эти состояния пересекаются. Поэтому сервер
   обязан принимать `NetState` отдельным каналом — RPC-репорт от пилота
   (`NetPosition`/`NetRotation`/`NetLinearVelocity`/`SimTick`, частота 15–20 Гц,
   `Mode = Authority`, потому что авторитет теперь пилот). В варианте A этого кода
   нет вообще.
2. **Валидация на сервере — детектор, а не корректор.** Сервер не имеет права
   телепортировать или догонять чужое тело: пока у корабля есть пилот, серверное
   тело тоже заморожено (п. 4 ниже), и у сервера нет права на второе интегрирование.
   Сервер может только отбросить подозрительный репорт (несоответствие
   `SimTick`/импульсов) и отозвать авторитет обратно в `1`.
3. **Смена авторитета не реплицируется сама.** В доке `SetMultiplayerAuthority`
   прямо сказано: новый авторитет другим пирам не рассылается, и дочерним узлам
   он не наследуется. Нужен явный `AssignShipAuthority(shipNetId, peerId)`
   (`CallLocal` + рассылка всем), который на **каждом** пира вызывает
   `SetMultiplayerAuthority(peerId, recursive: true)` и перестраивает `Freeze`
   и `SetVisibilityFor`. Без этого после смены пилота часть пиров продолжает
   брать картинку со старого источника.
4. **Правило `Freeze` переворачивается.** Не «клиент → `Freeze = true`», а
   «не авторитет → `Freeze = true; FreezeMode = FreezeModeEnum.Static`», и в
   число не-авторитетов попадает **сам сервер**, пока корабль под пилотом.
   `Static`, а не `Kinematic`: замороженное кинематическое тело на сервере
   продолжает расталкивать локальные узлы по своей траектории.
5. **`ApplyImpulse` — только авторитету.** Кнопка `Nudge Ship` и ввод пилота
   шлют импульс лишь там, где `IsMultiplayerAuthority()`. В offline авторитет = `1`
   и `IsMultiplayerAuthority() == true`, поэтому 89 assert'ов стадии 1 не меняются.
6. **Вход/выход из кабины — это 4 шага с окном рассинхрона**: RPC пилота на сервер
   → сервер меняет авторитет → RPC-рассылка новым пиром → каждый пир перестраивает
   `Freeze`/visibility. Между шагом 2 и шагом 4 есть окно, в котором пиры знают
   разный авторитет. Лечится счётчиком `SimTick` в репорте (устаревшие снимки
   отбрасываются) и тем, что картинка интерполируется **всегда**, а не только
   не-авторитету. Проверяется вручную в `run-net-demo.sh`: 10 входов/выходов из
   кабины подряд без рывков картинки и без зависшего `auth` (§2.10 п.7–8).
7. **Объём работы:** +`scripts/core/ShipAuthority.cs` (или метод в `NetworkManager`),
   +RPC-репорт `NetState`, +детектор на сервере. Оценка 1.5–2 дня против
   ~0.5 дня для A. Плюс навсегда остаётся два режима корабля (под пилотом /
   автопилот на сервере) вместо одного.

Что B убирает: предсказание с откатом для корабля не нужно ни при каких
обстоятельствах — пилот и есть авторитет, он интегрирует истину. Откат нужен
только `CharacterBody3D`-игроку. Это снимает противоречие §2.5 п.3 ↔ п.5
(см. правку там же).

### В4. `Ship` в сцене или через спавнер (§3.6)
- A — `Ship` остаётся статичным узлом `Main.tscn`, реплицируется только `NetState`.
- B — сервер спавнит `Ship` через `MultiplayerSpawner` + `AddSpawnableScene`.

Исходная рекомендация была **A** (меньше движущих частей, `NodePath` совпадает у
всех пиров) — она отменена решением ниже: требование «игрок меняет корабли,
переходит между ними и на станцию» снимает её, потому что статичный узел описывает
ровно один вечный корабль. Обязательное следствие любого из вариантов: узел обязан
существовать по одинаковому пути у всех пиров, иначе `MultiplayerSynchronizer`
молча не находит узел. В A это гарантирует сама сцена, в B — спавнер.

Как на это влияет В3 = B: у статичного узла смена авторитета не покрыта ничем —
`MultiplayerSpawner` в доке завязан на видимость синхронизатора («spawners will
handle nodes according to visibility of synchronizers»), а `SetMultiplayerAuthority`
сам по себе другим пирам не рассылается. Поэтому при A весь механизм смены
пилота — только собственный RPC `AssignShipAuthority` (§2.5 п.5), и он обязан
быть, а не «опциональная оптимизация». При B (спавнер) добавляется соблазн
использовать видимость синхронизатора как канал рассылки авторитета, но тогда
спавнер начнёт обращаться с узлом по правилам видимости (вплоть до удаления у
пира, которому видимость сняли), а корабль — не удаляемый узел. С учётом В3=B это
не аргумент за A, а запрет: авторитет меняем только своим RPC и только
рекурсивно, видимость к кораблям не трогаем (§4 В4 п.1).
**Решение:** **B** — `Ship` живёт в отдельной сцене (`scenes/Ship.tscn`) и спавнится
сервером через `MultiplayerSpawner`. Статичного узла `Ship` в `Main.tscn` не будет.

Причина не в удобстве, а в требовании геймплея: игрок умеет **менять корабли,
переходить из одного в другой и на станцию**. Значит в мире их несколько, они
появляются и исчезают в рантайме, и «один вечный узел `Main/Ship`» описывает только
первый корабль. Вариант A физически не масштабируется: чтобы дать игроку второй
корабль, в A пришлось бы либо заранее плодить инстансы в `Main.tscn` (и тогда у
всех пиров обязан быть ровно тот же набор, включая неиспользуемые), либо
создавать узел вручную и писать рассылку «появился узел» самому — то есть
реализовывать `MultiplayerSpawner` руками.

**Важная поправка к формулировке «спавнится с игроком»:** корабль **не** привязан
к игроку и спавнится **не** на каждого игрока. Игрок — пассажир, он приходит и
уходит; корабль — объект мира, он один на всех и переживает отключение любого
игрока. Поэтому:

- спавнит корабль **только сервер**, при старте мира (или по игровому событию),
  а не клиент и не «вместе с `RequestSpawn`»;
- порядок: **сначала корабли и станция, потом игроки** — иначе `LastSeenState`
  со ссылкой на точку крепления указывает на ещё не заспавненный корабль
  (§2.4 п.4);
- удаление корабля — тоже серверное решение, и перед ним сервер обязан
  переписать `LastSeenState` всех пристёгнутых к нему игроков (иначе возврат
  ведёт в несуществующую точку, §2.4 п.3).

Обязательные следствия B (без них B хуже, чем A):

1. **Канал рассылки авторитета — только свой RPC.** `MultiplayerSpawner` в доке
   завязан на видимость синхронизаторов («spawners will handle nodes according
   to visibility of synchronizers»): снятая видимость для спавнера означает
   «у этого пира узла нет», то есть **удаление**. Корабль — не удаляемый узел,
   поэтому `SetVisibilityFor` / `AddVisibilityFilter` к `Ship`-синхронизатору
   применять запрещено: `PublicVisibility = true` навсегда, а смена пилота идёт
   исключительно через `AssignShipAuthority` (§4 В3 п.3). Для чужих `Player`
   видимость включать можно — там удаление узла у невидящего пира допустимо.
2. **Очередь отложенных назначений авторитета.** `AssignShipAuthority(shipNetId,
   peerId)` может прийти на пир раньше, чем спавнер создаст у него узел этого
   корабля (спавн идёт через `MultiplayerSpawner`, он асинхронный). Пир, у
   которого узла ещё нет, обязан положить пару в очередь и применить при
   `_EnterTree` корабля. Без этого часть пиров навсегда остаётся с авторитетом
   `1` и `Freeze == false`, то есть интегрирует чужое тело (§3.1 п.2).
3. **Стабильный `NetId` нужен самому кораблю, не только точкам крепления.**
   Пока корабль один и статичен, его адрес — это путь `Main/Ship`. Когда их
   несколько и они спавнятся, путь перестаёт быть идентификатором (у двух
   кораблей пути `GameRoot/Ship` и `GameRoot/Ship2`, и имена даёт спавнер).
   Сервер выдаёт кораблю `ShipNetId` из монотонного счётчика **до** `AddChild`
   и пишет его в `NetState` со `PropertySetSpawn(true)`, чтобы пир получил его
   вместе с узлом. Все RPC про корабль (`AssignShipAuthority`, репорт
   `NetState`, `RequestAttach`) — по `ShipNetId`, не по `NodePath`.
4. **Реестр `(ownerNetId, NetId) → AttachPoint` (§3.4) обязан выдерживать N
   владельцев.** `ownerNetId` выдаётся сервером и уникален в пределах процесса:
   одна схема нумерации и для кораблей, и для станции, иначе `ownerNetId = 1`
   окажется одновременно у первого корабля и у станции. Точка станции — это
   обычный `AttachPoint` в `Station.tscn`, отдельного механизма для станции нет.
5. **`RegisterInfoSource("Ship", …)` (§3.1 п.6) перестаёт быть уникальным
   ключом.** Два корабля зальют в `DebugInfoBus` два источника с одним именем,
   и в панели останется последний. Ключ — `Ship#{ShipNetId}`.
6. **Что B даёт взамен:** поздний вход в игру. `PropertySetSpawn(true)` шлёт
   новому пиру узел вместе со снимком свойств, поэтому корабль он получает
   сразу и в правильном состоянии. В A этого нет: узел «появился» никогда, и
   пришедший через 5 минут пир видит `NetPosition = Vector3.Zero`, пока
   кто-нибудь не дёрнет корабль, — значит в A пришлось бы писать собственное
   сообщение «начальное состояние корабля». В B его нет.
7. **Что B стоит:** `GameRoot` + `Spawner` + `AddSpawnableScene` в `Main.tscn`
   (§3.6); assert'ы `Main/Ship` → `GameRoot/Ship` (§3.8); очередь из п.2;
   `ShipNetId` из п.3. Оценка +0.5 дня к и без тому объёму, который нужен для
   спавна игрока (§2.4) — инфраструктура одна и та же.

Связка с В3=B: при B у корабля и так есть собственный RPC-канал авторитета, а
спавнер даёт второй канал (видимость), который для корабля запрещён. Это не
аргумент против B — это запрет, который надо записать в код и в assert
(`PublicVisibility == true` у `Ship`-синхронизатора, §3.9 п.13).

### В5. Точки крепления — под телом или под картинкой (§3.3)
- A — под `ShipHull`: игрок визуально всегда совпадает с корпусом, но его физика
  отстаёт от коллизии корпуса на величину интерполяции.
- B — под `Ship` (тело): физика точна, но на клиенте игрок «дрожит» относительно
  нарисованного корпуса.

Рекомендация: **A** — этап 6 (стыковка, `MoveInAttachedPlane`) визуальная задача,
расхождение на 30 Гц измеряется сантиметрами.
**Решение:** _(не выбрано)_

### В6. Что входит в бюджет «< 50 пакетов/с на игрока» (§2.7, §2.9)
- A — все пакеты пира: тогда 30 снимков + 60 ввода = ~90/с, порог недостижим.
- B — только исходящие репликационные снимки (30/с), ввод вне бюджета.
- C — порог поднят до ~120/с, ввод остаётся на каждый физ.шаг.

Рекомендация: **C** с фиксацией в плане: «50/с — только репликационные пакеты;
ввод — отдельный бюджет 60/с, `Unreliable`». Порог в `prototype-plan.md` считался
без учёта ввода.

После В3 = B в бюджет добавляется ещё один поток, о котором в A/B речи не было:
**пилот шлёт состояние своего корабля серверу отдельным RPC-репортом** (§2.3,
§3.1 п.5), потому что синхронизатор пилота работает на клиентов, а серверу нужен
свой канал. Итого на пир-пилот: 30 снимков игрока + 30 снимков корабля (всем,
через синхронизатор) + 15–20 репортов корабля на сервер (RPC) + 60 ввода. Порог C
(«репликационные ≤ 50/с») при пилотировании превышается и на репликации, поэтому
в варианте C надо записать, что для пира-пилота потолок поднимается до ~90/с, или
репорт корабля на сервер идёт на 15–20 Гц (серверу не нужна частота картинки, ему
нужна валидация и серверные проверки). Это единственная строка бюджета, которая
появляется именно из выбора В3=B, и её надо зафиксировать, а не «потом померить».
**Решение:** _(не выбрано)_

### В7. Частота репликации (§2.9)
- A — 30 Гц (`ReplicationInterval = 1/30`), сглаживание интерполяцией клиента.
- B — 60 Гц = физ.тику, интерполяция почти не нужна, трафик ×2.

Рекомендация: **A**.
После В3 = B частота перестаёт быть одной цифрой: `ReplicationInterval`
(синхронизатор, картинка клиентам) и частота RPC-репорта корабля на сервер —
разные настройки, и вторую можно взять ниже (15–20 Гц), не теряя картинку:
клиенты читают синхронизатор, а серверу нужен лишь темп для валидации и
серверных проверок (§3.11 п.14). Выбранная частота репорта обязана быть выше,
чем допуск по `|err|` в панели (200 мс), иначе сервер всегда работает по
устаревшей позиции.
**Решение:** _(не выбрано)_

### В8. Слои физики (§2.8)
- A — оставить текущие 6, добавить `Debris = 1u << 6`, `World = 1u << 7` (слои 7–8).
- B — перенумеровать под `prototype-plan.md` (World/Ship/Character/Target/…) сейчас.
- C — не добавлять, пока debris реально не появится.

Рекомендация: **A** — сдвиги не ломают существующие ссылки, `layer_names` в
`project.godot` синхронизируется с `CollisionLayers.cs`.

После В3 = B к слоям добавляется требование, которого в A/B/C нет: у **двух**
копий одного корабля (авторитетная и замороженная на не-авторитете) должны быть
одинаковые `CollisionLayer`/`CollisionMask`, иначе серверные проверки
(`DockingZone`, мишени) видят контакт только у одной копии. Различать копии надо
не слоями, а `Freeze` и авторитетом. Отдельная ловушка: замороженное
`Static`-тело продолжает участвовать в контактах, поэтому на не-авторитете
контакт с чужим кораблём может дать локальный `body_entered`, которого у
авторитета нет (§3.11 п.14). Правило: любые `body_entered`/`body_exited` у
`Ship` обрабатывает только авторитетный пир, остальные игнорируют.
**Решение:** _(не выбрано)_

### В9. Тестовый стенд сети (§2.6, §3.9)
- A — `run-net-demo.sh` (headless-сервер + 2 клиента на `127.0.0.1`) + `NetTest.tscn`
  на `OfflineMultiplayerPeer` в одном процессе.
- B — плюс `NetLoopbackTest.tscn` с двумя `MultiplayerApi` в одном процессе.
  Механизм в 4.7: `SceneTree.SetMultiplayer(MultiplayerApi api, NodePath forScene)`
  и `SceneTree.GetMultiplayer(NodePath)` — то есть интерфейс привязывается к
  **поддереву**, а не к процессу; `Node.Multiplayer` возвращает нужный для узла.
  Пара `MultiplayerApi` + `OfflineMultiplayerPeer` (или два связанных ENet-пира)
  даёт «сервер» и «клиент» в одной сцене. `MultiplayerApi.CreateDefaultInterface()`
  возвращает новый экземпляр, `SetDefaultInterface(StringName)` — это про класс
  реализации, а не про второй интерфейс: не путать.
  Важное следствие из документации `Node.Multiplayer`: «переименование узла или
  его перемещение по дереву **не** переносит `MultiplayerApi` на новый путь —
  обновлять вручную». Это прямое подтверждение риска §3.11 п.1 (`Reparent`).
- C — только `NetTest.tscn`, без реального UDP-демо.

Рекомендация: **B** — именно loopback ловит главные болячки (§3.11 п.1–3:
`Reparent` + `RootPath`, `[Export]`-свойства в `SceneReplicationConfig`, `SpawnPath`).
**Решение:** _(не выбрано)_

### В10. `AttachPoint` через сеть: `NetId` или `NodePath` (§3.4)
- A — стабильный `[Export] int NetId` + реестр `(ownerNetId, NetId) → узел`.
- B — `NodePath` относительно корабля (`AttachPoints/Seat`), совпадает у всех,
  пока никто не делал `Reparent`.
- A′ — компромисс: ключ `(NodePath владельца, NetId)`, отдельный `ownerNetId` не нужен.

Рекомендация: **A′** — `NodePath` владельца стабилен для статичного корабля (В4=A),
`NetId` нужен только чтобы не зависеть от имён точек; это убирает целый реестр.
**Решение:** _(не выбрано)_

### В11. Предсказание с откатом для игрока на стадии 0 (§2.5 п.3)
- A — каркас: `InputFrame` с `tick`, буфер 120 записей, жёсткий телепорт при
  расхождении > 0.05 м.
- B — полноценный rollback с переигровкой физики на стадии 0.
- C — без предсказания вовсе, rollback перенести на этап с PvP/лагами.

Рекомендация: **A** — без него локальный игрок на клиенте дёргается, а переигровка
физики `CharacterBody3D` — отдельная большая работа.
После В1=B (dedicated) замечание: «у хоста задержка 0, откат не проверим» снимается —
все игроки, включая поднявшего сервер, одинаковые клиенты с одинаковым RTT, и
жёсткий телепорт при расхождении видно в `run-net-demo.sh` (§2.6) у любого из них.
То есть вариант A теперь проверяем, а не «впритык»; вариант B по-прежнему откладывается
из-за объёма, а не из-за непроверяемости.

После В3 = B формулировка «для игрока» перестаёт быть оговоркой, а становится
частью ответа: откат нужен **только** `CharacterBody3D`-игроку. Корабль в обоих
режимах (под пилотом — пилот и есть авторитет; без пилота — авторитет сервер, а
клиент только интерполирует) не имеет ситуации «я предсказал, сервер поправил»,
поэтому rollback для `Ship` не появляется ни при каких настройках лага. Это и
есть то, что покупается за цену В3=B: вместо двух механизмов (откат игрока +
откат корабля) остаётся один.
**Решение:** _(не выбрано)_

### В12. Правки `Stage1Test`: менять assert'ы или добавить второй тест (§3.8)
- A — правим 9 assert'ов под новую структуру (пути новые, количество то же).
- B — старые assert'ы остаются на offline-структуре, сетевое проверяет `NetTest`.

Рекомендация: **B** — `Stage1Test` отвечает за «сцену без сети» и не должен знать
про `GameRoot`; иначе при откате сетевого решения он становится блокером.
**Решение:** _(не выбрано)_

### В13. Как поднимается dedicated-сервер: только CLI или из UI игры (§2.1, §3.7)
- A — кнопка в дебаг-панели: текущий процесс становится сервером (`SelfId = 1`,
  `Player` не создаётся), играть из этого окна нельзя, нужен второй процесс.
- B — `HostGame`: кнопка запускает дочерний процесс-сервер
  (`OS.CreateProcess(OS.GetExecutablePath(), ["--path", …, "--headless", "--", "--server"])`)
  и сама подключается к нему клиентом — «Сетевая игра → Хостить» как в готовых играх.
- C — только CLI/`run-net-demo.sh`, из интерфейса сервер не поднимается.

Рекомендация была **C** для стадии 0 (меньше состояний UI), но это не следует из В1:
dedicated запрещает *игрока-сервер*, а не *запуск сервера из интерфейса*.
**Решение: A — сервер запускается кнопкой из интерфейса игры, в этом же процессе.**
Принято:
1. в дебаг-панели есть `Start Server` → `NetworkManager.StartServer()`; процесс
   становится сервером (`IsServer == true`, `SelfId == 1`), `Player` не создаётся,
   `DebugLayer` остаётся (окно продолжает рендерить мир — это «сервер под
   наблюдением», удобно для отладки);
2. **`HostGame` (вариант B) на стадии 0 не делаем**: `OS.CreateProcess` /
   `OS.Kill` / `OS.IsProcessRunning` в 4.7 есть (сигнатуры сверены с
   `GodotSharp.xml`), но это отдельная подсистема: ждать готовности порта,
   вешать `OS.Kill` на `NOTIFICATION_PRE_EXIT` (иначе сирота с занятым портом),
   читать `--log-file` убитого headless-процесса, объяснять игроку запрос
   брандмауэра Windows на `127.0.0.1`. Откладывается как отдельный вопрос стадии 2+;
3. кнопка `Join` в том же окне **не** подключается к серверу этого же процесса:
   в одном процессе один `MultiplayerPeer`, поэтому «поднял сервер и сам же
   подключился» нет ни кнопкой, ни кодом. Играть идёт второй процесс
   (`godot --path . -- --join 127.0.0.1` или `run-net-demo.sh`, §2.6);
4. `Disconnect` возвращает процесс в `Offline` — из «окна-сервера» можно выйти
   обратно в одиночный режим, не закрывая игру (assert в §3.9 п.5).

Цена решения A: игрок, нажавший `Start Server`, остаётся без игрока и обязан
запустить второй экземпляр игры. Это ровно та же цена, что зафиксирована в §4 В1
(«сыграть с собой в одном окне нельзя намеренно»), только теперь она видна в UI,
а не только в скрипте.

### Порядок применения решений

В1, В2, В4 и В13 уже применены (списки правок ниже), остальные вопросы — по мере выбора.

Как только решения вписаны, по ним правятся: §2.1 (роль сервера), §2.4 (вход),
§2.5 (авторитет корабля), §2.6 (состав тестов), §2.7–2.9 (бюджеты/частоты),
§2.8 (слои), §3.3–3.4 (иерархия `Ship` и `AttachPoint`), §3.6 (состав `Main.tscn`),
§3.8 (assert'ы), §3.9 (состав тестов), §3.10 (коммиты). Раздел §1 остаётся как
есть: он описывает проблемы, а не решения.

Уже внесено по В1 (=B, dedicated): §1.1, §1.2 (сигналы входа), §2.1 (три роли,
`StartServer` вместо `Host`, CLI-флаги после `--`), §2.2 (кто какие сигналы ловит),
§2.4 (вход через `ConnectedToServer` → `RequestSpawn`, запрет авторитета `1` для
игрока), §2.5 п.3/п.5 (`IsMultiplayerAuthority()` вместо `== 1`), §2.6 (стенд
обязателен, 2+ процесса, число клиентов задаёт скрипт, kill по PID), §2.7 (stdout-строка сервера),
§2.10 (критерии 1, 2, 5, 6), §3.1 (гейтинг через `IsMultiplayerAuthority()`),
§3.5 (п.5: реестр `peerId → Player` вместо жёсткого пути), §3.7 (кнопки без `Host`,
`role = dedicated, players = 0`), §3.8 (строка про `Host()` → `StartServer()`),
§3.9 (п.3: сервер без `Player`), §3.10 (шаги 1–2, 7), §3.11 (п.1: два процесса),
§3.12 (`DEVELOPMENT.md`), В2 и В11 (пометки, что аргумент «некому проверить» снят).

Уже внесено по В13 (=A, сервер из UI): §1.1 (тот же режим кнопкой, разница только
в способе запуска), §2.1 (таблица режимов, `Role` как единственный источник правды
о режиме, `Disconnect` → `Offline`, удаление одиночного `Player` при входе в
`Dedicated`), §2.6 (стенд из скрипта и кнопка — один режим, запрет ветки «если
сервер запустили из UI»), §2.7 (stdout-печать серверной статистики сохраняется и
при запуске кнопкой), §2.10 (критерий 7), §3.7 (кнопка `Start Server`,
`players = <N>`, отсутствие `HostGame`), §3.9 п.5 (assert на удаление/возврат
`Player`), §3.10 (шаги 5–6: переход `Offline → Dedicated` и прогон `NetTest`
вторым способом), §3.11 п.10 (окно живёт, игрока нет; панель ≠ режим; порт занят →
причина в панели), В13 целиком.

Уже внесено по В2 (=B, лобби нет + возврат на место): §2.4 (подразделы
«Идентичность: `playerKey`, а не `peerId`», «Где спавнится игрок», «Смена роли и
повторный вход»; порядок «записать `LastSeenState` → удалить узлы»; случай
`playerKey` уже занят + `DisconnectPeer(force: true)` без `PeerDisconnected`),
§2.6 (стенд переименован в `NetStand.tscn`), §2.10 (критерий 8), §3.6 (узел
`Main/SpawnPoints/*`, порядок обхода по имени, запрет хардкода `Vector3.Zero`,
`SpawnClearance`), §3.7 (`playerKey` коротко + строка `spawn = …`), §3.8 (assert
на `Main/SpawnPoints`), §3.9 (п.8 — три подпроверки возврата + идемпотентность +
`already-in-game`; п.9 — пустой `SpawnPoints` не роняет сервер), §3.10 (шаг 6:
`SpawnPoints` → `playerKey` → таблицы), §3.11 п.11 (пишем позицию после
`QueueFree`; сверка с `Vector3.Zero`; хранение по `peerId`), §2.4 (проверенный
`OS.GetUserDataDir()` через `tests/probe_paths.gd`), В2 целиком (в т.ч. снята
битая ссылка на «этап 8»).

Уже внесено по В3 (=B, авторитет корабля = пир-пилот, без пилота — `1`):
§2.3 (правило «позицию шлёт тот, кто её вычислял» + RPC-репорт `NetState` от
авторитета на сервер), §2.5 п.3 (один авторитет, но не всегда `1`; откат только
игроку), п.4 (`Freeze` у всех не-авторитетов, включая сервер; `Static`), п.5
(определён «корабль-кабина»: отдельного узла и второго авторитета нет; занятость
кабины = `Player` закреплён за `AttachPoint` и имеет авторитет своего пира; смена
авторитета — только сервером через `AssignShipAuthority(..., recursive: true)`,
потому что в 4.7 смена авторитета не реплицируется и детям не наследуется), п.6
(кто дёргает `ApplyImpulse`: только `IsMultiplayerAuthority()`, цепочка
«намерение → сервер гейтит → импульс у пилота → репорт»; offline сворачивается в
один локальный вызов, 89 assert'ов не меняются), §2.7 (счётчик репортов пилота +
`report age ms`), §2.9 (четвёртая частота: репорт 15–20 Гц отдельно от
`ReplicationInterval`), §2.10 (критерии 7–8: `Freeze`/`auth` у трёх процессов,
движение корабля под пилотом у всех, возврат авторитета при выходе и обрыве),
§3.1 (роль читается не один раз: `ApplyAuthority()` из `_Ready()` и из смены
авторитета; кнопка `Nudge Ship` только авторитету и перерегистрируется; гейтинг
репорта по `SimTick`; `auth`/`frozen` в панели; `CustomIntegrator` только
авторитету), §3.3 (`RootPath`/`SceneReplicationConfig` по факту 4.7: `AddProperty`,
`PropertySetReplicationMode/Spawn/Sync`, `PublicVisibility` + `SetVisibilityFor`
вместо «watchers»; ключевое следствие — направление репликации привязано к
авторитету, сервер получает истину только из RPC-репорта), §3.5 п.8 (кабина меняет
авторитет корабля: `AssignShipAuthority` из `ApplyAttach`/`ApplyDetach`/
`PeerDisconnected`, поле `Ship.PilotPeerId` вместо обхода детей), §3.4 п.3
(`ApplyAttach`/`ApplyDetach` обязаны вызвать смену авторитета, иначе кабина без
пилота), §3.9 п.2 (смена
авторитета до и после `AddChild` — разные проверки), новые п.9–11 (смена
авторитета: `Freeze` сразу, `FreezeMode = Static`, возврат авторитета,
рекурсивность; гейтинг `ApplyImpulse`; репорт по `SimTick`), §3.10 (шаг 3b: смена
авторитета **до** переезда в `GameRoot`/спавнер), §3.11 п.12–14 (окно рассинхрона
при смене пилота; «синхронизатор сервера перестаёт быть источником `Ship`,
направление репликации привязано к авторитету»; замороженное
тело на сервере: серверные проверки читают `NetState`, а не `GlobalPosition`),
§3.12 (абзац в `DEVELOPMENT.md` про владение кораблём), В4/В7/В8/В11 (пометки о
том, что меняется именно из-за В3=B). В8 и В11 по-прежнему без выбора — правки
носят характер следствий, а не ответов.

Уже внесено по В4 (=B, корабль живёт в отдельной сцене и спавнится сервером;
причина — игрок меняет корабли, переходит между ними и на станцию):
§2.4 (новый подраздел «Объекты мира спавнятся раньше игроков»: корабли и станция
спавнит только сервер, не на каждого игрока; порядок при старте — мир → игроки;
порядок при удалении — снять крепления и переписать `LastSeenState` → `QueueFree`;
тот же `MultiplayerSpawner` покрывает `Player`, `Ship`, `Station`),
§3.2 (`ShipNetId` в `NetState`, `0` = не назначен, адрес узла в сети),
§3.3 (пути свойств в `SceneReplicationConfig` — `NetState/<Prop>` относительно
`RootPath`, а не `NetPosition`; `PropertySetSpawn(true)` обязателен для
`NetPosition`/`NetRotation`/`ShipNetId`; `PublicVisibility = true` навсегда),
§3.4 п.2 (реестр на N владельцев: один монотонный счётчик `ownerNetId` для
кораблей и станции, коллизия ключа = громкая ошибка, точка станции — обычный
`AttachPoint`, авторитет станции всегда `1`), п.3 (переход «корабль → корабль» —
один `RequestAttach`, порядок `AssignShipAuthority(старый, 1)` →
`AssignShipAuthority(новый, pilot)`, у станции авторитет не меняется),
§3.1 п.6 (ключ источника `Ship#{ShipNetId}`, иначе два корабля затирают друг
друга в панели), п.8 (`ShipNetId != 0` в `_Ready()`, адрес выдаёт сервер до
`AddChild`, в offline — `NetworkManager`),
§3.5 п.3 (дебаг-кнопка крепит к ближайшей точке из реестра и печатает
`attach → ship#N/point#M`, иначе переход между кораблями вручную не проверить),
§3.6 (состав `GameRoot` после старта мира, `AddSpawnableScene` один раз на
сцену, имена экземпляров даёт спавнер → путь не идентификатор, порядок спавна
мир → игроки, запрет `SetVisibilityFor`/`AddVisibilityFilter` для кораблей,
`TestStand` ≠ `Station.tscn`),
§3.8 (assert'ы `GameRoot/Ship`, `GameRoot/Ship/ShipHull/Hull`, `ShipNetId != 0`
+ источник `Ship#1`, `Main/Ship` отсутствует; предупреждение про ожидание спавна
в offline, иначе assert красный из-за порядка инициализации),
§3.9 п.13–17 (запрет видимости; спавн-снимок + assert на путь `NetState/NetPosition`;
N кораблей: разные `ShipNetId`, два источника, attach по `(ownerNetId, NetId)`,
коллизия ключа; удаление корабля с пристёгнутым игроком; переход между кораблями
и инвариант «≤ 1 корабль с данным пилотом»),
§3.10 (шаг 4: порядок спавна и `ShipNetId` до `AddChild`; новый шаг 4b: второй
корабль отдельным коммитом, потому что на шаге 4 «адрес = путь» работает случайно),
§3.11 п.15–19 (путь не идентификатор + grep по `NodePath`; гонка спавн ↔
авторитет и её проверка в `run-net-demo.sh`; видимость как канал рассылки —
ловушка; `DebugInfoBus` с одним именем на два корабля; удаление корабля с
пристёгнутым игроком), В4 целиком (включая поправку к формулировке «спавнится с
игроком»: корабль — объект мира, не собственность игрока).
В5, В8, В11 по-прежнему без выбора; В5 теперь обязан учесть, что точек крепления
несколько на несколько владельцев, а В8 — что станция появляется как спавнящийся
объект, а не как статичный `TestStand`.
