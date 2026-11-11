#!/usr/bin/env bash
# Прогон всех автопрогонов этапа; печатает нормализованную строку итога по каждому.
GODOT="${GODOT:-/f/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
cd "$(dirname "$0")"
# Подчищаем зависшие инстансы Godot (иначе новый запуск блокируется локами).
taskkill //F //IM "Godot_v4.7.2-stable_mono_win64.exe" >/dev/null 2>&1
taskkill //F //IM "Godot_v4.7.2-stable_mono_win64_console.exe" >/dev/null 2>&1
dotnet build -v q --nologo 2>&1 | grep -iE "error CS" || true
grand_checks=0; grand_fails=0
for t in "$@"; do
  scene="res://tests/$t.tscn"
  out=$(timeout 150 "$GODOT" --headless --path . "$scene" 2>&1)
  # Итоговая строка в любом из трёх форматов тестов:
  #   "RESULT checks=N failures=M"        (Stage7/Net/...)
  #   "итог: N OK, M FAIL"                (Stage2/LiquidSpace)
  #   "TESTNAME: N PASS / M FAIL"         (SuitModel/Shim)
  #   "STAGEntTest OK/FAIL"               (Stage3/4/5/6)
  res=$(echo "$out" | grep -E "RESULT checks|итог: [0-9]+ OK|[0-9]+ PASS / [0-9]+ FAIL|STAGE[0-9]+TEST (OK|FAIL)" | tail -1)
  # Реальные провалы — ТОЛЬКО индивидуальные строки прогона (не счётчик «0 FAIL»):
  #   строка с началом «FAIL », «[FAIL]», «FAIL:», или итог «failures=[1-9]».
  fails=$(echo "$out" | grep -cE "^FAIL |[[:space:]]\[FAIL\]|FAIL:|failures=[1-9]")
  echo "$t => ${res:-NO_RESULT}  (real-fail-lines=$fails)"
  # Суммируем числа из итоговой строки, если она в формате checks/failures.
  if echo "$res" | grep -qE "RESULT checks=[0-9]+ failures=[0-9]+"; then
    c=$(echo "$res" | sed -E 's/.*checks=([0-9]+).*/\1/')
    f=$(echo "$res" | sed -E 's/.*failures=([0-9]+).*/\1/')
    grand_checks=$((grand_checks + c)); grand_fails=$((grand_fails + f))
  fi
done
echo "GRAND (checks/failures format tests): checks=$grand_checks failures=$grand_fails"
taskkill //F //IM "Godot_v4.7.2-stable_mono_win64.exe" >/dev/null 2>&1
taskkill //F //IM "Godot_v4.7.2-stable_mono_win64_console.exe" >/dev/null 2>&1
