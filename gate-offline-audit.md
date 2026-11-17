# Аудит гейтов `IsServer` — поведение в Offline

Дата: аудит после фиксации BUG «захват не работает в offline» (П7-фикс).
Запрос: проверить ВСЕ места, где стоит гейт вида `IsServer` (серверная логика),
и определить поведение в роли `Offline`; где найдутся проблемы — план исправления.

## Методика

Эталон класса бага (найден и исправлен ранее): гейт `IsServer == true` для логики,
которая в одиночной игре обязана выполняться локально. В роли `Offline`
`IsServer == false` (Role ≠ Dedicated/Host), и гейт молча отключает механику.
Правильный предикат для «серверной» логики процесса — `IsAuthoritySide`
(= `IsServer || Offline`, NetworkManager.cs:98) либо явный признак реального
соединения `HasRealPeer`.

Поиск: `grep -rn "IsServer" scripts tests --include=*.cs` — 50 совпадений
(из них ~15 — тесты/комментарии). Каждое оставшееся проверено вручную.

## Сводка: 4 категории оставшихся гейтов

### A. RPC-приёмники «только сервер» — в offline недостижимы, БАГОВ НЕТ

Эти гейты защищают приёмник RPC, который в offline физически не вызывается:
отправитель отсутствует (RPC «сам себе» в offline запрещён), а локальные
вызывающие обходят RPC прям серверным путём.

| Место | Гейт | Поведение в offline |
|---|---|---|
| `Player.Attach.cs:143` `RequestAttach` (RPC) | `is not { IsServer: true }` → PrintErr | Недостижим: `OnButtonAttach` (Player.cs:450) шлёт RPC только при `IsClient`; offline/хост зовут `AttachServerSide` напрямую. **Латентная несогласованность** — см. план П1. |
| `Player.Attach.cs:159` `RequestDetach` (RPC) | тот же гейт | Недостижим: `OnButtonDetach` (Player.cs:462) и `OnDetachOrSeatPressed` (Player.Eva.cs:~370) используют ветку `IsClient` / прямой `DetachServerSide`. То же — см. П1. |
| `Ship.Net.cs:730` `AcceptInputFrom` | `is not { IsServer: true }` | Недостижим из игры: `SubmitInputToServer` (Ship.Net.cs:~588) делает early-return `if (not { HasRealPeer: true })` (BUG-011). Offline-пилот интегрирует предсказание локально. Тестовый шов `TestSubmitInput` на роли Offline отклоняется — тесты поднимают Dedicated (Stage5/Stage7 так и делают). Ок. |
| `Ship.Combat.cs:602` `AcceptFireFrom` | `is not { IsServer: true }` | Недостижим из игры: `TryFire` (Ship.Combat.cs:~535) в offline идёт локальной авторитетной веткой (`nm.IsAuthoritySide` → очередь `_pendingFires`), RPC не шлётся. Шов `TestFire` на Offline отклоняет — тесты используют Dedicated. Ок. |
| `NetworkManager.Identity.cs:237` `HandleRequestSpawn` | `if (!IsServer)` | Дизайн: спавн игроков ≥2 — только настоящий сервер; offline-игрок спавнится `StartSinglePlayerWorld`, этот путь не трогает. Ок. |
| `SnapshotBroadcaster.cs:511` `RpcSnapshotBatch` | `IsServer == true` → return | Приёмник на клиенте; offline RPC не летают. Ок. |
| `SnapshotBroadcaster.cs:568` `RpcRequestFullSnapshot`-приём | `nm == null \|\| !nm.IsServer` → return | Offline не вызывается: клиентский сторож `ClientGapWatch` гейтится `HasRealPeer` (BUG-011). Ок. |
| `Ship.Net.cs:408` `RpcTogglePilotAssistRpc` | гейт по отправителю `sender != PilotPeerId` | В offline недостижим из сети; `RequestTogglePilotAssist` при `HasAuthority` применяет сразу. Ок. |

### B. Сетевые циклы с `HasRealPeer` — offline исключён по построению, БАГОВ НЕТ

| Место | Предикат |
|---|---|
| `SnapshotBroadcaster.cs:124/126` `_Process` | `IsServer && HasRealPeer` / `!IsServer && HasRealPeer` — offline не попадает ни в одну ветку (фикс BUG-011). |
| `NetworkManager.cs:476` `OnPeerConnected` | `IsServer && id >= 2` — сигнала PeerConnected в offline не бывает. |
| `Ship.cs:765` страж авторитета корабля | `is { HasRealPeer: true } nm && nm.IsServer` — в offline не срабатывает (предупреждение только для реальной сети). |

### C. Отображение/статистика — не логика, БАГОВ НЕТ (1 косметика)

