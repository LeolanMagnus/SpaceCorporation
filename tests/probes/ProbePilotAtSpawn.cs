using Godot;

namespace SpaceCorporation;

/// <summary>Проба BUG-008 (повторная): спавним Main.tscn и проверяем симптом из
/// баг-листа — «второй корабль тоже реагирует на ввод игрока».
///
/// Фаза 1 (offline): читаем PilotPeerId / IsLocalPilot / включён ли ввод у обоих
/// кораблей.
/// Фаза 2 (offline): жмём thrust_f 60 кадров и смотрим, какое тело поехало — ехать
/// должен только пристёгнутый корабль (#1), свободный (#2) обязан остаться
/// неподвижным. Ожидание: ship#1 pilot=1, IsLocalPilot=True, поехал; ship#2
/// pilot=0, IsLocalPilot=False, input выключен, |v| = 0.
///
/// Фаза 3-4 (сеть, флаг `--host`): на том же прогоне поднимаем host-сервер (путь
/// кнопки «Start Server»: offline→Host). Хост = пир 1, а свободный корабль в сети
/// имеет пилота-автопилота 1 — один только тест `PilotPeerId == SelfId` делал хоста
/// пилотом обоих корпусов, и второй корабль повторял ввод игрока (рецидив BUG-008).
/// Ожидание: у свободного корабля pilot = 1, но IsLocalPilot = False, input
/// выключен и |v| = 0; локально-пилотируемый корабль ровно один.
///
/// Запуск:
///   ./run-tests.sh res://tests/probes/ProbePilotAtSpawn.tscn
///   ./run-tests.sh res://tests/probes/ProbePilotAtSpawn.tscn -- --host</summary>
public partial class ProbePilotAtSpawn : Node
{
    private const int HoldStart = 30;
    private const int HoldFrames = 60;
    private const int OfflineReportFrame = HoldStart + HoldFrames + 30;
    private const int HostStartFrame = OfflineReportFrame + 10;
    private const int HostReportFrame = HostStartFrame + 150;

    private int _frames;
    private int _failures;
    private bool _hostMode;
    private bool _held;

    public override void _Ready()
    {
        foreach (string a in OS.GetCmdlineUserArgs())
            if (a == "--host")
                _hostMode = true;

        var main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(main);
    }

    public override void _Process(double delta)
    {
        _frames++;

        if (_frames == HoldStart)
        {
            Input.ActionPress("thrust_f");
            _held = true;
            Print("holding thrust_f");
        }
        else if (_frames == HoldStart + HoldFrames)
        {
            Input.ActionRelease("thrust_f");
            _held = false;
            Print("released thrust_f");
        }

        // Второй заход газа — уже в host-фазе: первый отпускается ДО подъёма
        // сервера, а после пересоздания мира именно удерживаемый thrust_f
        // различает «корабль хост-игрока» (обязан поехать) и «автопилот 1»
        // (обязан стоять: без гейта он читал бы ту же клавиатуру).
        if (_hostMode && _frames == HostStartFrame + 30)
        {
            Input.ActionPress("thrust_f");
            Print("host: holding thrust_f");
        }
        else if (_hostMode && _frames == HostReportFrame - 30)
        {
            Input.ActionRelease("thrust_f");
            Print("host: released thrust_f");
        }

        if (_frames == OfflineReportFrame)
        {
            Print("after thrust hold");
            CheckMotion(offline: true);
            if (!_hostMode)
                Finish();
        }

        if (_hostMode && _frames == HostStartFrame)
            StartHostPhase();

        if (_hostMode && _frames == HostReportFrame)
        {
            Print("host: after 150 frames of thrust hold");
            CheckMotion(offline: false);
            Finish();
        }
    }

    /// <summary>Etaп offline: из offline-мира поднимаем host-сервер (тот же путь,
    /// что кнопка «Start Server» в дебаг-панели, `withLocalPlayer: true`). Мир
    /// пересоздаётся, хост-игрок спавнится и автопристёгивается к свободному
    /// креслу; у второго корабля пилот остаётся 1 = автопилот сервера.</summary>
    private void StartHostPhase()
    {
        var nm = NetworkManager.Instance!;
        var err = nm.StartServer(withLocalPlayer: true);
        if (err != Error.Ok)
        {
            PrintErr($"ф.3: StartServer(withLocalPlayer:true) -> {err} (порт занят?)");
            _failures++;
            return;
        }
        GD.Print($"ProbePilotAtSpawn: ф.3 host поднят: role = {nm.Role}, " +
                 $"self = {nm.SelfId}, FreeShipPilotId = {nm.FreeShipPilotId}");
    }

