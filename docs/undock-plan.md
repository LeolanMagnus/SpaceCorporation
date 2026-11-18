# План разработки: отстыковка (undock)

> Статус: **на согласовании**. Имплементация не начата до одобрения.
> Скиллы: `ponytail` (full) — минимальный дифф; `critic` — самопроверка перед выдачей.

## 0. Что есть сейчас (изученный код)

Модуль стыковки — три слоя:

| Слой | Файлы | Ответственность |
|---|---|---|
| Чистые константы/функции | `scripts/core/Docking.cs` | фазы `DockState` (byte, порядок фиксирован), пороги, пружина |
| Реестр сделок | `scripts/net/NetworkManager.Docking.cs` | `_dockRequests`, `BeginDock` / `SetDockPhase` / `AbortDock` (единственный писатель обеих сторон) |
| Поведение корабля | `scripts/entities/Ship.Docking.cs` | гейты запроса `AcceptDockFrom`, авто-отмена `AcceptDockCancelFrom`, фазы `TickDockPhases`, окно `TickDockWindow`, авто-захват `TickAlignCapture`, пружина `StepDockSpring`, контакт-гейт 5.10 в `HandleStationContacts` |

Поток фаз: `None → Requested → Approved → Capturing → Latched → Docked`.
`Docked` = сервер обнулил скорости, снапнул позу «брюхом к палубе» и поставил
`Freeze = true` (`Ship.ApplyAuthority`, единственная точка записи:
`dockLocked = (DockState == Docked)`).

Отмены СЕЙЧАС:
- **до захвата** — есть: радиоменю «Отменить посадку» → `RequestDockCancel` →
  `AcceptDockCancelFrom` (принимает только `Requested`/`Approved`) → `AbortDock`;
- **обрыв пира** (`OnPeerLeft`) — до `Docked`: `AbortDock`; **`Docked` не отменяется**:
  запись из реестра убирается, но `DockStateValue`/`DockShipNetId`/`Freeze` остаются —
  корабль навсегда стоит на палубе без пилота;
- **из Docked — отстыковки НЕТ вообще**. Это и есть пробел.

## 1. Пробелы, которые закроет отстыковка

1. `Docked → None` нет ни у игрока-пилота, ни у сервера (кнопкой/событием).
2. Последствия зависшего `Docked` (пир ушёл / захотели уйти из кресла):
   корабль вморожен в позу на палубе, станция «занята» (`DockShipNetId ≠ 0` →
   `BeginDock` для других даёт `station-busy`).
3. Снять `Docked` нельзя и через `AbortDock`: он ищет запись в `_dockRequests`,
   а после `OnPeerLeft` записи нет — `AbortDock` для «сиротского Docked» это no-op.
4. После перехода в `None` включается гейт 5.10 наоборот: корабль стоит **внутри**
   цилиндра станции (контакт с целью пропускался, пока фаза `>= Approved`),
   и первый же кадр `HandleStationContacts` начислит урон/отскок «изнутри».

## 2. Дизайн (минимальный, по образцу существующих путей)

### 2.1 Триггер — та же клавиша F / то же радиоменю, фаза решает действие

Сейчас F (`request_docking`) шлёт запрос стыковки, радиоменю имеет строки
«Запросить посадку» / «Отменить посадку». Отстыковка = **то же намерение
«расстаться со станцией» в фазе `Docked`**:

- пилот в кресле пристыкованного корабля жмёт F (или пункт радиоменю
  «Отстыковаться») → клиент-пилот шлёт `RpcUndock(shipNetId)` на сервер
  (AnyPeer, CallLocal=false, Reliable, канал 1 — копия `RpcCancelDocking`);
- серверный гейт `AcceptUndockFrom(senderPeer, shipNetId)`:
  - только авторитетная сторона (В14=B);
  - `senderPeer == PilotPeerId` (В17);
  - `Net.DockStateValue == Docked` (иначе отказ с причиной в комм-лог, по образцу
    «Посадка уже началась — отмена невозможна»).
