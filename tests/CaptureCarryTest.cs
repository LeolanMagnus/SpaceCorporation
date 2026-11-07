using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Живой тест-сценарий frame-plan шаг 7: «унос картинки захваченного корабля».
/// Наблюдатель (не авторитет) интерполирует картинку A на `now − _interpDelay`
/// — то есть на delay секунд в прошлом. Если при этом пилот родителя B гонит
/// A со скоростью v_frame, наблюдатель рисует A на |v_frame|·delay метров
/// ПОЗАДИ того места, где его видит пилот B (при 100 м/с и 0.1 с — 10 м).
/// Шаг 7 добавляет в `InterpolateHull` сдвиг точки съёмки `v_frame ×
/// _interpDelay` (slew-лимитированный), взятый из РЕПЛИК родителя (тот же
/// NetState, что рисует этот наблюдатель).
///
/// Тест изолирует механику от физики и UDP: корабли спавнятся с выключенным
/// пилот-ассистом и пустым вводом (стоят), позы в буфер интерполяции кладутся
/// швом `TestPushSnapshot` — как в NetTest п.18. Дальше три группы проверок:
///
///   (1) величина: картинка A со сдвигом = картинка A без сдвига +
///       v_frame·delay, когда реплики родителя задали v_frame;
///   (2) плавность: после включения фазы сдвиг НАРАСТАЕТ по slew-лимиту
///       (8 м/с), ни один кадр не даёт скачка больше порога метрики (в)
///       MaxHullDelta (0.15 м при 60 к/с) — вход в захват не рисует прыжок;
///   (3) гашение: после сброса реплики родителя (выход из фазы) вектор
///       плавно гаснет к нулю, картинка возвращается на позу снимка.
///
/// Плюс гейт дальнего тира (R8): после far-записи (`ApplyBatchRecord` с
/// FarTier=true) унос НЕ включается даже с валидной репликой родителя — в far
/// нет скоростей, а протухшие использовать нельзя.
///
/// Роль наблюдателя лепим честно: у корабля-наблюдателя снимается авторитет
/// (`Net.SetOwner(0)` + пересчёт — см. TestDropAuthorityForTest, если шва
/// нет — реальный путь: спавним оба корабля на сервере, авторитет у обоих у
/// сервера; чтобы включился НЕ-авторитетный путь, проверяем `ApplyBatchRecord`
/// напрямую: он сам гейтится по HasAuthority).
/// Запуск: ./run-tests.sh res://tests/CaptureCarryTest.tscn
/// </summary>
public partial class CaptureCarryTest : Node
{
    private int _checks;
    private int _failures;
    private int _frame;
    private Node? _main;
    private WorldSpawner? _spawner;
    private Ship? _small;   // захваченный (наблюдатель проверяется на нём)
    private Ship? _big;     // родитель

    private enum Phase
    {
        WaitSpawn, StartServer, Setup, Baseline,
        CarryRamp, CarryHold, FarGate, FadeOut,
        Done
    }
    private Phase _phase = Phase.WaitSpawn;

    private const float ParentSpeed = 100f;   // м/с по X — величина v_frame
    private const double T0 = 100.0;          // «сейчас» в условных секундах
    private const double Step = 1.0 / 60.0;   // кадровый шаг вызова InterpolateHull
    // Непрерывные тестовые часы: все фазы идут на одной шкале, dt slew-счётчика
    // обязан быть ровно Step — разрыв шкалы между фазами даёт фантомный
    // «скачок картинки» (slew за большой dt законен, но метрика (в) считает его).
    private double _t;

