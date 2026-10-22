#!/usr/bin/env bash
# Прогон автотеста этапа 1 (и последующих) в headless-режиме.
# Использование: ./run-tests.sh            — тест этапа 1
#                ./run-tests.sh res://tests/Stage2Test.tscn
set -euo pipefail

GODOT="${GODOT:-/f/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
SCENE="${1:-res://tests/Stage1Test.tscn}"

cd "$(dirname "$0")"

echo "### dotnet build"
dotnet build -v q --nologo

echo "### godot --headless $SCENE"
"$GODOT" --headless --path . "$SCENE"
