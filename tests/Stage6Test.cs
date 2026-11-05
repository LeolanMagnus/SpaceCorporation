using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Автотест стадии «радар» (radar-plan.md, этап R2): сборщик контактов
/// RadarSource + чистая RadarModel + wiring в HUD. Запуск:
///   godot --headless --path . res://tests/Stage6Test.tscn
///
/// Критерий плана R2: «в мире с кораблём и мишенью рядом (< 2000 м) радар
/// даёт ≥ 1 блип». Проверки:
///   п.1 — хост-режим: сервер с локальным игроком, игрок пристёгнут, свой
///         корабль найден (OwnShipForTest) — центр радара определён;
///   п.2 — горячая клавиша hud_radar зарегистрирована в InputMap;
///   п.3 — спавн мишени в 1200 м от своего корабля: NetPosition записан
///         (правка WorldSpawner), адрес в реестре Targets;
///   п.4 — CollectContacts: свой корабль ИСКЛЮЧЁН, Ship2 (~16 м) и мишень
///         (~1200 м) присутствуют с верными RelPos/фракцией/формой;
///   п.5 — Sync+Advance: LiveCount ≥ 2, FindBlip(мишень) != null, своего
///         корабля в блипах нет, дистанция блипа ≈ 1200;
///   п.6 — станция TestStand: блип square/Neutral;
///   п.7 — флаг цели: Select(мишень) → контакт с IsTarget;
///   п.8 — радиусный фильтр: мишень в 2500 м не входит (< 0.9R нет, новый);
///   п.9 — гистерезис/затухание: при сужении Range ближний контакт выходит
///         в Stale и гаснет за FadeTime;
///   п.10 — экстраполяция: RelVel двигает блип между сборками;
///   п.11 (R3) — цвет по фракции (HudTheme.FactionColor различает фракции),
///         hit-test блипа (HitTestBlipId/BlipPixel) и клик-выбор через
///         TargetingService, pinned-флаг цели в модели.
/// Тела проверок — Stage6Test.Helpers.cs (карта фаз, как Stage5Test).
/// </summary>
public partial class Stage6Test : Node
{
    private int _checks;
    private int _failures;
    private int _frame;
    private Node? _main;
    private WorldSpawner? _spawner;
    private RadarSource? _source;

    private enum Phase
    {
        WaitSpawn, StartServer, WaitPlayer,
        P1, P2, P3, P4, P5, P6, P7, P8, P9, P10,
        Done
    }
    private Phase _phase = Phase.WaitSpawn;

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
        // Headless: пир 2 «существует» для проверок реестра (образец Stage5Test).
        NetworkManager.TestExtraPeers.Add(2);
    }

    public override void _PhysicsProcess(double delta)
    {
        _frame++;
        switch (_phase)
        {
            case Phase.WaitSpawn:
                _spawner = WorldSpawner.Current;
                if (_spawner != null && _spawner.FindShipByName("Ship") != null
                                  && _spawner.FindShipByName("Ship2") != null)
                    _phase = Phase.StartServer;
                else if (_frame > 120)
                {
                    Check(false, "мир заспавнен (Ship и Ship2 в GameRoot)");
                    Finish();
                }
                break;

            case Phase.StartServer:
            {
                var nm = NetworkManager.Instance;
                Check(nm != null, "NetworkManager.Instance доступен");
                if (nm != null)
                {
                    // Host (с локальным игроком): у теста должен быть свой
                    // корабль — центр радара. Dedicated без игрока оставил бы
                    // радар без центра (OwnShipNode == null).
                    var err = nm.StartServer(withLocalPlayer: true);
                    Check(err == Error.Ok, $"StartServer(host) -> Ok (получено {err})");
                    Check(nm.IsServer && nm.Role == NetworkManager.NetRole.Host,
                          $"после StartServer(withLocalPlayer) роль Host (получено {nm.Role})");
                }
                _source = _main?.GetNodeOrNull<RadarSource>("HudLayer/RadarSource");
                Check(_source != null, "RadarSourceFinden: Main/HudLayer/RadarSource смонтирован");
                _phase = Phase.WaitPlayer;
                break;
            }

            case Phase.WaitPlayer:
                // Хост-игрок спавнится через RoleChanged; автопристёгивание к
                // ближнему сиденью — его _Process. Ждём, пока свой корабль
                // определится (или страховка AttachToDefaultPoint).
                if (_source != null && _source.OwnShipForTest() != null)
                {
                    // Режимы дальности: штатный радар Mk1 — 800 м, а тесты
                    // п.4-п.11 считают контакты до 2500 м: ставим RN-2 (2 км),
                    // мощность радара корабля = 2000 (MaxRange модели).
                    _source.OwnShipForTest()!.EquipModule("radar_rn2_mk2");
                    _phase = Phase.P1;
                }
                else if (_frame > 240)
                {
                    Check(false, "хост-игрок пристёгнут к кораблю (OwnShipForTest != null)");
                    Finish();
                }
                break;

            case Phase.P1: CheckOwnShipAndCenter();       _phase = Phase.P2; break;
            case Phase.P2: CheckHotkeyRegistered();       _phase = Phase.P3; break;
            case Phase.P3: SpawnNearbyTarget();           _phase = Phase.P4; break;
            case Phase.P4: CheckCollectContacts();        _phase = Phase.P5; break;
            case Phase.P5: CheckModelBlips();             _phase = Phase.P6; break;
            case Phase.P6: CheckStationBlip();            _phase = Phase.P7; break;
            case Phase.P7: CheckTargetFlag();             _phase = Phase.P8; break;
            case Phase.P8: CheckFarTargetExcluded();      _phase = Phase.P9; break;
            case Phase.P9: CheckHysteresisAndExtrapolation(); _phase = Phase.P10; break;
            case Phase.P10: CheckR3Interaction();            _phase = Phase.Done; break;

            case Phase.Done: Finish(); break;
        }
    }

    // ------------------------------------------------------------- утилиты

    private void Check(bool ok, string what)
    {
        _checks++;
        if (ok)
            GD.Print($"PASS: {what}");
        else
        {
            _failures++;
            GD.PrintErr($"FAIL: {what}");
        }
    }

    private void Finish()
    {
        GD.Print($"==== Stage6Test: проверок={_checks}, провалов={_failures} ====");
        GD.Print(_failures == 0 ? "==== STAGE6TEST OK ====" : "==== STAGE6TEST FAILED ====");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
