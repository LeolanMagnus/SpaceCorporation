# Баги

Формат: ID, статус (`open` / `fixed`), symptom → cause → fix → как проверено.

---

## BUG-001 — Дебаг-панель уезжает за экран, текст и подпись чекбокса невидимы

- **Статус:** fixed
- **Компонент:** `scenes/DebugLayer.tscn` (DebugLayer, этап 1)
- **Симптом:** при нажатии F3 и открытии панели дебаг-меню: поля тёмные и без текста,
  окно панели выезжает за пределы экрана (за левый край), чекбокс паузы без подписи.
- **Причина:** у `LblDebugInfo` информационный текст (FPS/camera + строки Ship/Player/Net)
  был без переноса — одна строка шириной до ~850 px раздувала минимальную ширину панели
  до 858 px. У `DebugPanel` стояло `grow_horizontal = 0` / `grow_vertical = 0`
  (GROW_BEGIN — рост от правого/нижнего края влево/вверх), поэтому панель шириной 858 px
  при открытии прирастала влево от якоря и уезжала за левый край окна
  (`pos.x = -530` при якоре x=8). За экраном оказывались весь текст, кнопки и подпись
  `Pause world` чекбокса — наружу торчал только тёмный фон панели.
- **Исправление:**
  - `LblDebugInfo`: добавлен `autowrap_mode = 2` (перенос по словам) — минимум по ширине
    упал с 858 px до ~1 px, панель держится за заданные 320 px;
  - `DebugPanel`: `grow_horizontal = 1`, `grow_vertical = 1` — если контента станет
    больше, панель растёт вправо/вниз, а не за левый край.
- **Проверка:** проба `tests/probes/probe_dbgui.gd/.tscn` (геометрия дебаг-UI, оставлена
  как регрессия): до — `panel pos=(-530, 42) size=(858, 258)`; после —
  `panel pos=(8, 44) size=(320, 310)`, метка переносится, `chk text='Pause world'`
  и кнопки `Start Server / Join / Disconnect / Test` с текстами внутри панели.
  `Main.tscn` в headless грузится без ошибок.

---

## BUG-002 — Кнопки дебаг-меню уезжают за нижний край экрана

- **Статус:** fixed
- **Компонент:** `scenes/DebugLayer.tscn` + `scripts/debug/DebugLayer.cs` (DebugLayer, этап 1)
- **Симптом:** при открытии дебаг-панели кнопки (`Start Server / Join / Disconnect / Test`
  и динамические из `RegisterDebugButton`) уходят за нижний край экрана — часть кнопок
  недоступна. Виноват и длинный debug-info текст: он растягивает VBox панели вниз.
- **Причина:** панель `DebugPanel` подстраивала высоту под содержимое (`VBox` без
  ограничения), а в `VBox` поверх кнопок лежал `LblDebugInfo` с многострочным текстом.
  Высота панели = info + чекбокс + все кнопки → при 10+ кнопках или длинном тексте
  панель выходила за нижний край.
- **Исправление:**
  - кнопки перенесены в `ScrollContainer` (`VBox/Scroll/Buttons`) — панель фиксированной
    высоты (offset_bottom = 300, т.е. 256 px), список кнопок скроллится внутри;
  - debug-info вынесена в отдельное окно `DebugInfoPanel` под основной панелью,
    раскрывается toggle-кнопкой «Debug Info» (`toggled → ToggleInfoPanel(bool)` в
    `DebugLayer.cs`); `LblDebugInfo` больше не влияет на высоту панели с кнопками;
  - `ChkPauseWorld` и `BtnDebugInfo` — в одной строке `HBoxTop` над сепаратором;
  - `ClosePanel()` закрывает и инфо-окно, снимая `pressed` с кнопки (иначе при повторном
    открытии панели кнопка осталась бы «нажатой», а окно — скрытым).
- **Проверка:** регресс-проба `tests/probes/probe_dbgui.gd/.tscn`: панель держит
  `size=(320, 256)` при 16 кнопках (`buttons minsize=(100, 556)` скроллятся в
  `scroll size=(320, 213)`); раскрытие «Debug Info» даёт отдельное окно
  `pos=(8, 308) size=(320, 127)` и **не** меняет размер панели (`panel size after
  info=(320, 256)`). `dotnet build` — 0 ошибок; `Main.tscn` в headless грузится без ошибок.

---

## BUG-003 — Смена роли (Start Server) роняет debug-info: ObjectDisposedException на освобождённом Player

- **Статус:** fixed
- **Компонент:** `scripts/core/Player.cs` + `scripts/core/DebugInfoBus.cs` (шина дебага, §3.11 п.9)
- **Симптом:** при нажатии `Start Server` в дебаг-меню отладчик сыплет ошибкой каждый кадр:
  `DebugInfoBus: источник «Player» упал: System.ObjectDisposedException: Cannot access a
  disposed object. Object name: 'SpaceCorporation.Player'` — стек идёт через
  `Player.<_Ready>b__...` → `Node.GetParent()` → `DebugInfoBus.BuildInfoText()` →
  `DebugLayer.UpdateDebugInfo()`.
- **Причина:** `WorldSpawner.OnRoleChanged` при переходе `Offline → Dedicated` зовёт
  `RemoveOfflinePlayer()` → `Player.QueueFree()` (одиночный игрок с авторитетом 1 в сети
  быть не может, §2.4). Но `Player._Ready` регистрирует в `DebugInfoBus` источник текста
  `"Player"` — лямбду, захватившую этот узел (`GetParent()`, `AttachedTo`, `GlobalPosition`),
  и кнопки `Detach Player` / `Attach Player to Seat`. У `Player` **не было `_ExitTree`**,
  поэтому регистрации оставались в `_infoSources` вечно. `DebugInfoBus.BuildInfoText()`
  каждый кадр вызывал мёртвую лямбду → `GetParent()` на освобождённом объекте →
  `ObjectDisposedException`. Тот же латентный баг был у `Ship`: он регистрирует источник
  `Ship#{ShipNetId}` и не снимал его при `DespawnShip` (§3.9 п.16).