- сервер выполняет `UndockNow(shipNetId)` (см. 2.2).

 Offline/хост — прямой вызов гейта, ровно как в `SubmitDockToServer`.

### 2.2 Серверная процедура `UndockNow` — в NetworkManager.Docking.cs

Одна точка, идемпотентная, переживает «сиротский Docked» (записи в реестре может
не быть):

```
UndockNow(shipNetId, why):
  entry = _dockRequests.TryGetValue(...) ?? по Net-полям корабля (DockStationNetId)
  ship.Net.DockStateValue = None; ship.Net.DockStationNetId = 0;
  _dockRequests.Remove(shipNetId);
  станция: SetDockStateServerOnly(None, 0)   // огни гаснут, станция свободна
  корабль: ApplyAuthority()                  // dockLocked=false → Freeze=false
  HudEventBus.Report("Отстыковка…") (только локальному пилоту, как в dock)
```

`AbortDock` расширяется до `UndockNow`-пути (или `AbortDock` вызывает общий сброс
по Net-полям, когда записи в реестре нет — реализация решит на месте; главное —
**одна** функция сброса докованных полей обеих сторон).

### 2.3 Анти-таран при выходе (пробел №4)

Корабль в момент отстыковки стоит «брюхом на палубе» внутри collision-цилиндра
станции (r=60). Сразу после `None` контакт-гейт 5.10 гаснет → урон + отскок.
Минимальное решение: **выталкивание перед снятием Docked**:

- в `UndockNow` сервер поднимает корабль вдоль нормали палубы площадки
  (`pad.GlobalBasis.Y`) на `HullSize.Y/2 + UndockPushOffM` (константа ~1.5 м) и
  даёт `LinearVelocity = padUp * UndockPushSpeed` (~3 м/с, ниже порога урона 12
  и ниже `DockSpeedLimit` 8 — сам выход никогда не «таранит»);
- у площадки-нет (внешний стыковочный узел) — вдоль `DockPointGlobal − stationPos`.

Снап позиции делает сам сервер (он и был автором снапа при Docked), реплика
доедет батчем. Синхронизировать `_predicted` у локального пилота — по образцу
снапа в `TickDockPhases`.

`Docking.cs`: новая константа `UndockPushSpeed = 3f` (+ `UndockPushOffM = 1.5f`).
Чистых функций и новых enum-фаз не нужно: `Docked → None` — прямое снятие,
промежуточные фазы «отстёгивается» избыточны (ponytail: не заводим, пока нет
требования анимации отхода).

### 2.4 Клиентская сторона (предсказание)

`Ship.Sync`: `_clientDockPhaseActive` снимается, когда реплика доводит фазу до
`None` (там уже есть ветка выхода из фазы с `_hasPrediction = false`) — проверить
на месте, правки может не потребоваться вовсе.

### 2.5 Игрок вне кресла (EVA-пристыкованный)

Статус «пристыкован» (`Player.Docked.cs`) считается каждый шаг из
`LandingPad.HasDockedShip()` → **гаснет сам** при снятии `Docked`, отдельного
RPC не нужно. Игрок внутри зоны при отстыковке теряет ботинки и остаётся
в джетпаке рядом с палубой — приемлемо (П9 уже описывает выход за край зоны).

### 2.6 «Сиротский Docked» (пир ушёл) — что делать с кораблём без пилота

Правило меняется с «Docked не отменяем» на:
- пир ушёл, корабль `Docked`, пилота нет → корабль **остаётся Docked** (как сейчас,
  это физически дешёво и корректно), НО становится доступен новому пилоту:
  сесть в кресло → пилот → F/радиоменю → отстыковка работает (гейт 2.1 по
  `PilotPeerId`, реестр для отстыковки не обязателен).
- правки в `OnPeerLeft` **не требуются** (дедупликация логики обеспечивает 2.2).

## 3. Файлы, которых это касается (scope)

