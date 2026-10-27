using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Проба перед тестами п.4-п.6 (шаг 2.2). Отвечает на три вопроса, от ответа на
/// которые зависит конструкция автотеста:
///   1) вызывает ли PhysicsServer3D `_integrate_forces` у НАСТОЯЩЕго Ship.tscn
///      в headless-процессе (у ProbeBody из Stage2Test вызывал — но у Ship есть
///      Freeze/авторитет/NetState, которые могут мешать);
///   2) доводит ли эмуляция событий до `Input.IsActionPressed`, то есть можно ли
///      в тесте проверить реальный клавиатурный путь без швов;
///   3) приходит ли в `_Input` узла `InputEventMouseMotion`, посланный через
///      viewport, и что делает `Input.MouseMode = Hidden` в headless.
/// Запуск: godot --headless --path . res://tests/probes/ProbeInputHeadless.tscn
/// Выход: 0, если физкадры и состояние действий подтверждены (мышь в headless
/// не приходит — это отдельный вывод пробы, он зафиксирован в комментарии).
/// </summary>
public partial class ProbeInputHeadless : Node3D
{
    private Ship _ship = null!;
    private int _frames;
    private int _ticksAtStart;
    private bool _keyPushed;
    private bool _actionSeen;
    private bool _rootMouseSeen;
    private bool _shipMouseSeen;
    private ProbeMouseCollector _collector = null!;
    private bool _aAction, _aKey, _bAction, _cAction, _dAction;
    private Vector2 _mouseRelativeSeen;
    private string _mouseModeAfter = "";

    public override void _Ready()
    {
        GD.Print("=== ProbeInputHeadless ===");

        var scene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
        GD.Print($"Ship.tscn загружен: {scene != null}");
        _ship = (Ship)scene.Instantiate();
        _ship.Name = "ProbeShip";
        AddChild(_ship);
        GD.Print($"ship: auth = {_ship.GetMultiplayerAuthority()}, netid = {_ship.Net?.ShipNetId}, " +
                 $"custom_integrator = {_ship.CustomIntegrator}, freeze = {_ship.Freeze}, " +
                 $"can_sleep = {_ship.CanSleep}, mass = {_ship.Mass}, process_input = {_ship.IsProcessingInput()}");

        Input.MouseMode = Input.MouseModeEnum.Hidden;
        _mouseModeAfter = Input.MouseMode.ToString();

        // Гипотеза: у CollisionObject3D (RigidBody3D) `_input` вызывается только
        // через пикинг вьюпорта — мышь до Ship не доходит именно поэтому.
        // Проверка: обычный Node3D-«сборщик» под Ship должен получать мышь.
        _ship.Set("input_pickable", true);
        _collector = new ProbeMouseCollector { Name = "MouseCollector" };
        _ship.AddChild(_collector);
        GD.Print($"ship: input_pickable = {_ship.Get("input_pickable")}, collector = {_collector.GetPath()}");
    }