- **Исправление:**
  - `Player._ExitTree`: снимает регистрации — `DebugInfoBus.RemoveInfoSource("Player")`
    и (для `_isLocal`) `RemoveDebugButton("Detach Player")` / `RemoveDebugButton("Attach Player to Seat")`,
    **но только при реальном удалении узла** (`if (!IsQueuedForDeletion()) return;`).
    Первая версия снимала их безусловно, и это само по себе было регрессией: `_ExitTree`
    срабатывает и при `Reparent` (пристёжка/отстёжка переносит игрока между точкой и
    мировым корнем, §3.5), а `_Ready` после `Reparent` повторно не вызывается — кнопки
    и источник терялись после первой же пристёжки (обнаружено в `Stage1Test`, шаги 17–19).
    `IsQueuedForDeletion() == true` ровно когда узел помечен `QueueFree` — это и есть путь удаления;
  - `Ship._ExitTree`: снимает `RemoveInfoSource($"Ship#{Net?.ShipNetId ?? 0}")` и
    `RemoveDebugButton("Nudge Ship")` — страховка для путей удаления корабля без обёртки `DespawnShip`
    (Reparent к кораблям не применяется, поэтому безусловная проверка там не нужна);
  - лямбда источника `"Player"` усилена guard'ом `if (!GodotObject.IsInstanceValid(this)) return string.Empty;`
    — защита от гонки «Start+Disconnect в одном кадре», когда отложенный `_ExitTree` старого
    игрока мог бы снять источник свежезаспавненного (QueueFree освобождает узел только в конце кадра).
- **Проверка:** `dotnet build` — 0 ошибок. `./run-tests.sh res://tests/NetTest.tscn` →
  `checks=68 failures=0`; тест п.15 проходит путь `Offline → Dedicated` (удаление одиночного
  игрока) и подтверждает, что источники `Ship#1`/`Ship#2` остаются живы. В выводе прогона
  **нет** `ObjectDisposedException` и строки «источник … упал». `Stage1Test` шаги 17–19
  (кнопки Detach/Attach Player) зелёные.

---

## BUG-004 — Start Server: после удаления одиночного игрока экран гаснет (нет активной камеры)

- **Статус:** fixed
- **Компонент:** `scripts/core/WorldSpawner.cs` + `scenes/Player.tscn` (камера, §3.11 п.10)
- **Симптом:** при нажатии `Start Server` в дебаг-меню мир пропадает из виду — экран
  чёрный/пустой («игрок не видит сцену»), хотя процесс жив и сервер поднялся.
- **Причина:** единственная `Camera3D` во всём проекте живёт внутри `Player.tscn`
  (`current = true`). При переходе `Offline → Dedicated` `WorldSpawner.OnRoleChanged`
  зовёт `RemoveOfflinePlayer()` → `Player.QueueFree()` (одиночный игрок с авторитетом 1
  в сети быть не может, §2.4), и вместе с игроком удаляется его камера. Godot **не**
  выбирает новую `current`-камеру автоматически: в дереве не остаётся ни одной активной
  камеры, viewport теряет источник вида → экран гаснет. Дизайн (§3.11 п.10) прямо
  описывает это состояние: «окно живёт, а игрока нет» — значит мир обязан оставаться видимым.
- **Исправление:** в `WorldSpawner` добавлена запасная мировая камера `ServerCamera`
  (создаётся лениво, кладётся в мировой корень `WorldRoot`, а не под игрока):
  - `OnRoleChanged` при уходе из `Offline` перед `RemoveOfflinePlayer()` снимает мировой
    трансформ камеры последнего локального игрока (`CaptureLocalPlayerCamera`) и после
    спавна мира включает запасную камеру (`ActivateFallbackCamera`, `Current = true`) —
    она встаёт ровно на место камеры игрока, поэтому вид не «прыгает»;
  - при возврате в `Offline` (`Disconnect`) игрок восстанавливается, его камера из
    `Player.tscn` (`current = true`) перехватывает viewport, а запасная гасится
    (`DeactivateFallbackCamera`) — двух `current`-камер не возникает (§3.11 п.4);
  - CLI-dedicated (`--server`) этот путь не проходит: мир спавнится прямо из `_Ready`
    (`RoleChanged` сработал до `Ready` сцены), поэтому запасная камера создаётся только
    при оконном `Start Server` через кнопку — в headless без окна лишняя камера не нужна.
- **Проверка:** `dotnet build` — 0 ошибок. В `tests/NetTest.cs` п.15 добавлен assert
  `GetTree().Root.GetCamera3D() != null` после `StartServer()`. Прогон
  `./run-tests.sh res://tests/NetTest.tscn` → `checks=68 failures=0`, новый assert
  `PASS п.15: после StartServer в viewport есть активная камера (мир виден без игрока)`.
  Камерные assert'ы `Stage1Test` («в корне Main нет самодельных узлов камеры», «камера
  игрока активная») остаются зелёными.

---

## BUG-005 — Stage1Test падает: устаревшие пути панели дебага после переработки BUG-002

- **Статус:** fixed
- **Компонент:** `tests/Stage1Test.cs` (пути узлов панели дебага)
- **Симптом:** `./run-tests.sh res://tests/Stage1Test.tscn` падал с `checks=… failures=…`
  и `Node not found: "DebugRoot/DebugPanel/VBox/LblDebugInfo"` / `".../VBox/Buttons"` +
  `NullReferenceException`. Камерные assert'ы при этом были зелёными.