    // Пороги: см. Ship.CapShiftSlewPerSec = 8 м/с и метрику (в) MaxHullDelta.
    private const float SlewPerSec = 8.0f;
    private const float MaxHullDelta = 0.15f; // метрика (в): кадр не больше 0.15 м

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _main = GetNodeOrNull<Node>("Main");
        if (_main == null)
        {
            GD.PrintErr("FAIL: сцена Main не найдена");
            Finish();
            return;
        }
        NetworkManager.TestExtraPeers.Add(2);
    }

    public override void _PhysicsProcess(double delta)
    {
        _frame++;
        switch (_phase)
        {
            case Phase.WaitSpawn:
                _spawner = WorldSpawner.Current;
                if (_spawner != null && _spawner.FindShipByName("Ship") != null)
                    _phase = Phase.StartServer;
                else if (_frame > 120)
                {
                    Check(false, "мир заспавнен (Ship появился в GameRoot)");
                    Finish();
                }
                break;

            case Phase.StartServer:
            {
                var nm = NetworkManager.Instance;
                Check(nm != null, "NetworkManager.Instance доступен");
                if (nm != null)
                {
                    var err = nm.StartServer();
                    Check(err == Error.Ok, $"StartServer() в headless -> Ok (получено {err})");
                }
                _phase = Phase.Setup;
                break;
            }

            case Phase.Setup:
                SetupPair();
                _phase = _small != null && _big != null ? Phase.Baseline : Phase.Done;
                break;

            case Phase.Baseline:
            {
                // Без фазы захвата (реплика родителя = 0): уноса НЕТ — картинка
                // строго в интерполированной позе (регрессия: шаг 7 не трогает
                // обычный путь). Два снимка, render посередине.
                _small!.TestPushSnapshot(T0, Vector3.Zero, Quaternion.Identity);
                _small.TestPushSnapshot(T0 + 0.2, new Vector3(0f, 0f, -20f), Quaternion.Identity);
                _small.InterpolateHull(T0 + 0.3); // render = T0+0.2 -> ровно b
                var p0 = _small.TestHullGlobalPosition();
                Check(Mathf.Abs(p0.Z + 20f) < 0.01f && p0.X < 0.001f && p0.Y < 0.001f,
                      $"без фазы: картинка в позе снимка {p0} (ожидается (0,0,-20))");

                // Скорость-реплика родителя есть, но фазы нет (родитель = 0) —
                // всё равно никакого сдвига.
                _big!.Net!.NetLinearVelocity = new Vector3(ParentSpeed, 0f, 0f);
                _small.InterpolateHull(T0 + 0.3 + Step);
                _t = T0 + 0.3 + Step;
                var p1 = _small.TestHullGlobalPosition();
                Check(p1.DistanceTo(p0) < 0.01f,
                      "реплика скорости родителя БЕЗ фазы захвата унос не включает");
                _phase = Phase.CarryRamp;
                break;
            }

            case Phase.CarryRamp:
            {
                // Включение фазы: ставим реплику родителя (ровно то значение,
                // которое OnChange-синхронизатор донёс бы до наблюдателя) и
                // гоним кадры: сдвиг обязан НАРАСТАТЬ по slew-лимиту.
                // ВАЖНО: реплику скорости родителя переписывает ноль-публикация
                // живого авторитета в `_PhysicsProcess` (публикация каждые
                // NetPublishEveryN физшагов), поэтому скорость выставляется
                // ЗАНОВО в начале каждой фазы — внутри фазы физкадров не
                // происходит, все вызовы InterpolateHull идут в одном帧.
                _big!.Net!.NetLinearVelocity = new Vector3(ParentSpeed, 0f, 0f);
                _small!.Net.CaptureParentNetId = _big.Net.ShipNetId;
                var prev = _small.TestHullGlobalPosition();
                float maxJump = 0f;
                int stepsToFull = 0;
                const int maxSteps = 120;
                // Цель по X = v_frame.X · delay = 100 · 0.1 = 10 м (delay у
                // наблюдателя без сети остаётся базовым InterpDelay=0.1).
                float wantShift = ParentSpeed * (float)_small.InterpDelaySec;
                for (int i = 0; i < maxSteps; i++)
                {
                    _t += Step;
                    _small.InterpolateHull(_t);
                    var cur = _small.TestHullGlobalPosition();
                    maxJump = Mathf.Max(maxJump, cur.DistanceTo(prev));
                    prev = cur;
                    stepsToFull = i + 1;
                    if (Mathf.Abs(cur.X - wantShift) < 0.001f)
                        break; // дошли до цели сдвига (v_frame·delay по X)
                }
                var finalPos = _small.TestHullGlobalPosition();
                Check(Mathf.Abs(finalPos.X - wantShift) < 0.05f && Mathf.Abs(finalPos.Z + 20f) < 0.01f,
                      $"унос вышел на цель: X={finalPos.X:F3} ≈ v·delay={wantShift:F2}, " +
                      $"Z={finalPos.Z:F3} (поза снимка без сдвига)");
                Check(maxJump <= MaxHullDelta + 1e-3f,
                      $"разгон плавный: максимальный скачок картинки за кадр = {maxJump:F4} " +
                      $"<= порога метрики (в) {MaxHullDelta}");
                // Сlew 8 м/с на 10 м ≈ 1.25 с ≈ 75 кадров (первый кадр — dt=0,
                                // сдвиг не двигается — см. коммент в CaptureCarryShift).
                Check(stepsToFull is >= 70 and <= 80,
                      $"разгон идёт slew-лимитом 8 м/с: шагов до цели = {stepsToFull} " +
                      $"(ожидается ~75 = 10м / (8м/с · 1/60с))");
                _phase = Phase.CarryHold;
                break;
            }

            case Phase.CarryHold:
            {
                // Держим фазу: сдвиг стабилен = v_frame·delay, картинка не
                // ползёт (стабильная цель => slew не работает).
                var before = _small!.TestHullGlobalPosition();
                for (int i = 0; i < 30; i++)
                    _small.InterpolateHull(_t += Step);
                var after = _small.TestHullGlobalPosition();
                Check(before.DistanceTo(after) < 0.001f,
                      $"в устоявшейся фазе картинка стоит: |Δ| = {before.DistanceTo(after):F6}");
                _phase = Phase.FarGate;
                break;
            }

            case Phase.FarGate:
            {
                // R8: far-запись скоростей не несёт — унос обязан погаситься,
                // даже если реплика родителя валидна. ApplyBatchRecord —
                // НЕ-авторитетный путь (сам гейтится по HasAuthority), в этом
                // стенде оба корабля авторитетны сервером, поэтому тир ставим
                // тем же способом, каким его выставляет приёмник: через
                // ApplyBatchRecord нельзя — он отобьётся. Шов: TestSetWireTier
                // (см. Ship.cs) выставляет `_lastWireFarTier` ровно так, как
                // это делает строка 3059.
                _small!.TestSetWireTierForTest(true);
                var p = _small.TestHullGlobalPosition();
                Check(p.X > 5f, $"far-гейт: уже начатый сдвиг НЕ обнуляется мгновенно (X={p.X:F2})");
                var prev = p;
                float maxJump = 0f;
                for (int i = 0; i < 120; i++)
                {
                    _t += Step;
                    _small.InterpolateHull(_t);
                    var cur = _small.TestHullGlobalPosition();
                    maxJump = Mathf.Max(maxJump, cur.DistanceTo(prev));
                    prev = cur;
                    if (cur.X < 0.001f) break;
                }
                var ended = _small.TestHullGlobalPosition();
                Check(ended.X < 0.001f && Mathf.Abs(ended.Z + 20f) < 0.01f,
                      $"far-тир: унос погашен к нулю, картинка в позе снимка " +
                      $"(X={ended.X:F4}, Z={ended.Z:F3})");
                Check(maxJump <= MaxHullDelta + 1e-3f,
                      $"гашение в far плавное: скачок за кадр = {maxJump:F4} <= {MaxHullDelta}");
                _small.TestSetWireTierForTest(false);
                _phase = Phase.FadeOut;
                break;
            }

            case Phase.FadeOut:
            {
                // Выход из фазы: реплика родителя = 0 (так её сбрасывает
                // OnChange-переход) — вектор обязан плавно погаснуться, даже
                // когда родительская реплика уже недоступна (залатчанный
                // `_capShiftCur` доживает до нуля).
                _small!.Net.CaptureParentNetId = 0;
                var p = _small.TestHullGlobalPosition();
                Check(p.X < 0.001f, $"после far сдвиг уже погашен (X={p.X:F4}) — старт гашения с нуля ок");
                // Повторный цикл: разгон → сброс фазы → гашение к нулю.
                _small.Net.CaptureParentNetId = _big!.Net!.ShipNetId;
                for (int i = 0; i < 30; i++) // разгон 0.5 с = 4 м
                    _small.InterpolateHull(_t += Step);
                var mid = _small.TestHullGlobalPosition();
                Check(mid.X > 3.5f && mid.X < 4.5f,
                      $"разгон за 0.5 с = {mid.X:F2} м (slew 8·0.5=4)");
                _small.Net.CaptureParentNetId = 0;
                var prev = mid;
                float maxJump = 0f;
                for (int i = 0; i < 120; i++)
                {
                    _t += Step;
                    _small.InterpolateHull(_t);
                    var cur = _small.TestHullGlobalPosition();
                    maxJump = Mathf.Max(maxJump, cur.DistanceTo(prev));
                    prev = cur;
                    if (cur.X < 0.001f) break;
                }
                var ended = _small.TestHullGlobalPosition();
                Check(ended.X < 0.001f,
                      $"сброс фазы: сдвиг погас к нулю (X={ended.X:F4})");
                Check(maxJump <= MaxHullDelta + 1e-3f,
                      $"гашение при выходе плавное: скачок = {maxJump:F4} <= {MaxHullDelta}");
                _phase = Phase.Done;
                break;
            }

            case Phase.Done:
                Finish();
                break;
        }
    }

    private void SetupPair()
    {
        _big = _spawner!.SpawnShipFromScene(Vector3.Zero, "CarryBig", ShipType.ContainerFreighter);
        _small = _spawner.SpawnShipFromScene(new Vector3(0f, 0f, -15f), "CarrySmall", ShipType.Courier);
        Check(_big != null && _small != null, "пара кораблей заспавнена");
        if (_big == null || _small == null) return;

        // Физика в этом тесте не нужна вовсе: стоячие корабли с пустым вводом,
        // ассист выключен, чтобы реальные физкадры не двигали Net-реплики
        // (авторитет пишет NetState из тела каждый шаг — при стоячем корпусе
        // реплики остаются нулями, а позы картинки кладёт TestPushSnapshot).
        _big.PilotAssist = false;
        _small.PilotAssist = false;
        _big.Flight.PilotAssist = false;
        _big.Flight.AssistDragAccel = 0f;
        _small.Flight.PilotAssist = false;
        _small.Flight.AssistDragAccel = 0f;
        _big.TestSetInputForTest(default, manualSteps: true);
        _small.TestSetInputForTest(default, manualSteps: true);

        // Полностью глушим игровой конвейер обоих кораблей. Причина (найдено
        // прогоном): живой `_PhysicsProcess` авторитета переписывает NetState из
        // тела каждые NetPublishEveryN физшагов (Net.NetLinearVelocity = 0 у
        // стоячего корпуса — это обнуляло вручную выставленную реплику скорости
        // родителя), а живой `_Process` зовёт InterpolateHull на ЧАСАХ реального
        // времени и гонит дренаж `_recvBuf` — всё это отравляло dt в slew-счётчике
        // и позы между фазами. Тест управляет конвейером сам: позы кладёт
        // TestPushSnapshot, кадры рисует ручными InterpolateHull(now).
        _big.SetProcess(false);
        _big.SetPhysicsProcess(false);
        _small.SetProcess(false);
        _small.SetPhysicsProcess(false);

        Check(_small.InterpolationEnabled, "у наблюдателя интерполяция включена");
        Check(Mathf.Abs((float)_small.InterpDelaySec - 0.1f) < 1e-5f,
              $"без сети delay = базовый InterpDelay=0.1 (получено {_small.InterpDelaySec:F3})");
    }

    private void Check(bool ok, string what)
    {
        _checks++;
        if (ok)
            GD.Print($"  PASS: {what}");
        else
        {
            _failures++;
            GD.PrintErr($"  FAIL: {what}");
        }
    }

    private void Finish()
    {
        GD.Print($"=== CaptureCarryTest: {_checks - _failures}/{_checks} проверок, " +
                 $"{_failures} провалов ===");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
