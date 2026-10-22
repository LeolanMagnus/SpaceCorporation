using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Прогон ProbeNest.tscn: вложенный CharacterBody3D (игрок пристёгнут к креслу
/// корабля) и перепривязка в рантайме. Проверяет гипотезу задела на этап 6:
///   1) игрок, пристёгнутый к кораблю, летает вместе с ним;
///   2) Detach возвращает игрока в корень сцены, и он больше не следует за кораблём;
///   3) AttachTo к точке другой сцены (стенд «как станция») работает;
///   4) повторная пристёжка к креслу ставит игроку локальный PlayerOffset.
/// </summary>
public partial class ProbeNest : Node3D
{
    private Ship _ship = null!;
    private Player _player = null!;
    private AttachPoint _seat = null!;
    private AttachPoint _stand = null!;

    private int _frame;
    private Vector3 _shipPosAtImpulse;
    private Vector3 _playerPosAtImpulse;
    private Vector3 _shipPosAtDetach;
    private Vector3 _playerPosAtDetach;

    public override void _Ready()
    {
        _ship = GetNode<Ship>("Ship");
        _player = GetNode<Player>("Player");
        _seat = _ship.GetNode<AttachPoint>("AttachPoints/Seat");
        _stand = GetNode<AttachPoint>("TestStand/StandPoint");
    }

    public override void _PhysicsProcess(double delta)
    {
        _frame++;

        if (_frame == 5)
        {
            // К этому шагу Player._PhysicsProcess уже авто-пристегнул игрока к Seat.
            GD.Print($"PROBE autoattach: attached={_player.IsAttached} point={_player.AttachedTo?.Name} " +
                     $"parent={_player.GetParent().GetName()}");
            GD.Print($"PROBE camera: {(_player.GetActiveCamera() != null ? "found" : "MISSING")}");

            _shipPosAtImpulse = _ship.GlobalPosition;
            _playerPosAtImpulse = _player.GlobalPosition;
            _ship.ApplyImpulse(new Vector3(0, 0, -40f));
            GD.Print("PROBE: ship impulse -Z");
        }

        if (_frame == 20)
        {
            var shipMoved = _ship.GlobalPosition - _shipPosAtImpulse;
            var playerMoved = _player.GlobalPosition - _playerPosAtImpulse;
            GD.Print($"PROBE CHECK1 nested-follows-ship: shipMoved={shipMoved.Length():F3} " +
                     $"playerMoved={playerMoved.Length():F3} " +
                     $"ok={(shipMoved.Length() > 0.5f && (playerMoved - shipMoved).Length() < 0.05f)}");

            _shipPosAtDetach = _ship.GlobalPosition;
            _playerPosAtDetach = _player.GlobalPosition;
            _player.Detach();
            // Даём кораблю ещё импульс: после открепления он должен улетать, а игрок — стоять.
            _ship.ApplyImpulse(new Vector3(0, 0, -40f));
            GD.Print($"PROBE: detached parent={_player.GetParent().GetName()} " +
                     $"playerGlobal={Fmt(_player.GlobalPosition)} (was {Fmt(_playerPosAtDetach)})");
        }

        if (_frame == 40)
        {
            var shipMoved = _ship.GlobalPosition - _shipPosAtDetach;
            var playerMoved = _player.GlobalPosition - _playerPosAtDetach;
            GD.Print($"PROBE CHECK2 free-flight: shipMoved={shipMoved.Length():F3} " +
                     $"playerMoved={playerMoved.Length():F3} " +
                     $"ok={(shipMoved.Length() > 0.5f && playerMoved.Length() < 0.05f)}");

            _player.AttachTo(_stand);
            GD.Print($"PROBE: attached to stand -> parent={_player.GetParent().GetName()} " +
                     $"playerGlobal={Fmt(_player.GlobalPosition)} standGlobal={Fmt(_stand.GlobalPosition)}");
        }

        if (_frame == 50)
        {
            var standGlobal = _stand.GlobalPosition;
            var expected = standGlobal + Vector3.Up * _stand.PlayerOffset.Y;
            GD.Print($"PROBE CHECK3 attached-to-stand: playerGlobal={Fmt(_player.GlobalPosition)} " +
                     $"expected={Fmt(expected)} ok={(_player.GlobalPosition - expected).Length() < 0.05f}");

            _player.AttachTo(_seat);
            GD.Print($"PROBE: re-attached to seat -> parent={_player.GetParent().GetName()}");
        }

        if (_frame == 60)
        {
            GD.Print($"PROBE CHECK4 reattach: localOffset={_player.Position} " +
                     $"seatPlayerOffset={_seat.PlayerOffset} " +
                     $"ok={(_player.Position - _seat.PlayerOffset).Length() < 0.001f}");
            var camGlobal = _player.GetActiveCamera()!.GlobalPosition;
            var rigOffset = camGlobal - _player.GlobalPosition;
            GD.Print($"PROBE CHECK5 camera-follows-player: offset={rigOffset} " +
                     $"ok={(rigOffset.Length() > 0.5f && rigOffset.Y > 0.5f)}");
            GD.Print("PROBE DONE");
            GetTree().Quit();
        }
    }

    private static string Fmt(Vector3 v) => $"({v.X:F2}, {v.Y:F2}, {v.Z:F2})";
}