- **Причина:** после переработки панели дебага (BUG-002) структура сцены изменилась,
  а тест остался на старых путях:
  - `LblDebugInfo` вынесен из `DebugPanel/VBox` в отдельное окно `DebugInfoPanel`;
  - контейнер кнопок `Buttons` пересажен внутрь `ScrollContainer` (`VBox/Scroll/Buttons`);
  - `ChkPauseWorld` переехал в `VBox/HBoxTop/ChkPauseWorld`;
  - детей у `VBox` стало 3 (`HBoxTop`, `Separator`, `Scroll`), а тест ждал `>= 4`.
- **Исправление:** пути в `Stage1Test.cs` приведены к новой структуре сцены
  (`DebugRoot/DebugInfoPanel/LblDebugInfo`, `DebugRoot/DebugPanel/VBox/Scroll/Buttons`,
  `DebugRoot/DebugPanel/VBox/HBoxTop/ChkPauseWorld`); ожидание `VBox детей >= 4`
  заменено на `>= 3` и добавлен assert наличия `ScrollContainer`. Пути совпадают с
  `DebugLayer._Ready` (строки 53–56).
- **Проверка:** `dotnet build` — 0 ошибок; `./run-tests.sh res://tests/Stage1Test.tscn`
  → `checks=104 failures=0`.

---

## FEAT-001 — Модель «игрок нажал Start Server и сам остался играть» (Host)

- **Статус:** done
- **Компонент:** `scripts/core/NetworkManager.cs`, `scripts/core/WorldSpawner.cs`,
  `tests/NetTest.cs`
- **Задача:** раньше `Start Server` поднимал dedicated-сервер и **удалял** локального
  игрока (BUG-003/BUG-004 лечили симптомы: ObjectDisposedException и гаснущий экран).
  Теперь кнопка `Start Server` поднимает сервер и оставляет игрока в игре — процесс
  становится **Host**: сервер + локальный игрок с авторитетом 1 (= id сервера).
- **Что изменилось:**
  - `NetRole` получил значение `Host` (между `Dedicated` и `Client`).
  - `IsServer => Dedicated || Host` (серверная ветка кода общая); добавлен `IsHost`.
    `IsClient`/`HasRealPeer`/`IsActive` не менялись.
  - `StartServer(port, maxPlayers, withLocalPlayer=false)`: `false` → Dedicated
    (CLI `--server`, headless/CI — игрока нет, поведение стадии 0 не тронуто);
    `true` → Host. Кнопка `Start Server` вызывает `withLocalPlayer: true`.
  - Логика спавна вынесена в `SpawnPlayerForPeer(peerId, key)` (общая для клиентов и
    хоста) + `SpawnHostPlayer()` (слот 1) + `CaptureHostCandidateLastSeen()`.
  - `HandleRequestSpawn`: коллизия ключа клиента с ключом хоста (`holder == 1`) —
    хоста НЕ выгоняем, клиент получает гостевой ключ `key#<peerId>` (dev-прогон
    «два окна» с общим `user://identity.cfg`). Проверка идёт ДО ветки вытеснения.
  - `WorldSpawner.OnRoleChanged`: `Offline → Host` = capture LastSeen → SpawnWorld →
    RemoveOfflinePlayer → SpawnHostPlayer (игрок пересоздаётся через MultiplayerSpawner,
    реплика доезжает до клиентов) → DeactivateFallbackCamera (камера игрока current=true,
    запасная не нужна — мигания нет). `Host → Offline` (Disconnect): узел игрока
    остаётся и становится offline-игроком; сетевой слот 1 чистится в `Disconnect()`.
  - `GetNetInfoLine`: `players = peers.Length + (IsHost ? 1 : 0)` (свой игрок не в GetPeers).
- **Проверка:** `dotnet build` — 0 ошибок. `NetTest` расширен фазами п.H
  (`CheckHostStart`/`CheckHostPlayer`/`CheckHostDisconnect`): роль Host, IsServer/IsHost,
  ровно один живой игрок с авторитетом 1, слот 1 в таблицах, активная камера, после
  Disconnect — игрок остался offline-игроком, слот очищен. Итог:
  `NetTest checks=84 failures=0`, `Stage1Test checks=104 failures=0`,
  `run-net-demo.sh` — dedicated-путь цел (`OK: сервер принял пира(ов)`),
  `ObjectDisposedException`/«источник … упал» нет.

---

## BUG-005 — Host жмёт `Detach Player`: «пир 0 просит переместить чужого игрока (авторитет = 1) — отказ»

- **Статус:** done
- **Компонент:** `scripts/core/Player.cs` (кнопки Detach/Attach),
  `scripts/core/DebugInfoBus.cs` + `scripts/debug/DebugLayer.cs` (шина кнопок)
- **Симптом:** после FEAT-001 хост жмёт `Detach Player` от своего игрока (авторитет 1)
  и получает отказ: `Player: пир 0 просит переместить чужого игрока (авторитет узла = 1) —
  отказ`. Плюс при нажатии кнопки из старого (offline) игрока сыпался
  `ObjectDisposedException` (сирота-кнопка с колбэком на освобождённый узел).
- **Причина (две независимые):**
  1. `OnButtonDetach`/`OnButtonAttach` ветвились по `HasRealPeer` и для хоста уходили в
     клиентский RPC `RequestDetach`/`RequestAttach` (`CallLocal=false`). У хоста нет
     клиент→серверного перехода: обработчик отрабатывал НЕ из сети
     (`GetRemoteSenderId() == 0`), а `IsRequestFromOwner` сравнивал 0 с авторитетом 1 и
     отклонял запрос (§2.5 п.2). Хост — это и есть сервер, ему RPC «наверх» не нужен.
  2. `Player` снимал свои кнопки по МЕТКЕ (`RemoveDebugButton("Detach Player")`). При
     Offline→Host старый игрок удаляется `QueueFree` (его `_ExitTree` — в конце кадра),
     а новый игрок в том же кадре регистрирует кнопку с той же меткой. Снятие по метке
     либо не находило кнопку (Godot переименовывает коллизию имени в `@Button@N`), либо
     срезало кнопку НОВОГО игрока; кнопка СТАРОГО оставалась сиротой с колбэком на
     освобождённый узел. Отдельно: если кнопка была зарегистрирована ДО готовности
     `DebugLayer`, она лежала в очереди `_pendingButtons` и экземпляр `Button` вообще не
     был известен владельцу — снять её было нечем.
