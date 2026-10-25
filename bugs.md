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
  `NetTest checks=79 failures=0`, `Stage1Test checks=104 failures=0`,
  `run-net-demo.sh` — dedicated-путь цел (`OK: сервер принял пира(ов)`),
  `ObjectDisposedException`/«источник … упал» нет.
