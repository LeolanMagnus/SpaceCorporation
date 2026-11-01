using Godot;

namespace SpaceCorporation;

/// <summary>
/// Проба Э4-П7 (stage4-plan §4.4, шаг 4.10 п.26): панель дебаг-инфы содержит
/// строку «HP:» с числом. Спавним корабль (у него есть ShipHealthState), ждём
/// пару кадров (источник регистрируется в _Ready), зовём DebugInfoBus.BuildInfoText()
/// и ищем подстроку «HP:». Запуск:
/// godot --headless --path . res://tests/probes/ProbeDebugHpText.tscn
/// </summary>
public partial class ProbeDebugHpText : Node3D
{
    private Ship _ship = null!;
    private int _frames;
    private bool _finished;

    public override void _Ready()
    {
        GD.Print("=== ProbeDebugHpText ===");
        var shipScene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
        if (shipScene == null)
        {
            GD.PrintErr("Ship.tscn не загрузился");
            GetTree().Quit(1);
            return;
        }
        _ship = shipScene.Instantiate<Ship>();
        _ship.Name = "DebugShip";
        AddChild(_ship);
        var h = _ship.GetNode<ShipHealthState>("ShipHealthState");
        h.MaxHp = 100;
        h.Hp = 73;
        GD.Print($"  ship HP = {h.Hp}/{h.MaxHp}");
    }

    public override void _Process(double delta)
    {
        if (_finished)
            return;
        _frames++;
        if (_frames < 3)
            return;

        string info = DebugInfoBus.BuildInfoText();
        bool hasHp = info.Contains("HP:");
        bool hasHpValue = info.Contains("HP: 73/100");
        GD.Print($"  info text:\n{info}");
        GD.Print($"=== PROBE hasHp={hasHp} hasHpValue={hasHpValue} -> " +
                 $"{(hasHp && hasHpValue ? "OK" : "FAIL")} ===");
        _finished = true;
        GetTree().Quit(hasHp && hasHpValue ? 0 : 1);
    }
}