- **Фикс:**
  1. В `OnButtonDetach`/`OnButtonAttach` RPC шлём ТОЛЬКО настоящему клиенту
     (`NetworkManager.Instance is { IsClient: true }`); хост и offline вызывают
     `DetachServerSide`/`AttachServerSide` напрямую — тот же признак authoritySide, что в
     `_PhysicsProcess`.
  2. `DebugInfoBus.RegisterDebugButton` возвращает `DebugButtonHandle` — ручку на кнопку.
     `Player` хранит ручки и снимает СВОИ кнопки по ним в `_ExitTree`
     (`RemoveDebugButton(handle)`). Ручка работает и для отложенной заявки: снятие убирает
     либо готовый `Button`, либо запись в `_pendingButtons`, не задевая чужую кнопку с той
     же меткой. `RemoveDebugButton(string)` оставлен по-прежнему по имени/метке — он нужен
     `Ship` (у нескольких кораблей законно по своей «Nudge Ship», массовая чистка по метке
     ломала инерцию в `Stage1Test`).
- **Проверка:** `dotnet build` — 0 ошибок. `NetTest` расширен фазой п.H.detach
  (`CheckHostDetachButton`): у хоста ровно ОДНА живая кнопка `Detach Player` (нет мёртвых
  от старого игрока), нажатие открепляет СВОЕГО игрока без отказа. Итог:
  `NetTest checks=84 failures=0`, `Stage1Test checks=104 failures=0` (инерция корабля
  снова зелёная), в логе нет `ObjectDisposedException`, «источник … упал» и
  «просит переместить чужого игрока».

---

## BUG-006 — Тупик модели В3=B: серверное тело корабля заморожено, этапы 4 и 5 невозможны

- **Статус:** in progress — решается в стадии 2, шаг 2.3 (переход на В3=A,
  см. `stage2-plan.md` §0, В14). Пока код не изменён.
- **Компонент:** `scripts/core/Ship.cs` (`ApplyAuthority`, `RpcReportNetStateToServer`),
  `scripts/core/NetworkManager.cs` (`AssignShipAuthority`)
- **Симптом:** при живом пилоте сервер НЕ интегрирует корабль: `Ship.ApplyAuthority`
  ставит `FreezeMode = Static` всем, кто не авторитет, а авторитет при В3=B — пир-пилот,
  то есть на сервере тело заморожено всегда, кроме момента, когда пилота нет.
  Позиция корабля на сервере живёт только в `NetState` (её присылает пилот через
  `RpcReportNetStateToServer`).
- **Причина:** выбор В3=B из rework-плана §4. Он корректен сам по себе, но делает
  сервер наблюдателем, а не симулятором. Дальше по плану идут этапы, которым нужен
  именно симулятор: этап 4 (сервер сам считает урон от столкновений — нужен контакт
  на теле, которое сервер интегрирует) и этап 5 (пружина стыковки через
  `CustomIntegrator` — физически не работает на замороженном теле).
  Второе следствие: канал `RpcReportNetStateToServer` противоречит
  `prototype-plan.md` §0 («Сервер никогда не доверяет клиентским позициям») —
  клиент шлёт позицию напрямую.
- **Исправление (стадия 2):** авторитет тела `Ship` всегда `1`; сервер интегрирует
  из входных кадров пилота; `AssignShipAuthority` → `SetPilot` (меняет только
  `PilotPeerId`, авторитет не трогает); `RpcReportNetStateToServer` и гейт по
  `_lastAcceptedReportTick` удаляются; отзывчивость пилота обеспечивается
  предсказанием и reconciliation, а не передачей авторитета тела.
- **Проверка:** `tests/Stage2Test.tscn` (детерминизм интегратора, сервер применяет
  ввод пилота и отклоняет чужой, откат/переигровка), `NetTest` (после `SetPilot`
  `PilotPeerId` меняется, а авторитет тела остаётся `1`).

---

## BUG-007 — NetTest: ERROR «Unable to auto-spawn node with reserved name: @CharacterBody3D@N»

- **Статус:** open (шум, на результаты assert'ов не влияет; починить в стадии 2,
  попутно с правками спавна)
- **Компонент:** `scripts/core/WorldSpawner.cs` (`SpawnPlayerAt`), `MultiplayerSpawner`
- **Симптом:** в логе `NetTest` (фазы п.8) трижды печатается
  `ERROR: Unable to auto-spawn node with reserved name: @CharacterBody3D@10. Make sure
  to add your replicated scenes via 'add_child(node, true)' to produce valid names.`
  со стек-трейсом; при этом все assert'ы фазы зелёные.
- **Причина:** `SpawnPlayerAt` делает `AddChild(player)` (без `force_readable_name`)
  и не задаёт узлу человекочитаемое имя, в отличие от спавна корабля, где есть
  `ship.Name = nameOverride`. `MultiplayerSpawner` при автоспавне на удалённой стороне
  не может дать узлу безымянного корня сцен-обёртки валидное имя и резервирует
  `@CharacterBody3D@N`.
- **Исправление:** задавать игроку имя до `AddChild` (по аналогии с кораблём:
  `Player#{peerId}` или по `playerKey`), либо `AddChild(player, true)`.
- **Проверка (когда будет сделано):** в логе `./run-tests.sh res://tests/NetTest.tscn`
  нет строки `reserved name`; `checks=84 failures=0` не деградирует.

---

