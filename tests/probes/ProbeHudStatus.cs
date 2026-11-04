using Godot;

namespace SpaceCorporation;

/// <summary>Probe Этапа H2: статус-бар HUD наполняется ЖИВЫМИ данными своего
/// корабля (класс/HP/скорость/режимы), а не заглушками.
///
/// Спавним Main.tscn (offline), ждём пристёгнутого игрока, жмём thrust_f,
/// затем читаем метки HudLayer:
///   • LblShipClass ≠ "—" и ≠ "" (имеет DisplayName из каталога);
///   • LblHp вида "100 / 100" (полоса HpBar = 1);
///   • после газа LblSpeed показывает > 0 m/s;
///   • LblModes непустой и содержит ASSIST/BOOST/CHASE|COCKPIT.
/// Запуск: ./run-tests.sh res://tests/probes/ProbeHudStatus.tscn</summary>
public partial class ProbeHudStatus : Node
{
    private const int SitFrames = 40;      //.Main устоялась, игрок в кресле
    private const int ThrustStart = SitFrames + 10;
    private const int ThrustFrames = 60;
    private const int ReportFrame = ThrustStart + ThrustFrames + 30;

    private int _frames;
    private int _failures;

    public override void _Ready()
    {
        var main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(main);
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (_frames == ThrustStart) Input.ActionPress("thrust_f");
        else if (_frames == ThrustStart + ThrustFrames) Input.ActionRelease("thrust_f");
        if (_frames != ReportFrame) return;

        HudLayer? hud = null;
        foreach (Node n in GetChildren())
            if (n.Name == "Main")
                hud = n.GetNodeOrNull<HudLayer>("HudLayer");
        if (hud == null) { Fail("HudLayer не найден в Main.tscn"); Finish(); return; }

        string cls = hud.GetNode<Label>("HudRoot/BottomLeft/StatusPanel/StatusBox/ShipClass/LblShipClass").Text;
        var hpBar = hud.GetNode<ProgressBar>("HudRoot/BottomLeft/StatusPanel/StatusBox/Bars/HpBox/HpBar");
        string hp = hud.GetNode<Label>("HudRoot/BottomLeft/StatusPanel/StatusBox/Bars/HpBox/LblHp").Text;
        string speed = hud.GetNode<Label>("HudRoot/BottomLeft/StatusPanel/StatusBox/SpeedBox/LblSpeed").Text;
        string modes = hud.GetNode<Label>("HudRoot/BottomLeft/StatusPanel/StatusBox/ModeBox/LblModes").Text;

        Print($"cls=\"{cls}\" hp=\"{hp}\" bar={hpBar.Value:F2} speed=\"{speed.Replace("\n", " ")}\" modes=\"{modes.Replace("\n", " ")}\"");

        Check(cls != "" && cls != "—", $"класс корабля заполнен: \"{cls}\"");
        Check(hp == "100 / 100", $"HP-число живое: \"{hp}\"");
        Check(hpBar.Value > 0.99f, $"HpBar = {hpBar.Value:F2} ≈ 1 (полный HP)");

        // После удержания газа скорость обязана быть > 0 (инерция §3.1 п.4).
        bool speedLive = float.TryParse(speed.Split(' ')[0], out float v) && v > 0f;
        Check(speedLive, $"скорость живая после thrust_f: \"{speed.Replace("\n", " ")}\"");

        Check(modes.Contains("ASSIST") && (modes.Contains("CHASE") || modes.Contains("COCKPIT")),
            $"режимы показаны: \"{modes.Replace("\n", " ")}\"");

        // Класс из каталога = валидный ShipType с непустым DisplayName.
        var net = hud.GetNode<Label>("HudRoot/TopLeft/WorldTag/LblWorldTag").Text;
        Print($"world tag: \"{net}\"");

        Finish();
    }

    private void Finish()
    {
        GD.Print(_failures == 0 ? "PROBE PASS ProbeHudStatus"
                                : $"PROBE FAIL ProbeHudStatus: {_failures} провалов");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Print(string s) => GD.Print("  " + s);
    private void Check(bool ok, string what)
    {
        GD.Print((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok) _failures++;
    }
    private void Fail(string what) => Check(false, what);
}