| Место | Поведение в offline |
|---|---|
| `HudLayer.cs:750–751` | Тег роли: offline рисует «OFFLINE», счётчик пиров скрыт. Ок. |
| `NetworkManager.Stats.cs:192` | `players = IsServer ? … : (IsClient ? 1 : 0)` → в offline показывает **0 игроков** вместо 1. Только дебаг-панель/лог. См. П2 (косметика). |

### D. Логика процесса с корректным offline-обходом — ок

| Место | Почему ок |
|---|---|
| `Player.cs:286/303` | `nm == null \|\| !HasRealPeer \|\| IsServer` → offline = авторитетная сторона. Предикат намеренный. |
| `Player.Eva.cs:208` | `nm == null \|\| !HasRealPeer \|\| IsServer` → offline = авторитет. Ок. |
| `WorldSpawner.cs:93` | `if (nm.IsServer) SpawnWorld();` — для offline есть отдельная ветка строкой ниже (`Role == Offline → StartSinglePlayerWorld`). Переходы ролей — `OnRoleChanged`. Ок. |
| `WorldSpawner.Spawn.cs` SpawnTarget/ClearTargets | Уже исправлены: гейт по роли (`IsClient` → отказ), offline разрешён (комментарии «Гейт по РОЛИ, а не по IsServer»). |
| `ShipHealthState.cs:171/183/191` | `IsServerAuthority` — псевдоним `HasAuthority` (владелец узла), к роли процесса отношения не имеет. В offline авторитет корабля = локальный (1). Ок. |
| Уже переведено на `IsAuthoritySide` | `Ship.Docking.cs` (TickAlignCapture, TickDockPhases, AcceptDockFrom, Dock*/Test-шов), `Player.Attach.cs:257` AttachServerSide (BUG-008/009), `Station.cs:121` SetDockStateServerOnly, `NetworkManager.Docking.cs`, `LandingPad`/Commlink — вся механическая цепочка стыковки. |

## Итог проверки

**Блокирующих offline-багов не найдено.** Класс бага (чистый `IsServer` на
локально-обязательной логике) полностью вычищен: последнее вхождение было в
цепочке захвата стыковки (исправлено в П7-фикс), в WorldSpawner-кнопках
(исправлено ранее), в назначении пилота (BUG-008/009).

Найдены только латентные/косметические несогласованности:

## План исправления

### П1 (низкий приоритет, защита от регресса): нормализовать гейты RPC-приёмников attach/detach

`Player.Attach.cs:143` и `:159` — гейт `is not { IsServer: true }` формально
противоречит конвенции `IsAuthoritySide` для «серверной» сердцевины. Сейчас
безопасно (offline туда не попадает), но будущий вызовов из offline молча
упрётся в отказ `IsRequestFromOwner` (GetRemoteSenderId()==0 ≠ авторитет 1)
или в PrintErr. Варианты:
1. Минимальный (рекомендую): оставить гейт, добавить к нему комментарий-инвариант
   («в offline недостижим: вызывающие обходят RPC прямой серверной веткой») —
   фактически он уже есть в комментариях вызывающих, перенести на приёмник.
2. Альтернатива: гейт `is not { IsAuthoritySide: true }`; в offline RPC «сам себе»
   запрещён Godot, так что приёмник так и не вызовется — поведение не изменится,
   ноPrintErr перестанет врать про «не сервер».
Тестов не меняется; проверка — Stage1/5/7 green.

### П2 (косметика): счётчик игроков в дебаг-статистике

`NetworkManager.Stats.cs:192`: в Offline показывать 1 (локальный игрок),
например `players = Role switch { Offline => 1, ... }`. Только строка
дебаг-панели/лога; риск нулевой. Тесты на строку статистики не зависят.

### П3 (дисциплина, без правок кода): конвенция в комментариях

Зафиксировать правило в коде (уже частично сделано): «гейт серверной по смыслу
логики — `IsAuthoritySide`; гейт сетевой доставки — `HasRealPeer`; чистый
`IsServer` допустим только в RPC-приёмниках, недоступных в offline по
построению отправителя». При новых RPC — обязательный комментарий о поведении
в offline. Опционально: guard-тест, вызывающий `TestSubmitInput`/`TestFire`/
`TestRequestDock` на роли Offline и ожидающий корректное поведение (шовы
специально завязаны на Dedicated — зафиксировать это комментарием у швов).

## Порядок выполнения (если принимать план)

1. П1 вариант 1 (комментарии-инварианты у 2 приёмников) — 2 строки.
2. П2 (1 выражение в Stats) — 1 строка.
3. П3 — комментарии у тестовых швов.
4. Прогон: `dotnet build` (0 ошибок) + Stage5 (149/0) + Stage7 (153/0) + Stage1
   (3 FAIL — известные на HEAD, не регресс).