## BUG-008 — Второй корабль реагирует на ввод игрока, хотя игрок к нему не пристёгнут

- **Статус:** open (зафиксирован, исправляется отдельно)
- **Компонент:** `scripts/core/Ship.cs` (`IsLocalPilot`, `ApplyAuthority`,
  `CollectInputFrame`), `scripts/core/Player.cs` (`AttachServerSide`),
  `scripts/core/NetworkManager.cs` (`SetPilot`, `SelfId`)
- **Симптом:** в offline-сцене (`Main.tscn`, два корабля) игрок пристёгнут только к
  одному кораблю, но WASD/мышь двигают ОБА корабля одновременно.
- **Причина:** право пилотировать определяется как `Ship.IsLocalPilot =>
  PilotPeerId == NetworkManager.SelfId`. Значение по умолчанию у каждого корабля —
  `PilotPeerId = 1` (автопилот/сервер), а в offline `SelfId == 1`
  (`NetworkManager.LocalAuthorityId` возвращает 1 без peer'а). Значит
  `IsLocalPilot == true` для ВСЕХ кораблей сразу: каждый вызывает
  `SetProcessInput(true)` и в `_IntegrateForces`/`FrameForStep` читает один и тот же
  глобальный клавиатурный ввод (`CollectInputFrame` → `Input.IsActionPressed`).
  Пристёжка к кабине в offline НЕ меняет `PilotPeerId`: ветка `nm.SetPilot(...)` в
  `Player.AttachServerSide` обёрнута в `if (NetworkManager.Instance is { IsServer: true })`,
  а offline-роль — НЕ `IsServer` (`IsServer => Dedicated || Host`). Поэтому право
  управления вообще не привязано к факту «игрок сидит в этом кресле».
- **Исправление (предлагаемое):** в offline привязывать пилота к кораблю по факту
  пристёжки игрока, а не держать `PilotPeerId = 1` у всех. Варианты:
  - в `AttachServerSide`/`DetachServerSide` вызывать смену пилота и для offline
    (расширить гейт с `IsServer` на «авторитетная сторона = сервер ИЛИ offline»,
    тот же признак `authoritySide`, что в `Player._PhysicsProcess` и
    `OnButtonAttach/Detach`), при этом различать «офлайн-игрок» и «автопилот»,
    например отдельным значением `PilotPeerId = 0` для «никто не пилотирует»;
  - и/или гейт `IsLocalPilot` дополнить условием «к этому кораблю пристёгнут
    локальный игрок», чтобы `SetProcessInput` включался только у пристёгнутого.
- **Проверка (когда будет сделано):** в `Main.tscn` (offline, 2 корабля) WASD
  двигает только пристёгнутый корабль; второй стоит/дрейфует. В `Stage1Test`/`NetTest`
  не деградирует.

---

## BUG-009 — После открепления от корабля игрок продолжает им управлять (корабль должен дрейфовать)

- **Статус:** open (зафиксирован, исправляется отдельно; общий корень с BUG-008)
- **Компонент:** `scripts/core/Player.cs` (`DetachServerSide`),
  `scripts/core/Ship.cs` (`IsLocalPilot`, `ApplyPilot`)
- **Симптом:** игрок открепляется (`Detach Player`) от корабля, но продолжает
  гонять его WASD/мышью; корабль не переходит в свободный дрейф.
- **Причина:** `Player.DetachServerSide` возвращает пилота автопилоту
  (`nm.SetPilot(shipNetId, peerId: 1)`) только внутри
  `if (ship != null && ship.PilotPeerId == playerPeer && NetworkManager.Instance is
  { IsServer: true } nm)`. В offline роль НЕ `IsServer`, поэтому `SetPilot` не
  вызывается. Более того, в offline `PilotPeerId` корабля и так всегда `1`
  (пристёжка его не меняла — см. BUG-008), а `SelfId == 1`, поэтому
  `IsLocalPilot` остаётся `true` и после отстёжки: управление не снимается.
  Отстёжка в offline меняет только дерево (`Detach()` → Reparent в мировой корень),
  но не право пилотировать.
- **Исправление (предлагаемое):** то же, что в BUG-008 — в offline при отстёжке
  снимать пилота с корабля (переводить в «никто не пилотирует» / автопилот), и
  `IsLocalPilot` должен становиться `false`, чтобы `SetProcessInput(false)` и
  `SetPhysicsProcess` убрали локальный ввод; замороженное/автопилотное тело тогда
  просто дрейфует по инерции.
- **Проверка (когда будет сделано):** в offline: пристегнуться → корабль
  слушается; открепиться → `IsLocalPilot == false`, WASD не действует, корабль
  движется по инерции (`|v|` сохраняется, тяга = 0). Регресс в `Stage1Test`.

---

## BUG-010 — Q/E вращают корабль вокруг вертикальной оси Y (рыскание), а нужен крен вокруг продольной оси Z

- **Статус:** open (зафиксирован, исправляется отдельно)
- **Компонент:** `project.godot` (`[input]` `torque_yaw_left/right` = Q/E),
  `scripts/core/InputFrame.cs` (`InputMask.TorqueYawLeft/Right`,
  `AxisYawLeft/Right`), `scripts/core/FlightModel.cs` (`Step`, `localTorque`)
- **Симптом:** клавиши Q/E заставляют корабль вращаться вокруг мировой вертикали
  (рыскание, ось Y), тогда как по замыслу Q/E — это крен вокруг продольной оси
  корабля (локальная ось Z, направление «вперёд» = −Z).
- **Причина:** раскладка и модель задают Q/E как РЫСКАНИЕ по оси Y. В
  `project.godot` Q=`torque_yaw_left`, E=`torque_yaw_right`. В
  `FlightModel.Step` момент собирается так:
  `localTorque = (AxisPitchUp−AxisPitchDown, AxisYawRight−AxisYawLeft, 0f)` —
  то есть X = тангаж, Y = рыскание, а **Z (крен) всегда 0**. Отдельного действия и
  битов под крен (`torque_roll_*`, `InputMask.TorqueRoll*`, `AxisRoll*`) в проекте
  нет вовсе. Поэтому Q/E физически не могут дать вращение вокруг Z.
- **Исправление (предлагаемое):** либо переназначить Q/E на крен и завести
  отдельные клавиши под рыскание, либо (минимально) переименовать/переназначить
  действия так, чтобы Q/E управляли компонентом Z в `localTorque`:
  - добавить в `InputMask` биты `TorqueRollLeft/Right` и оси `AxisRollLeft/Right`
    (+ `FromMask`/`Merge`/сериализация, тест п.3 на размер кадра),
  - в `FlightModel.Step` поставить крен в Z: `localTorque.Z = AxisRollRight −
    AxisRollLeft` (знак — под «вперёд = −Z»),
  - в `project.godot` привязать Q/E к действиям крена, а рыскание — на другие
    клавиши; обновить гейт `IsInteractionMode` в `Ship.CollectInputFrame`.
- **Проверка (когда будет сделано):** проба/тест: удержание Q/E даёт
  `AngularVelocity` вдоль локальной Z корпуса (продольной), а не мировой Y;
  тангаж (Z/X) и рыскание (новые клавиши) работают по своим осям. `dotnet build`
  без ошибок, тесты стадии не деградируют.

---

## OBS-001 — Долг стадии 2: кадры ввода шли `Reliable, Channel 1`, канал 2 был объявлен и пуст

- **Статус:** fixed (закрыт рефакторингом неткода, шаг R8 плана `net-refactor-plan.md`
  = п.3.6 плана `stage3-plan.md`; фиксируется на шаге 3.0 стадии 3, потому что именно
  в плане стадии 3 этот долг был записан как задача)
- **Компонент:** `scripts/core/Ship.cs` (`RpcSubmitInput`), `scripts/core/NetworkManager.cs`
  (`ChannelCount`), `scripts/core/SnapshotBroadcaster.cs`
- **Симптом:** `NetworkManager` объявляет 3 канала (`ChannelCount = 3`), но по факту
  весь трафик (ввод, снапшоты, события) шёл по каналу 1 как `Reliable`. Кадр ввода —
  это состояние, которое перекрывается следующим кадром; надёжная доставка при потерях
  собирает очередь устаревших команд, и «команда приходит через 400 мс» хуже, чем
  «команда потеряна». При этом канал 2 был пуст — бюджет unreliable-транспорта
  фактически не использовался.
- **Причина:** транспорт ввода выбран на ранней стадии (до появления батчей снапшотов),
  когда ещё не было аварийного репорта состояния; перевод на `Unreliable` откладывался,
  чтобы не шатать пройденную реконсиляцию без замещающего пути доставки состояния.
- **Исправление:** `Ship.RpcSubmitInput` — `[Rpc(AnyPeer, CallLocal = false,
  TransferMode = Unreliable, TransferChannel = 2)]` (фактически `Ship.cs:992-993`).
  Условие безопасности выполнено: состояние пилота теперь приходит батчем
  (`SnapshotBroadcaster`, тоже канал 2) и аварийным репортом (`RpcRequestFullSnapshot`,
  надёжный канал 1), поэтому потерянный кадр ввода не оставляет пилота без эталона
  (шаги R3–R5 сделаны раньше R8).
- **Проверка:** `./run-tests.sh res://tests/NetTest.tscn` → `checks=123 failures=0`
  (реконсиляция, `predErr`, `hardRollbacks` без регресса);
  `./run-tests.sh res://tests/Stage2Test.tscn` → `82 OK, 0 FAIL`;
  `LATENCY=100 ./run-net-demo.sh 2` — `predErr` не хуже таблицы §2.6,
  `LastAcceptedInputTick` продолжает расти (кадры идут, несмотря на потери).

---

## BUG-011 — ERROR в логе Stage1Test: `RPC 'RpcRequestFullSnapshot' on yourself is not allowed by selected mode`

- **Статус:** open (зафиксирован при базовом прогоне стадии 3, к стрельбе отношения не имеет)
- **Компонент:** `scripts/core/SnapshotBroadcaster.cs` (`ClientGapWatch`, строка 140),
  `scripts/core/Ship.cs` (`RpcRequestFullSnapshot`)
- **Симптом:** в прогоне `./run-tests.sh` (Stage1Test) в выводе появляется
  `ERROR: RPC 'RpcRequestFullSnapshot' on yourself is not allowed by selected mode.`
  со стеком `SnapshotBroadcaster.ClientGapWatch` → `_Process`. Тест при этом зелёный
  (`checks=104 failures=0`) — ошибка не валит assert'ы, но засоряет лог и означает,
  что пир шлёт RPC «сам себе» в режиме, где это запрещено.
- **Причина:** сторож `ClientGapWatch` заводится на пире, который ждёт снимки своего
  корабля, и по порогу 0.5 с шлёт `RpcId(peer, nameof(RpcRequestFullSnapshot), …)`.
  В конфигурации, где ожидающий пир и есть адресат (single-process/`Offline`-подобный
  стенд, `self == 1` и сервер = 1), `RpcId` на собственный peer_id запрещён режимом
  `MultiplayerApi` — вместо локального вызова нужен прямой вызов метода.
- **Исправление (предлагаемое):** в `ClientGapWatch` перед `RpcId` гейт
  `if (peer == Multiplayer.GetUniqueId()) { <прямой серверный путь запроса>; return; }`
  (или общий хелпер `SendToPeerOrSelf`), плюс assert в тесте, что в выводе прогона нет
  этой строки.
- **Проверка (когда будет сделано):** `./run-tests.sh` — в stdout нет
  «on yourself is not allowed»; `grep -c "RpcRequestFullSnapshot" log` = 0 ошибок;
  тестовый assert «пир получает полный снимок и в single-process конфигурации».

---

## OBS-002 — Автопилот стенда разгоняет корабль: гейт скорости читает `_predicted` там, где он неактуален

- **Статус:** fixed (стендовый гейт по роли, шаг перед 3.1; пересдача §2.6 на 3 игроках — шаг 3.9).
  Фикс в `Ship.cs` (`_PhysicsProcess`, гейт автопилота): added condition
  `NetworkManager.Instance?.Role != NetRole.Dedicated`. Замер после фикса
  (`LATENCY=100 ./run-net-demo.sh 2`, 2 прогона): `hardRollbacks` пилота стабилен
  **3–4 за 14–22 с** (было 21…79) ≈ 0,2/с — критерий (б) ≤1/с выполнен с запасом;
  `predErr` установившегося полёта 0,05…0,24 м (пик окна 0,305 — bang-bang стенда на
  редких доворотах рысканьем, см. примечание ниже), `LastAcceptedInputTick` растёт.
  `maxHullDelta ≈1 м` у стороннего наблюдателя НЕ изменился — это долг §2.7 (шаг 3.9),
  не относится к OBS-002.
  - **Почему фикс по роли, а не (A)/(C):** на dedicated-сервере локальный пир `SelfId=1`
    никогда не является целевым пилотом в бою; сервер обязан брать ввод из очереди кадров
    пилота (`FrameForStep`), а не из автопилота. Дефолт `PilotPeerId = 1` (Ship.cs:42) +
    `SelfId = 1` (dedicated) делали `IsLocalPilot` ложно-true у бесхозного корабля до
    attach клиента — сервер гонял `ThrustF` каждый физшаг ~8 с (трения в космосе нет) и
    уводил корабль на ~950 м ещё ДО первого снимка пилоту; первая реконсиляция давала
    `err ≈ 955 м` → жёсткий откат → резонанс регулятора с частотой снимков.
- **Компонент:** `scripts/core/Ship.cs` — автопилот стенда (`AutopilotMask`, ~строка 711),
  `_predicted` (строка 217), серверный `_PhysicsProcess` для «бесхозного» корабля
- **Симптом:** перепроверка критериев §2.6 (`LATENCY=100 ./run-net-demo.sh 2`) даёт
  разброс от прогона к прогону: `hardRollbacks = 21…79 за 22 с` (порог ≤1/с),
  `predErr` пилота в полёте 0,3…2,4 м (порог 0,3 м). Диагностика `[recon ship#N]`
  (пишется при `err > 0,3`) показывает первую сверку уже с `err ≈ 955 м`:
  `serverPos.z ≈ -961` при клиентском предсказании `(0, 0, -6)`, `appliedTick = 0`.
  Серверный корабль улетает в ~10 раз быстрее крейсерской цели стенда (6 м/с).
- **Причина (три слоя):**
  1. Гейт автопилота берёт скорость из `_predicted.LinearVelocity.Length()`, но `_predicted`
     корректен только у пилота-клиента. Сервер до пристёгивания пилота ведёт «бесхозный»
     корабль как `IsLocalPilot` (`PilotPeerId = 1`) — там `_predicted` ведётся, но
     после пристёгивания серверная симуляция идёт из очереди кадров, а `_predicted`
     сервера по чужому кораблю не обновляется; при сбросе `_predicted`/первой реплике
    Bang-bang-регулятор (тяга если `speed < 6`, тормоз если `speed > 9,6`, без D-составляющей)
     входит в резонанс с частотой снимков 30 Гц и порождает пачки откатов.
  2. Пока клиент подключается (~8 с на стенде), сервер жмёт `ThrustF` каждый физшаг:
     в космосе трения нет, скорость растёт линейно (≈120 м/с к моменту attach).
  3. Первый снимок пилота приходит с RTT 100 мс, серверный корабль уже в сотнях метров —
     предсказание пилота стартует из точки спавна → гарантированный жёсткий откат,
     а дальше каждый дрейф в субкадр при скорости >30 м/с пробивает `ReconcileThreshold = 0,3`.
- **Это НЕ регрессия рефакторинга:** на замерах стадии 2 та же механика давала «7 откатов
  за 22 с», потому что спавн/attach успевали раньше разгона; порядок таймеров меняет исход
  (21 vs 79). Код гейта идентичен в 93331c7 и 9ecced4 (`diff` автопилот-блока пуст).
- **Кандидаты фикса (шаг 3.9):** (A) сервер-авторитет читает скорость из тела
  (`LinearVelocity`), а не из `_predicted`; (B) автопилот сервера молчит, пока
  `AcceptedInputCount == 0` (не гонять бесхозный корабль); (C) первый снимок = стартовая
  точка предсказания пилота (спавн в позицию серверного тела).
- **Проверка (когда будет сделано):** `LATENCY=100 ./run-net-demo.sh 2` ≥ 2 прогона:
  `hardRollbacks ≤ 22` (≤1/с) и `predErr ≤ 0,3 м` в установившемся полёте;
  первая строка `[recon ship#N]` (если появится) с `err` того же порядка, что в полёте.

## OBS-003 — Голодание интерполяционного буфера наблюдателя при ≥3 кораблях (долг §2.7, шаг 3.9)

- **Статус:** fixed (шаг 3.9, стадия 3). Метрика: адаптивный `InterpDelay` + штамп
  снимка по журнал-времени доставки.
- **Симптом:** `LATENCY=100 ./run-net-demo.sh 3` — у стороннего наблюдателя
  `maxHullDelta` до ~1.9 м (в худших прогонах §2.7 — до 3 м) при пороге §2.6 (в)
  < 0.15 м; `interpBuf` проседал до 1–3 снимков (порог ≥2). На 2 игроках (стенда
  §2.6) в плохих окнах тоже проседает до 3. Пилота не касается (у пилота откат,
  не интерполяция).
- **Причина (два слоя, оба в `Ship.cs` клиент-наблюдатель):**
  1. **Штамп по `now`.** `DrainRecvBufferForObserver` клал снимок в `_interpBuf` со
     временем `now`. После сетевой заминки (RTT≈200 мс + разделение потока на 3
     игрока) за один кадр «дозревает» ПАЧКА снимков; все они получают одну и ту же
     отметку → вырожденные сегменты с нулевым интервалом, hull перескакивает на весь
     путь пачки за один кадр (скачок = весь пройденный за голодный зазор путь).
  2. **Фиксированная задержка.** `InterpDelay = 0.1` при 3 игроках меньше фактического
     периода приёма снимков (~0.25 с/корабль: 3 потока × 30 Гц делят пропускную
     способность канала + очередь задержки). render-момент `now − delay` выезжает за
     самый свежий снимок → картинка мёрзнет в последнем снимке, а с приходом
     следующего прыгает назад в сегмент с α≈0.6 (скачок 1.0–1.9 м).
- **Фикс:**
  1. Снимок штампуется ЖУРНАЛЬНЫМ временем доставки `DueSec` (из буфера приёма), а
     не `now` — пачка сохраняет ровный шаг приёма, вырожденные сегменты и скачок
     пачки исчезают. Окно буфера держит `≥2` снимка и режется по
     `2.5 × max(InterpDelay, _interpDelay)`.
  2. Адаптивная задержка (`AdaptInterpDelay`, ОДИН пересчёт за кадр, не за снимок):
     `target = clamp(2 × наихудший_недавний_зазор, 0.1, 0.6)`, где зазор — EMA(0.4)
     шага между снимками + пик-холд с медленным спадом (×0.995), чтобы единичный
     быстрый зазор не ронял задержку (иначе render снова выезжает за свежий снимок →
     самоподдерживающаяся осцилляция). Первый замер — bootstrap (ставим сразу в
     цель, без рампы), дальше гистерезис с мёртвой зоной: реагируем вверх при
     |Δ|>0.05 с шагом 0.06/кадр, вниз — при |Δ|>0.15 шагом 0.02 (голодание лечим
     быстро, перерасход снимаем осторожно). Скачок самой задержки сам по себе
     сдвигает hull (v×Δdelay), поэтому рост/спад ограничены по шагу.
- **Почему метрика не «читерит» тесты:** шов `TestPushSnapshot` (NetTest п.13/п.18)
  кладёт снапшот мимо дренажа → `_adaptPending` не выставляется, адаптив не
  включается, `InterpDelaySec` остаётся ровно 0.1 — assertion'ы п.13/п.18 целы.
  Гейт скорости автопилота стенда (OBS-002, `TargetSpeed = 6 м/с`) держит
  калибровочный режим §2.6(v): без него плавное движение на ~27 м/с даёт ~0.9 м/кадр
  само по себе и порог 0.15 недостижим без «резинга».
- **Замер (после фикса):** `LATENCY=100 ./run-net-demo.sh 3` (24 с) — у стороннего
  наблюдателя `maxHullDelta = 0.066…0.125 м` (< 0.15), `interpBuf = 6` устойчиво
  (≥2, не опустошается), `interpDelay → 0.57–0.58`, `snapPeriod ≈ 0.25`. Регресс
  2 игроков: `maxHullDelta ≈ 0.06`, `interpBuf = 6` (было проседание до 3). Пилот
  (§2.6 а/б) не затронут: `hardRollbacks` кумулятивный 22→23/24 с (≤1/с),
  `predErr` в таблице §2.6.
- **Регресс-наборы:** Stage1 104/0, Stage2 82/0, Stage3 78/0, NetTest 123/0;
  CodecTest 103046/0 (R1), BroadcasterTest 27/0 (R3), SpatialIndexTest 25/0 (R2),
  StandTest R9 11/0.

## OBS-004 — `IntersectShape` из C# не отдаёт `depth`/`normal`/`point` для станции (Этап 4, шаг 4.4/4.7)

- **Статус:** fixed (обходной путь зафиксирован, стадия 4). Кандидат из плана 4.12
  («`ComputePenetration` … возвращает ноль или неверную нормаль — проверить пробом
  до шага 4.7») подтвердился в другой форме.
- **Симптом:** проба `tests/probes/ProbeStationPen.tscn` (Godot 4.7, C#):
  `space.IntersectShape` по сферам возвращает словарь только с ключами
  `rid, collider_id, collider, shape` — `depth`, `direction`, `normal`, `point`
  отсутствуют (`depth=<нет>`). Для статического `StaticBody3D` толерантный контакт
  из C#-биндинга оценить нельзя.
- **Причина:** в C#-биндинге `PhysicsShapeQueryParameters3D.Margin = 0.0` +
  `CollideWithBodies` для статики не заполняет поля глубины; документированного
  эквивалента `ComputePenetration` (устаревший `compute_contacts`) в 4.x C# нет.
- **Фикс (обход):** в `Ship.HandleStationContacts` геометрия контакта считается
  аналитически: внешняя нормаль = `shipPos - station.GlobalPosition` (для цилиндра
  станции этого достаточно для знака `vn` и отскока; вырожденный случай d≈0 —
  нормаль от носа корабля). Урон/отскок от формы проникновения не зависят —
  только от `vn` (`CollisionDamage.ApproachSpeed`). Глубина проникновения не
  используется, поэтому отсутствие `depth` на результаты не влияет.
- **Как проверено:** Stage4Test п.17–п.20 (детерминированный таран: 40 м/с →
  `Hp=44`, отскок `vel.X<0`; 2 м/с — урона нет, но отскок есть; кулдаун; god-режим
  — урон 0 при сохранённом отскоке); проб `ProbeStationPen` остаётся в репозитории
  как регресс-документация поведения биндинга.