| Файл | Изменение |
|---|---|
| `scripts/core/Docking.cs` | константы `UndockPushSpeed`, `UndockPushOffM` (2 строки) |
| `scripts/net/NetworkManager.Docking.cs` | общий `UndockNow(shipNetId, why)`; `AbortDock` переиспользует его |
| `scripts/entities/Ship.Docking.cs` | `RequestUndock` (клиент-шов) + `RpcUndock` + `AcceptUndockFrom` + выталкивание; шов теста `TestUndockForTest` |
| `scripts/ui/CommlinkMenu.cs` | строка «Отстыковаться» + `OnUndockRequested` (по образцу OptionDockCancel) |
| `scripts/ui/HudLayer.cs` | подключение callback + условие видимости строки (свой корабль в `Docked`) |
| `tests/Stage5Test*.cs` | блок тестов отстыковки (см. §4) |

Не затрагиваются: `Ship.Sync` (скорее всего), `Player.Docked.cs`, codec/снапшоты
(поля уже реплицируются), `Station.cs` (её часть делает `SetDockStateServerOnly`).

## 4. Тесты (шов автотеста, по образцу блока dock Stage5Test)

1. **U1 basic**: docking до `Docked` → `TestUndockForTest` → обе стороны `None`,
   `Freeze == false`, `LinearVelocity` вдоль нормали палубы ≈ `UndockPushSpeed`,
   позиция выше палубы на зазор; `ActiveDockCount == 0`.
2. **U2 гейт не-пилота**: `AcceptUndockFrom` от чужого пира → отказ, состояние
   не изменилось.
3. **U3 не та фаза**: отмена вызова при `Approved` (нет `Docked`) → отказ с
   причиной; путь `RequestDockCancel` не сломан.
4. **U4 без урона**: несколько физкадров после отстыковки → `DamageTakenCount == 0`
   (выталкивание выше цилиндра / скорость ниже порога) — ловит пробел №4.
5. **U5 станция свободна**: после U1 второй корабль делает `TestRequestDock` к той
   же станции → принят (нет `station-busy`).
6. **U6 сиротский Docked**: `OnPeerLeft` пира при `Docked` → состояние осталось;
   новый пилот (`SetPilot`) → `TestUndockForTest` → `None`.

Прогон: `./run-tests.sh` (Stage5Test), `dotnet build` — 0 ошибок.

## 5. Порядок шагов имплементации (после одобрения)

1. `Docking.cs`: константы.
2. `NetworkManager.Docking.cs`: `UndockNow` + рефакторинг `AbortDock`.
3. `Ship.Docking.cs`: RPC-путь (`RequestUndock`/`RpcUndock`/`AcceptUndockFrom`)
   + выталкивание + шов теста.
4. Сборка + smoke: док до `Docked`, отстыковка, скриншот/лог.
5. UI: CommlinkMenu + HudLayer.
6. Тесты U1–U6 в Stage5Test, прогон `run-tests.sh`.
7. `critic`-ревью диффа, запись в `DEVELOPMENT.md` (раздел stage5) и `bugs.md`/
   changelog при необходимости.

## 6. Риски

- **Выталкивание vs физика**: снап позиции внутрь зазора цилиндра должен
  вывести корпус из пересечения; если hull-шейп больше зазора — прицел
  выталкивания поднять (параметр, а не эвристика кадра). Проверка — тест U4.
- **Заказчик мог хотеть «пошаговую» анимацию отхода** (отстёгивается →
  отходит → None). План сознательно её НЕ заводит (ponytail): импульс 3 м/с
  даёт визуально «плавный отход» бесплатно; если понадобится — добавим фазу
  `Undocking` между `Docked` и `None` (значения enum можно дописывать в конец,
  byte-порядок старых не трогается).
- Комм-лог HUD у чистого клиента не узнает об отстыковке — та же известная
  граница, что и у dock-событий («RPC-рассылка событий будет позже»).
