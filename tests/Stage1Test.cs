using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Автотест приёмки этапа 1. Инстанцирует Main.tscn, эмулирует F3 и клики по кнопкам,
/// печатает PASS/FAIL по каждому пункту чек-листа и завершает процесс с кодом 0/1.
/// Запуск: godot --headless --path . res://tests/Stage1Test.tscn
/// </summary>
public partial class Stage1Test : Node
{
    private int _failures;
    private int _checks;

    private DebugLayer _layer = null!;
    private RigidBody3D _probe = null!;
    private RigidBody3D? _ship;
    private int _frame;
    private int _nudgeFrame;

    public override void _Ready()
    {
        // Тест должен жить, пока дебаг-панель ставит дерево на паузу.
        ProcessMode = ProcessModeEnum.Always;

        var main = GetNodeOrNull<Node>("Main");
        if (main == null)
        {
            GD.PrintErr("FAIL: сцена Main не найдена");
            Finish();
            return;
        }

        _layer = main.GetNodeOrNull<DebugLayer>("DebugLayer");
        Check(_layer != null, "DebugLayer присутствует в Main");
        if (_layer == null)
        {
            Finish();
            return;
        }

        // --- физика: глобальная гравитация выключена
        var gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity").AsDouble();
        Check(Mathf.IsZeroApprox(gravity), $"global gravity == 0 (получено {gravity})");

        // --- имена слоёв коллизий
        Check(LayerName(1) == "Ship", $"слой 1 = Ship (получено {LayerName(1)})");
        Check(LayerName(2) == "Station", $"слой 2 = Station (получено {LayerName(2)})");
        Check(LayerName(3) == "Character", $"слой 3 = Character (получено {LayerName(3)})");
        Check(LayerName(4) == "Target", $"слой 4 = Target (получено {LayerName(4)})");

        // --- константы CollisionLayers совпадают с project.godot
        Check(CollisionLayers.Ship == 1 && CollisionLayers.Station == 2 &&
              CollisionLayers.Character == 4 && CollisionLayers.Target == 8 &&
              CollisionLayers.DockingZone == 16 && CollisionLayers.DebugHelper == 32,
              "константы CollisionLayers == степени двойки по номерам слоёв");

        // --- тестовый стенд из Main.tscn на слое Station
        var stand = main.GetNodeOrNull<StaticBody3D>("TestStand");
        Check(stand != null && stand.CollisionLayer == CollisionLayers.Station,
              "TestStand — StaticBody3D на слое Station");

        // --- шаг 1.5: корабль-заглушка в Main.tscn
        _ship = main.GetNodeOrNull<RigidBody3D>("PlayerShip");
        Check(_ship != null, "PlayerShip (RigidBody3D) присутствует в Main");
        if (_ship != null)
        {
            Check(Mathf.IsZeroApprox(_ship.GravityScale), "PlayerShip: gravity_scale = 0");
            Check(Mathf.IsZeroApprox(_ship.LinearDamp), "PlayerShip: linear_damp = 0");
            Check(Mathf.IsEqualApprox(_ship.AngularDamp, 1.5f),
                  $"PlayerShip: angular_damp = 1.5 (получено {_ship.AngularDamp})");
            Check(_ship.CollisionLayer == CollisionLayers.Ship,
                  $"PlayerShip: слой Ship (получено {_ship.CollisionLayer})");
            Check(_ship.CollisionMask == (CollisionLayers.Station | CollisionLayers.Target),
                  $"PlayerShip: маска Station|Target (получено {_ship.CollisionMask})");
            Check(_ship.GetNodeOrNull<Node3D>("Muzzle") != null,
                  "у корабля есть нос Muzzle (пригодится на этапе 3)");
        }

        // --- заглушка корабля (шаг 1.5): в невесомости не двигается
        _probe = new RigidBody3D
        {
            Name = "Probe",
            GravityScale = 0f,
            LinearDamp = 0f,
            AngularDamp = 1.5f,
            CollisionLayer = CollisionLayers.Ship,
            CollisionMask = CollisionLayers.ShipMask,
        };
        _probe.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1, 1, 1) } });
        main.AddChild(_probe);
        Check(Mathf.IsZeroApprox(_probe.GravityScale), "проба: GravityScale = 0");

        // --- DebugInfoBus: кнопка и источник через шину
        DebugInfoBus.RegisterDebugButton("BusButton", () => GD.Print("bus button pressed"));
        DebugInfoBus.RegisterInfoSource("Probe", () => $"probe vel = {_probe.LinearVelocity.Length():F3}");

        var bus = GetNodeOrNull<DebugInfoBus>("/root/DebugInfoBus");
        Check(bus != null, "autoload DebugInfoBus существует");

        // --- панель изначально закрыта, кнопка «Debug Mode» изначально скрыта
        Check(!_layer.DebugModeVisible, "дебаг-режим изначально выключен (кнопка скрыта)");
        Check(!_layer.PanelOpen, "панель изначально закрыта");
        Check(!GetTree().Paused, "дерево изначально не на паузе");

        var btn = _layer.GetNodeOrNull<Button>("DebugRoot/BtnDebugMode");
        Check(btn != null, "узел DebugRoot/BtnDebugMode существует");
        Check(btn != null && btn.Text == "Debug Mode", "подпись кнопки = «Debug Mode»");
        Check(btn != null && btn.Visible == false, "кнопка изначально Visible = false");
        Check(btn != null && btn.GetPosition() == new Vector2(8, 8), "кнопка в левом верхнем углу");

        var panel = _layer.GetNodeOrNull<PanelContainer>("DebugRoot/DebugPanel");
        Check(panel != null, "PanelContainer «DebugPanel» существует");
        Check(_layer.GetNodeOrNull<VBoxContainer>("DebugRoot/DebugPanel/VBox/Buttons") != null,
              "в панели есть VBoxContainer с кнопками");
        Check(_layer.GetNodeOrNull<Label>("DebugRoot/DebugPanel/VBox/LblDebugInfo") != null,
              "в панели есть Label «LblDebugInfo»");

        // --- тестовая кнопка из DebugLayer.Ready()
        var buttons = _layer.GetNodeOrNull<VBoxContainer>("DebugRoot/DebugPanel/VBox/Buttons");
        Check(buttons != null && buttons.GetChildCount() >= 1,
              $"тестовая кнопка зарегистрирована (кнопок: {buttons?.GetChildCount()})");
    }

    public override void _Process(double delta)
    {
        _frame++;

        switch (_frame)
        {
            case 2:
                // F3 → показать кнопку
                SendF3();
                break;
            case 4:
                Check(_layer.DebugModeVisible, "F3 включает дебаг-режим (кнопка видима)");
                Check(_layer.GetNode<Button>("DebugRoot/BtnDebugMode").Visible,
                      "после F3 BtnDebugMode.Visible == true");
                break;
            case 5:
                // клик по кнопке «Debug Mode» → панель + пауза
                _layer.GetNode<Button>("DebugRoot/BtnDebugMode").EmitSignal(
                    BaseButton.SignalName.Pressed);
                break;
            case 7:
                Check(_layer.PanelOpen, "клик по «Debug Mode» открывает панель");
                Check(GetTree().Paused, "открытая панель ставит дерево на паузу");
                break;
            case 8:
                // клик по тестовой кнопке → печать в консоль (см. вывод «debug button pressed»)
                var buttons = _layer.GetNode<VBoxContainer>("DebugRoot/DebugPanel/VBox/Buttons");
                var testBtn = FindButton(buttons, "Test");
                Check(testBtn != null, "в панели есть кнопка «Test» из DebugLayer");
                testBtn?.EmitSignal(BaseButton.SignalName.Pressed);
                break;
            case 9:
                // кнопка из шины должна появиться в той же панели
                var busButtons = _layer.GetNode<VBoxContainer>("DebugRoot/DebugPanel/VBox/Buttons");
                Check(busButtons.GetChildCount() >= 2,
                      $"DebugInfoBus.RegisterDebugButton добавил кнопку (всего: {busButtons.GetChildCount()})");
                FindButton(busButtons, "BusButton")?.EmitSignal(BaseButton.SignalName.Pressed);
                break;
            case 10:
                // DebugInfo обновляется и на паузе (у DebugLayer ProcessMode = Always)
                var label = _layer.GetNode<Label>("DebugRoot/DebugPanel/VBox/LblDebugInfo");
                Check(label.Text.Contains("FPS"), $"DebugInfo содержит FPS: [{FirstLine(label.Text)}]");
                Check(label.Text.Contains("probe vel"),
                      "DebugInfo содержит блок системы (probe vel)");
                Check(label.Text.Contains("ship:"),
                      "DebugInfo содержит блок корабля (ship: |v| ...)");
                break;
            case 11:
                // F3 при открытой панели → закрыть панель, снять паузу, скрыть кнопку
                SendF3();
                break;
            case 13:
                Check(!_layer.DebugModeVisible, "повторный F3 выключает дебаг-режим");
                Check(!_layer.PanelOpen, "панель закрылась");
                Check(!GetTree().Paused, "пауза снята");
                break;
            case 14:
                // гравитация не тянет тело: за ~10 кадров скорость осталась нулевой
                Check(Mathf.IsZeroApprox(_probe.LinearVelocity.Length()),
                      $"проба не упала: |velocity| = {_probe.LinearVelocity.Length():F4}");
                break;
            case 15:
                // кнопка корабля-заглушки: импульс → дальше инерция без гравитации.
                // Физика уже не на паузе, поэтому тело реально полетит.
                var shipButtons = _layer.GetNode<VBoxContainer>("DebugRoot/DebugPanel/VBox/Buttons");
                var nudge = FindButton(shipButtons, "Nudge Ship");
                Check(nudge != null, "ShipStub зарегистрировал кнопку «Nudge Ship» через шину");
                nudge?.EmitSignal(BaseButton.SignalName.Pressed);
                _nudgeFrame = _frame;
                break;
            case 25:
                // спустя ~10 кадров после импульса: скорость сохранилась (linear_damp = 0),
                // вертикальная составляющая не появилась (гравитации нет)
                if (_ship != null)
                {
                    var v = _ship.LinearVelocity;
                    Check(v.Length() > 4.5f,
                          $"инерция сохранилась: |v| = {v.Length():F2} м/с через {_frame - _nudgeFrame} кадров");
                    Check(Mathf.IsZeroApprox(v.Y),
                          $"гравитация не тянет корабль вниз: v.Y = {v.Y:F4}");
                }
                Finish();
                break;
        }
    }

    /// <summary>Кнопки создаются кодом, их Name генерируется движком — ищем по подписи.</summary>
    private static Button? FindButton(Node container, string text)
    {
        foreach (var child in container.GetChildren())
        {
            if (child is Button b && b.Text == text)
                return b;
        }
        return null;
    }

    private void SendF3()
    {
        var key = new InputEventKey
        {
            Keycode = Key.F3,
            PhysicalKeycode = Key.F3,
            Pressed = true,
            Echo = false,
        };
        GetViewport().PushInput(key);
        GetViewport().PushInput(new InputEventKey
        {
            Keycode = Key.F3,
            PhysicalKeycode = Key.F3,
            Pressed = false,
        });
    }

    private string LayerName(int layer) =>
        ProjectSettings.GetSetting($"layer_names/3d_physics/layer_{layer}").AsString();

    private static string FirstLine(string s)
    {
        int i = s.IndexOf('\n');
        return i < 0 ? s : s[..i];
    }

    private void Check(bool condition, string description)
    {
        _checks++;
        if (condition)
        {
            GD.Print($"PASS  {description}");
        }
        else
        {
            _failures++;
            GD.Print($"FAIL  {description}");
        }
    }

    private void Finish()
    {
        GD.Print($"RESULT checks={_checks} failures={_failures}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }
}