    public override void _Process(double delta)
    {
        _frames++;

        if (_frames == 3 && !_keyPushed)
        {
            _keyPushed = true;
            _ticksAtStart = (int)_ship.Tick;

            // Вариант A: InputEventKey через Input.ParseInputEvent.
            var key = new InputEventKey { Keycode = Key.W, PhysicalKeycode = Key.W, Pressed = true, Echo = false };
            Input.ParseInputEvent(key);
            _aAction = Input.IsActionPressed("thrust_f");
            _aKey = Input.IsKeyPressed(Key.W);

            // Вариант B: тот же ключ через GetViewport().PushInput.
            GetViewport().PushInput(new InputEventKey { Keycode = Key.A, PhysicalKeycode = Key.A, Pressed = true });
            _bAction = Input.IsActionPressed("thrust_l");

            // Вариант C: готовое InputEventAction через ParseInputEvent.
            Input.ParseInputEvent(new InputEventAction { Action = "thrust_u", Pressed = true });
            _cAction = Input.IsActionPressed("thrust_u");

            // Вариант D: InputEventAction через PushInput.
            GetViewport().PushInput(new InputEventAction { Action = "thrust_d", Pressed = true });
            _dAction = Input.IsActionPressed("thrust_d");

            GetViewport().PushInput(new InputEventMouseMotion { Relative = new Vector2(37f, -11f) });
        }

        // Состояние действий обновляется НЕ в момент пуша, а при обработке очереди
        // ввода движком (до физкадра), поэтому смотрим на несколько кадров подряд.
        // Кадр шага (`LocalInput`) логируем тоже каждый кадр: `_lookDelta` шаг
        // обнуляет, и «ноль в кадре 20» ничего не доказывает.
        if (_frames <= 6)
            GD.Print($"  f{_frames}: MouseMode = {Input.MouseMode}");

        if (_frames >= 3 && _frames <= 10)
        {
            var li = _ship.LocalInput;
            GD.Print($"  f{_frames}: tick={_ship.Tick} mask={li.Mask} yaw={li.LookYaw:F4} pitch={li.LookPitch:F4} " +
                     $"|v|={_ship.LinearVelocity.Length():F3} |w|={_ship.AngularVelocity.Length():F4} " +
                     $"rot={_ship.GlobalRotation} proc_input={_ship.IsProcessingInput()}");
        }

        if (_frames == 10)
        {
            _actionSeen = Input.IsActionPressed("thrust_f") && Input.IsActionPressed("thrust_u");
            GD.Print($"  сразу после пуша: A(key/Parse)={_aAction}/{_aKey} B(key/Push)={_bAction} " +
                     $"C(action/Parse)={_cAction} D(action/Push)={_dAction}");
            GD.Print($"  сейчас: thrust_f={Input.IsActionPressed("thrust_f")} thrust_u={Input.IsActionPressed("thrust_u")} " +
                     $"thrust_l={Input.IsActionPressed("thrust_l")} thrust_d={Input.IsActionPressed("thrust_d")}, " +
                     $"MouseMode after set = {_mouseModeAfter}");
        }

        if (_frames == 20)
        {
            // Мышь, дошедшая до Ship, уже израсходована моделью (шаг обнуляет
            // _lookDelta), поэтому вывод пробы — в строках f3..f10 выше.
            GD.Print($"frames={_frames} ship.Tick = {_ship.Tick} (было {_ticksAtStart}), " +
                     $"pos = {_ship.GlobalPosition}, |v| = {_ship.LinearVelocity.Length():F3}, " +
                     $"|w| = {_ship.AngularVelocity.Length():F4}, " +
                     $"hull pos = {_ship.GetNodeOrNull<Node3D>("ShipHull")?.GlobalPosition}");
            GD.Print($"root _Input mouse = {_rootMouseSeen}, relative = {_mouseRelativeSeen}, " +
                     $"collector accum = {_collector.Accum}");
            GD.Print($"=== PROBE RESULT: physics={_ship.Tick > _ticksAtStart} action={_actionSeen} " +
                     $"root_mouse={_rootMouseSeen} collector_mouse={_collector.Accum != Vector2.Zero} -> " +
                     $"{(_ship.Tick > _ticksAtStart && _actionSeen ? "OK" : "FAIL")} ===");
            GetTree().Quit(_ship.Tick > _ticksAtStart && _actionSeen ? 0 : 1);
        }
    }

    /// <summary>Ловим мышь, чтобы понять: доходит ли она до узлов вообще.
    /// Пишем в поле пробы, а не в Ship — проба проверяет транспорт, а не логику.</summary>
    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion m)
        {
            _rootMouseSeen = true;
            _mouseRelativeSeen = m.Relative;
        }
    }
}

/// <summary>Обычный Node3D (НЕ CollisionObject3D) — проверяем, доходит ли до него
/// мышь, посланная через viewport. Если да — пилотирование мышь принимает именно
/// такой «сборщик» под кораблём, а не сам RigidBody3D.</summary>
public partial class ProbeMouseCollector : Node3D
{
    public Vector2 Accum;

    public override void _Input(InputEvent @event)
    {
        if (@event is InputEventMouseMotion m)
        {
            Accum += m.Relative;
            GD.Print($"    collector _Input: relative = {m.Relative}, accum = {Accum}, " +
                     $"mouse_mode = {Input.MouseMode}");
        }
    }
}