    /// <summary>Проверка по двум кораблям: пилотируемый (пристёгнут локальный
    /// игрок) обязан ехать от удерживаемого thrust_f, свободный — иметь выключенный
    /// ввод и стоять на месте.</summary>
    private void CheckMotion(bool offline)
    {
        var nm = NetworkManager.Instance!;
        string tag = offline ? "offline" : "host";

        int seatedNetId = 0;
        foreach (var node in GetTree().GetNodesInGroup("player"))
            if (node is Player p && GodotObject.IsInstanceValid(p) &&
                !p.IsQueuedForDeletion() && p.GetMultiplayerAuthority() == nm.SelfId)
            {
                if (p.IsAttached)
                    seatedNetId = p.AttachedTo!.OwnerNetId;
            }
        if (seatedNetId == 0)
        {
            PrintErr($"[{tag}] локальный игрок не пристёгнут к креслу — проба ничего не меряет");
            _failures++;
        }

        int ships = 0, piloted = 0;
        foreach (var (id, node) in nm.Ships)
        {
            if (node is not Ship ship || !GodotObject.IsInstanceValid(ship) ||
                ship.Net == null || ship.Net.ShipNetId == 0)
                continue;
            ships++;
            bool seatedHere = ship.Net.ShipNetId == seatedNetId;
            float speed = ship.LinearVelocity.Length();
            GD.Print($"  [{tag}] ship#{ship.Net.ShipNetId}: PilotPeerId = {ship.PilotPeerId}, " +
                     $"IsLocalPilot = {ship.IsLocalPilot}, inputOn = {ship.IsProcessingInput()}, " +
                     $"|v| = {speed:F3}, seatedHere = {seatedHere}");

            if (seatedHere)
            {
                if (!ship.IsLocalPilot || !ship.IsProcessingInput())
                {
                    PrintErr($"[{tag}] корабль локального игрока без права ввода — гейт перегнут");
                    _failures++;
                }
                if (speed <= 0.01f)
                {
                    PrintErr($"[{tag}] пилотируемый корабль не поехал от thrust_f");
                    _failures++;
                }
            }
            else
            {
                if (ship.IsLocalPilot || ship.IsProcessingInput())
                {
                    PrintErr($"[{tag}] СВОБОДНЫЙ корабль числится локально-пилотируемым " +
                             "(IsLocalPilot/inputOn) — рецидив BUG-008");
                    _failures++;
                }
                if (speed > 0.01f)
                {
                    PrintErr($"[{tag}] свободный корабль поехал |v| = {speed:F3} — " +
                             "повторяет ввод игрока");
                    _failures++;
                }
            }
            if (ship.IsLocalPilot)
                piloted++;
        }

        if (ships < 2)
        {
            PrintErr($"[{tag}] кораблей в группе «ship»: {ships}, ждали ≥ 2");
            _failures++;
        }
        if (piloted > 1)
        {
            PrintErr($"[{tag}] локально-пилотируемых кораблей {piloted}: один игрок гоняет несколько корпусов");
            _failures++;
        }
    }

    private void Finish()
    {
        PrintErr(_failures == 0 ? "PROBE: PASS" : $"PROBE: FAIL ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Print(string phase)
    {
        GD.Print($"=== probe @frame {_frames} ({phase}): role = {NetworkManager.Instance.Role}, " +
                 $"self = {NetworkManager.Instance.SelfId}, " +
                 $"freePilot = {NetworkManager.Instance.FreeShipPilotId} ===");
        foreach (var (id, ship) in NetworkManager.Instance.Ships)
        {
            if (!GodotObject.IsInstanceValid(ship))
                continue;
            GD.Print($"  ship#{id}: PilotPeerId = {ship.PilotPeerId}, " +
                     $"IsLocalPilot = {ship.IsLocalPilot}, " +
                     $"inputOn = {ship.IsProcessingInput()}, " +
                     $"auth = {ship.GetMultiplayerAuthority()}");
        }
        foreach (var node in GetTree().GetNodesInGroup("player"))
            if (node is Player p && GodotObject.IsInstanceValid(p) && !p.IsQueuedForDeletion())
                GD.Print($"  player: auth = {p.GetMultiplayerAuthority()}, attached = {p.IsAttached}, " +
                         $"ownerNetId = {(p.AttachedTo != null ? p.AttachedTo.OwnerNetId.ToString() : "—")}");
    }

    private void PrintErr(string msg)
    {
        GD.PrintErr($"!!! {msg}");
    }
}
