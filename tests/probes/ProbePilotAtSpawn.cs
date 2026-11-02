using Godot;

namespace SpaceCorporation;

/// <summary>Проба BUG-008 (повторная): спавним Main.tscn в offline и проверяем
/// симптом из баг-листа — «второй корабль тоже реагирует на ввод игрока».
///
/// Фаза 1: читаем PilotPeerId / IsLocalPilot / включён ли ввод у обоих кораблей.
/// Фаза 2: жмём thrust_f 60 кадров и смотрим, какое тело поехало — ехать должен
/// только пристёгнутый корабль (#1), свободный (#2) обязан остаться неподвижным.
///
/// Ожидание: ship#1 pilot=1, IsLocalPilot=True, поехал; ship#2 pilot=0,
/// IsLocalPilot=False, input выключен, |v| = 0.
/// Запуск: ./run-tests.sh res://tests/probes/ProbePilotAtSpawn.tscn</summary>
public partial class ProbePilotAtSpawn : Node
{
    private const int HoldStart = 30;
    private const int HoldFrames = 60;
    private const int ReportFrame = HoldStart + HoldFrames + 30;

    private int _frames;
    private bool _held;

    public override void _Ready()
    {
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

        if (_frames != 5 && _frames != ReportFrame)
            return;

        Print(_frames == 5 ? "after spawn" : "after thrust hold");
        if (_frames == ReportFrame)
        {
            // Инерция (§3.1 п.4): после отпускания газа пристёгнутый корабль
            // продолжает лететь (|v| > 0), свободный — обязан стоять на месте.
            foreach (var (id, ship) in NetworkManager.Instance.Ships)
                if (GodotObject.IsInstanceValid(ship))
                    GD.Print($"  ship#{id}: |v| = {ship.LinearVelocity.Length():F3} м/с " +
                             $"({(ship.LinearVelocity.Length() > 0.01f ? "летит" : "стоит")})");
            GetTree().Quit();
        }
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
            if (node is Player p && GodotObject.IsInstanceValid(p))
                GD.Print($"  player: attached = {p.IsAttached}, " +
                         $"ownerNetId = {(p.AttachedTo != null ? p.AttachedTo.OwnerNetId.ToString() : "—")}");
    }
}
