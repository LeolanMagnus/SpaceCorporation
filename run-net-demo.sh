#!/usr/bin/env bash
# Двухпроцессное сетевое демо стадии 0 (§2.6, §3.8): dedicated-сервер (headless)
# + N клиентов на 127.0.0.1. Критерий: экземпляры соединяются, каждый печатает
# роль и список пиров (строка "net: role = …, self = …, peers = […]").
#
# Использование:
#   ./run-net-demo.sh            — сервер + 2 клиента
#   ./run-net-demo.sh 3          — сервер + 3 клиента (число задаёт СКРИПТ, не Godot)
#   PORT=24570 ./run-net-demo.sh — другой порт (по умолчанию 24567)
#
# Флаги передаются ТОЛЬКО после `--`: иначе их съедает движок и процесс «молча
# не сервер» (§3.11 п.7). Сервер убивается по PID: dedicated крутится вечно, а
# висящий процесс с занятым портом ломает следующий прогон (Error.AlreadyInUse).
set -euo pipefail

GODOT="${GODOT:-/f/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
CLIENTS="${1:-2}"
PORT="${PORT:-24567}"

cd "$(dirname "$0")"

echo "### dotnet build"
dotnet build -v q --nologo

LOGDIR="$(mktemp -d)"
PIDS=()

cleanup() {
    # Убиваем всё, что подняли: сначала клиентов, потом сервер.
    for pid in "${PIDS[@]:-}"; do
        kill "$pid" 2>/dev/null || true
    done
    wait 2>/dev/null || true
    echo "### демо остановлено (логи: $LOGDIR)"
}
trap cleanup EXIT INT TERM

echo "### сервер: --headless -- --server --port $PORT"
"$GODOT" --headless --path . -- --server --port "$PORT" >"$LOGDIR/server.log" 2>&1 &
SRV=$!
PIDS+=("$SRV")

# Ждём, пока сервер поднимется (порт занят = сервер жив). Простой спиннер по логу.
for _ in $(seq 1 40); do
    if grep -qE 'role = dedicated|self = 1' "$LOGDIR/server.log" 2>/dev/null; then
        break
    fi
    if ! kill -0 "$SRV" 2>/dev/null; then
        echo "### СЕРВЕР УМЕР при старте:"
        cat "$LOGDIR/server.log"
        exit 1
    fi
    sleep 0.25
done

for i in $(seq 1 "$CLIENTS"); do
    echo "### клиент #$i: --headless -- --join 127.0.0.1 --port $PORT"
    "$GODOT" --headless --path . -- --join 127.0.0.1 --port "$PORT" >"$LOGDIR/client$i.log" 2>&1 &
    PIDS+=("$!")
done

# Дём-прогон: даём пирам обменяться пакетами и напечатать строку net: (раз в секунду).
sleep 4

echo "================ СЕРВЕР ================"
grep -E 'net: role|PeerConnected|PeerDisconnected|spawn =' "$LOGDIR/server.log" | tail -20 || true
for i in $(seq 1 "$CLIENTS"); do
    echo "================ КЛИЕНТ #$i ================"
    grep -E 'net: role|ConnectedToServer|ConnectionFailed|spawn =' "$LOGDIR/client$i.log" | tail -10 || true
done

# Критерий стадии 0: сервер видит подключившихся пиров.
if grep -qE 'PeerConnected id = [0-9]+' "$LOGDIR/server.log"; then
    echo "### OK: сервер принял пира(ов)"
else
    echo "### FAIL: сервер не принял ни одного пира"
    exit 1
fi
