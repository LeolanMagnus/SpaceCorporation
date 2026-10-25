# Stage0 rework: «игрок нажал Start Server и сам остался играть»

## Модель

Меняем решение В1 (ранее: dedicated-only, listen-server запрещён). Новая модель:
кнопка **Start Server** поднимает сервер в этом процессе и делает процесс **Host** —
сервером И игроком одновременно. CLI `--server` остаётся чистым **Dedicated**
(headless-сервер без игрока, тесты/CI не меняются).

Роли после переделки:

| Роль      | Суть                                   | IsServer | IsClient | IsHost | Игрок в процессе |
|-----------|----------------------------------------|----------|----------|--------|------------------|
| Offline   | одиночный мир, peer нет                | false    | false    | false  | есть, авторитет 1 |
| Host      | сервер + локальный игрок (кнопка)      | true     | false    | true   | есть, авторитет 1 (= id сервера) |
| Dedicated | сервер без игрока (CLI `--server`)     | true     | false    | false  | нет               |
| Client    | подключился к чужому серверу (кнопка/CLI) | false | true     | false  | свой игрок репликой, авторитет ≥ 2 |

Ключевые инварианты (правки к старым):
- «авторитет Player никогда не равен 1» действует только для **чужих** игроков;
  игрок хоста имеет авторитет 1 — это id сервера, и это корректно, потому что
  хост и есть сервер;
- «мой игрок» = `GetMultiplayerAuthority() == LocalAuthorityId` — для хоста это
  `1 == 1`, код Player/Ship не меняется;
- серверная ветка (спавн, авторитеты, приём репортов) — по `IsServer`, хост
  проходит по ней автоматически.

## Механика перехода Offline → Host

Игрока НЕ сохраняем узлом (он мог быть перепришпилён под точку крепления и
спавнился до появления peer — MultiplayerSpawner его не зарепликует). Вместо
этого — тот же путь, что уже работает для клиентов:

1. NetworkManager пишет состояние текущего offline-игрока (авторитет 1) в
   `_lastSeen[PlayerKey]` (CaptureLastSeen — позиция/поворот/крепление);
2. WorldSpawner.RemoveOfflinePlayer() (QueueFree в конце кадра);
3. SpawnWorld() идемпотентен (корабли уже есть);
4. NetworkManager.SpawnHostPlayer() → та же серверная сердцевина спавна
   (SpawnPlayerForPeer(1, PlayerKey)): игрок возрождается на той же позиции /
   в той же точке крепления, авторитет 1, через MultiplayerSpawner → реплика
   у всех подключённых клиентов;
5. регистрация в таблицах: `_peerPlayers[1]`, `_peerKeys[1] = PlayerKey`;
6. запасная камера НЕ включается (камера игрока current=true перекрывает viewport,
   мигания нет: старая камера освобождается в конце кадра, новая уже current).

Disconnect (Host → Offline): узел игрока с авторитетом 1 остаётся в мире и
становится offline-игроком (StartSinglePlayerWorld найдёт его через
FindOfflinePlayer и не будет спавнить второго). Таблицы слота 1 очищаются.

## Коллизия playerKey с хостом (важно для dev-теста в два окна)

Два процесса одного проекта делят `user://identity.cfg` → одинаковый PlayerKey.
Если клиент с тем же ключом, что у хоста, просит спавн — хоста НЕ выгоняем
(holder == 1 не выгоняют), вместо этого клиент получает гостевой ключ
`key#<peerId>` с громким предупреждением. Иначе dev-прогон «два окна» невозможен.

## Шаги реализации

1. **NetworkManager.cs**
   - enum: `NetRole { Offline, Dedicated, Host, Client }`;
   - `IsServer => Role == Dedicated || Role == Host`; `IsHost => Role == Host`;
   - `StartServer(port, maxPlayers, bool withLocalPlayer = false)` →
     SetRole(withLocalPlayer ? Host : Dedicated); кнопка панели → `withLocalPlayer: true`;
     CLI `--server` → false (Dedicated, поведение прежнее);
   - вынести сердцевину спавна из HandleRequestSpawn в
     `SpawnPlayerForPeer(peerId, playerKey)`;
   - `SpawnHostPlayer()` (публично, шов автотеста): спавн с авторитетом 1 +
     регистрация слота 1; `CaptureHostCandidateLastSeen()`;
   - HandleRequestSpawn: коллизия ключа с holder==1 → гостевой ключ `key#peer`;
   - Disconnect: чистка слота 1 в таблицах без удаления узла;
   - GetNetInfoLine: `players = IsServer ? peers.Length + (IsHost ? 1 : 0) : ...`;
   - печать строки сети: Dedicated или Host.
2. **WorldSpawner.cs**
   - OnRoleChanged: ветка `next == Host` (capture → remove → SpawnWorld →
     SpawnHostPlayer, без fallback-камеры); Dedicated — прежнее;
   - _Ready: если роль уже Host (на будущее) — SpawnWorld + SpawnHostPlayer.
3. **Тесты**
   - NetTest: новая фаза Host-перехода: StartServer(withLocalPlayer:true) →
     Role==Host, IsServer, ровно один Player с авторитетом 1, LivePlayerCount==1,
     Disconnect → игрок сохранён, роль Offline; ошибок DebugInfoBus нет;
   - Stage1Test: кнопка «Start Server» есть, «Host» нет — остаётся как есть.
4. **bugs.md**: запись о смене модели В1 и о починке ObjectDisposedException.

## Что НЕ меняется

- CLI `--server` / headless-стенды / CI — Dedicated как раньше;
- Player.cs / Ship.cs — проверки по `IsServer`/авторитету уже корректны для хоста;
- путь клиента (Join → RequestSpawn → спавн ≥ 2) — без изменений.
