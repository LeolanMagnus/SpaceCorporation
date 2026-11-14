# Тест двух игроков: два окна из собранной игры

Сценарий: окно 1 поднимает сервер кнопкой `Start Server` (режим Host, там же
играет первый игрок), окно 2 подключается кнопкой `Join` и видит обоих игроков.
В одном окне сыграть с собой нельзя — peer один (§4 В1), поэтому нужны два
процесса. Собранный exe для этого идеален: его можно запускать сколько угодно раз.

Пресет `Windows Desktop` уже создан в `export_presets.cfg` (путь экспорта —
`build/SpaceCorporation.exe`, рядом ляжет `SpaceCorporation.pck`).

## 0. Одноразово: установить шаблоны экспорта

Godot требует шаблоны **своей версии**. Установлены только 4.2.2 — для 4.7.2
нужно скачать один раз:

- Через редактор: `Editor → Manage Export Templates → Download and install`.
- Или вручную: скачать `export_templates_<версия>.tpz` с
  https://godotengine.org/download/archive/ и распаковать в
  `%APPDATA%\Godot\export_templates\4.7.2.stable.mono\`
  (для mono-сборки папка с суффиксом `.mono`; внутри должны лежать
  `windows_release_x86_64.exe` и `windows_debug_x86_64.exe`).

## 1. Собрать игру

- Через редактор: `Project → Export…` → выбрать пресет `Windows Desktop` →
  `Export…` → сохранить как `build/SpaceCorporation.exe` (OK).
- Или из консоли (Git Bash, из корня проекта):

```bash
GODOT="/f/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe"
dotnet build -v q --nologo
"$GODOT" --headless --path . --export-release "Windows Desktop" build/SpaceCorporation.exe
```

Готово: в `build/` появятся `SpaceCorporation.exe` + `SpaceCorporation.pck`
(+ `SpaceCorporation.console.exe` — обёртка консоли, её не запускать для игры).
`build/` не коммитить.

## 2. Запустить два окна

Просто дважды запустить exe (двойной клик или консоль):

```bash
./build/SpaceCorporation.exe &   # окно 1 — будущий сервер
./build/SpaceCorporation.exe &   # окно 2 — будущий клиент
```

Можно и через флаги, но строго ПОСЛЕ `--` (флаги до `--` съедает движок, и
процесс «молча не сервер», §3.11 п.7):

```bash
./build/SpaceCorporation.exe -- --server &          # окно 1: host + его игрок
./build/SpaceCorporation.exe -- --join 127.0.0.1 &  # окно 2: клиент
```

(Флаги понимает и exe: `OS.GetCmdlineUserArgs()` читает аргументы после `--`.)

## 3. Сценарий через дебаг-панель (без флагов)

В КАЖДОМ окне:

1. **F3** — показать кнопку «Debug Mode» (действие `debug_toggle`).
2. Клик по «Debug Mode» — открылась панель.
3. **Снять галку «Pause world»** (вверху панели)! Открытая панель по умолчанию
   морозит мир — с паузой репликация не идёт и второй игрок «не появится/не
   поедет». Это главная ловушка ручного теста.
4. Окно 1: кнопка **`Start Server`** → это Host: сервер в этом окне + игрок
   с авторитетом 1.
5. Окно 2: кнопка **`Join`** (коннект на 127.0.0.1:24567).

## 4. Как убедиться, что всё получилось

Логи (для exe — в обёртке консоли `SpaceCorporation.console.exe`, либо
`--verbose`; в редакторе — вкладка Output):

- Окно 1: `NetworkManager: host-сервер, self = 1, порт = 24567, макс. игроков = 8`
  → затем `PeerConnected id = 2`.
- Окно 2: `NetworkManager: подключаюсь к 127.0.0.1:24567, self = 2`
  → `ConnectedToServer (self = 2)`.
- Строка `Net` в панели обоих окон: `role = host/client, self = …, peers = […]`.

Визуально: в обоих окнах видно двух игроков (узлы Player реплицирует
MultiplayerSpawner, позиции шлёт сервер). Движение первого игрока видно второму
и наоборот.

## 5. Грабли

- **`Error.AlreadyInUse`** в `Start Server` — порт 24567 занят висящим процессом
  с прошлого прогона. Убить процесс (диспетчер задач / `kill`) или задать другой
  порт: `-- --server --port 24570` и `-- --join 127.0.0.1 --port 24570`
  (порт одинаков у обоих).
- Забыт `--` перед флагами — процесс «молча не сервер».
- «Pause world» оставлен включённым — мир стоит, кажется что репликации нет.
- Окно 2 запустили ДО `Start Server` в окне 1 — `Join` даст `ConnectionFailed`
  и вернёт роль в Offline; жать `Join` надо после поднятого сервера.

## 6. Быстрая проверка без окон (CI/лог)

`./run-net-demo.sh` — dedicated-сервер + 2 клиента headless, печатает
`### OK: сервер принял пира(ов)`. Окон нет, но транспорт и спавн проверяются
полностью.
