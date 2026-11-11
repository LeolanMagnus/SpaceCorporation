using Godot;
using System;
using System.Collections.Generic;

namespace SpaceCorporation.Tests;

/// <summary>
/// Тест шва БЛОКА A (eva-plan А2). Запуск:
///   ./run-tests.sh res://tests/PilotInputShimTest.tscn
///
/// Инвариант блока A: сбор клавиш и курсора вынесен в `PilotInput`, и `Ship`, и
/// будущий скафандр (Блок C) обязаны собирать ОДИН И ТОТ ЖЕ `InputFrame` из
/// одинаковых клавиш и одинаковой позиции курсора (ТЗ п.9 «управление идентично
/// пилотированию»). Скафандра ещё нет, поэтому второй стороне шима служит чистый
/// `PilotInput` — ровно тот экземпляр, который Блок C передаст скафандру.
///
/// Что именно проверяет шим (и чего он НЕ подменяет):
///  • обёртка `Ship.CollectInputFrame` не добавляет и не теряет ни одного числа:
///    при тех же константах (`MaxAngularSpeed`, `MouseSensitivity`,
///    `FlightModel.FixedDt`) и том же состоянии collector'а кадр совпадает
///    ПОБИТОВО (маска, все 12 осей, look-канал, тик);
///  • пиксельный look-канал и позиционный (`PilotAim`) считаются по окну одинаково;
///  • аддитивный канал (`TestAddLookDelta`) съедается ровно один раз за шаг;
///  • режим взаимодействия и гейт «локального пилота» применяются одинаково
///    (сброс момента / обнуление look-канала) с обеих сторон;
///  • раскладка: все 13 действий `PilotInput` реально есть в `InputMap`, а
///    нажатия дают ровно ожидаемые биты («бит пропал» меряется числом битов,
///    п.16 eva-plan — не состоянием InputMap, а фактом записи в кадр);
///  • формат провода не изменился: `InputFrame.Size == 16`, round-trip
///    `ToBytes/FromBytes` сохраняет маску и look-канал.
///
/// Импорт реальных событий мыши в headless ненадёжен, поэтому курсор задаётся
/// напрямую через `HandleMouse`/поля collector'а (тот же путь кода, без
/// транспорта). Клавиши, наоборот, нажимаются НАСТОЯЩИМИ `InputEventAction`
/// (проба `tests/probes/ProbeInputHeadless` доказала, что это работает), чтобы
/// проверить всю цепочку `InputMap → InputMask`, а не только математику.
/// </summary>
public partial class PilotInputShimTest : Node3D
{
    private int _pass;
    private int _fail;
    private readonly List<string> _failures = new();

    private Ship _ship = null!;

    /// <summary>Вторая сторона шима: тот же collector, что получит скафандр
    /// (Блок C), но без корабля вокруг.</summary>
    private readonly PilotInput _suit = new();

    private int _frame;
    private bool _keysPushed;

    /// <summary>Действия, которые тест жмёт по-настоящему (5 штук — смесь тяги,
    /// момента и форсажа, чтобы проверить и `LinearBits`, и `AngularBits`, и
    /// отдельный бит `Boost`).</summary>
    private static readonly string[] Pressed =
    {
        PilotInput.ActThrustF,
        PilotInput.ActThrustB,
        PilotInput.ActTorquePitchDown,
        PilotInput.ActTorqueRollLeft,
        PilotInput.ActBoost,
    };

    private static readonly uint PressedMask =
        InputMask.ThrustF | InputMask.ThrustB | InputMask.TorquePitchDown
        | InputMask.TorqueRollLeft | InputMask.Boost;

    public override void _Ready()
    {
        GD.Print("=== PilotInputShimTest: начало ===");

        // Окно поднимаем до 1280×720 (как в Stage2Test): в headless по умолчанию
        // 64×64, полный радиус доворота = ¼ высоты = 16 px < слепой зоны 100 px,
        // и позиционный look-канал был бы физически недоступен.
        GetWindow().Size = new Vector2I(1280, 720);

        PackedScene? scene = GD.Load<PackedScene>("res://scenes/Ship.tscn");
        if (scene == null)
        {
            Check(false, "Ship.tscn не загрузился — шим нечего подставлять");
            Finish();
            return;
        }
        _ship = scene.Instantiate<Ship>();
        _ship.Name = "ShimShip";
        AddChild(_ship);
        // Локальный пир оффлайн-процесса — 1 (NetworkManager.SelfId), как в
        // Stage2Test/ProbeChaseAim: без этого IsLocalPilot ложно и collector глух.
        _ship.TestSetPilotForTest(1);
    }

    public override void _Process(double delta)
    {
        _frame++;

        // Кадр 2: жмём настоящие действия. Состояние InputMap обновляется не в
        // момент пуша, а при обработке очереди ввода движком, поэтому собираем на
        // кадре 4 (тот же приём, что в ProbeInputHeadless).
        if (_frame == 2 && !_keysPushed)
        {
            _keysPushed = true;
            foreach (string a in Pressed)
            {
                GetViewport().PushInput(new InputEventAction { Action = a, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = a, Pressed = true });
            }
            return;
        }

        if (_frame == 4)
        {
            RunLayoutTests();
            RunPressedKeyTests();
            RunCursorParityTests();
            RunAccumulatedChannelTests();
            RunInteractionModeTests();
            RunInactivePilotTests();
            RunWireFormatTests();
            Finish();
        }
    }

    // ==================================================================
    // П.1 — раскладка: все 13 действий живут в InputMap (связь именем)
    // ==================================================================
    private void RunLayoutTests()
    {
        GD.Print("[Shim] п.1: раскладка PilotInput ↔ InputMap");
        string[] actions =
        {
            PilotInput.ActThrustF, PilotInput.ActThrustB, PilotInput.ActThrustL,
            PilotInput.ActThrustR, PilotInput.ActThrustU, PilotInput.ActThrustD,
            PilotInput.ActTorquePitchUp, PilotInput.ActTorquePitchDown,
            PilotInput.ActTorqueYawLeft, PilotInput.ActTorqueYawRight,
            PilotInput.ActTorqueRollLeft, PilotInput.ActTorqueRollRight,
            PilotInput.ActBoost,
        };
        int missing = 0;
        foreach (string a in actions)
            if (!InputMap.HasAction(a))
            {
                missing++;
                GD.PrintErr($"  нет действия в InputMap: {a}");
            }
        Check(actions.Length == 13, "п.1: набор действий пилота — 13 (тяга 6, момент 6, форсаж)");
        Check(missing == 0, $"п.1: все 13 действий объявлены в InputMap (пропусков: {missing})");
    }


    // ==================================================================
    // П.2 — нажатые клавиши дают ровно ожидаемые биты (цепочка InputMap→InputMask)
    // ==================================================================
    private void RunPressedKeyTests()
    {
        GD.Print("[Shim] п.2: настоящие нажатия -> биты маски");

        // Курсор глушим (HasCursor = false на обеих сторонах), чтобы в кадр
        // прошла только маска: look-канал проверяется отдельно в п.3.
        _ship.Pilot.HasCursor = false;
        _suit.Active = true;
        _suit.InteractionMode = false;
        _suit.HasCursor = false;

        uint tick = _ship.Tick;
        InputFrame a = _ship.TestCollectInputFrame();
        InputFrame b = _suit.Collect(tick, ViewportSize, _ship.MaxAngularSpeed,
            FlightModel.FixedDt, _ship.MouseSensitivity);

        bool sawAll = (a.Mask & PressedMask) == PressedMask;
        Check(sawAll, $"п.2: пять реальных нажатий дали все пять битов " +
                      $"(mask = 0b{Convert.ToString(a.Mask, 2)}, ждём 0b{Convert.ToString(PressedMask, 2)})");
        Check(PilotInput.BitCount(a.Mask) == 5,
            $"п.2: ровно 5 битов (лишних нет): {PilotInput.BitCount(a.Mask)}");
        Check(a.Mask == b.Mask, $"п.2: маска Ship == маска чистого collector'а (0x{a.Mask:X} == 0x{b.Mask:X})");

        // Оси восстановлены из маски (FromMask) — по одной на каждый бит.
        Check(a.AxisF == 1f && a.AxisB == 1f && a.AxisPitchDown == 1f &&
              a.AxisRollLeft == 1f && a.AxisYawLeft == 0f && a.AxisL == 0f,
            "п.2: оси соответствуют битам (AxisF/B/PitchDown/RollLeft = 1, прочие 0)");
        Check(a.Tick == tick && b.Tick == tick, $"п.2: тик передан без изменений ({tick})");
    }

    // ==================================================================
    // П.3 — позиционный look-канал (PilotAim) считается одинаково
    // ==================================================================
    private void RunCursorParityTests()
    {
        GD.Print("[Shim] п.3: курсор-прицел -> одинаковый look-канал");

        // Один и тот же курсор обеим сторонам — через HandleMouse (тот же путь
        // накопления, что у реальной мыши, только без транспорта событий).
        var motion = new InputEventMouseMotion
        {
            Position = new Vector2(1280f * 0.5f + 120f, 720f * 0.5f - 90f),
            Relative = new Vector2(120f, -90f),
        };
        _ship.Pilot.Active = _ship.IsLocalPilot;
        _ship.Pilot.InteractionMode = false;
        _ship.Pilot.HandleMouse(motion, ViewportSize);
        _suit.Active = true;
        _suit.InteractionMode = false;
        _suit.HandleMouse(motion, ViewportSize);

        Check(_ship.Pilot.CursorPx == _suit.CursorPx && _ship.Pilot.HasCursor && _suit.HasCursor,
            $"п.3: курсор записан одинаково и с битом «курсор есть» ({_ship.Pilot.CursorPx} == {_suit.CursorPx})");

        uint tick = _ship.Tick;
        InputFrame a = _ship.TestCollectInputFrame();
        InputFrame b = _suit.Collect(tick, ViewportSize, _ship.MaxAngularSpeed,
            FlightModel.FixedDt, _ship.MouseSensitivity);

        Check(a.LookYaw == b.LookYaw && a.LookPitch == b.LookPitch,
            $"п.3: look-канал побитово совпадает (yaw {a.LookYaw:F6} == {b.LookYaw:F6}, " +
            $"pitch {a.LookPitch:F6} == {b.LookPitch:F6})");
        // Внутри слепой зоны нос не крутится вообще — проверяем, что вне она живая,
        // иначе «совпало» могло бы значить «оба нуля от глухого канала».
        float radius = (new Vector2(a.LookYaw, a.LookPitch)).Length();
        Check(radius > 0f, $"п.3: курсор вне слепой зоны даёт ненулевой доворот (|look| = {radius:F6})");
        Check(radius <= _ship.MaxAngularSpeed * FlightModel.FixedDt + 1e-6f,
            $"п.3: доворот не выше потолка корпуса ({radius:F6} <= " +
            $"{_ship.MaxAngularSpeed * FlightModel.FixedDt:F6})");

        // Пиксель-пер-клик (Блок E) берёт луч из ТОЙ ЖЕ позиции курсора, что и
        // look-канал: второе поле-копия курсора появляться не должно.
        Check(_ship.AimCursorPx == _ship.Pilot.CursorPx,
            $"п.3: Ship.AimCursorPx — то же поле, что cursor прицела ({_ship.AimCursorPx} == {_ship.Pilot.CursorPx})");
    }

    // ==================================================================
    // П.4 — аддитивный пиксельный канал съедается ровно один раз
    // ==================================================================
    private void RunAccumulatedChannelTests()
    {
        GD.Print("[Shim] п.4: аддитивный канал (TestAddLookDelta) -> один раз за шаг");

        // Позиционный канал глушим (HasCursor = false), чтобы в look попали ТОЛЬКО
        // накопленные пиксели шва — иначеparity мерялась бы на PilotAim повторно.
        _ship.Pilot.HasCursor = false;
        _suit.HasCursor = false;
        _ship.Pilot.ClearLookDelta();
        _suit.ClearLookDelta();

        _ship.TestAddLookDelta(new Vector2(30f, -12f));
        _suit.AddLookDeltaPx(new Vector2(30f, -12f));

        uint tick = _ship.Tick;
        InputFrame a = _ship.TestCollectInputFrame();
        InputFrame b = _suit.Collect(tick, ViewportSize, _ship.MaxAngularSpeed,
            FlightModel.FixedDt, _ship.MouseSensitivity);

        Check(a.LookYaw == b.LookYaw && a.LookPitch == b.LookPitch,
            $"п.4: одинаковые пиксели -> одинаковые радианы ({a.LookYaw:F6}, {a.LookPitch:F6})");
        Check(Mathf.Abs(a.LookYaw - 30f * _ship.MouseSensitivity) < 1e-6f &&
              Mathf.Abs(a.LookPitch + 12f * _ship.MouseSensitivity) < 1e-6f,
            $"п.4: пиксели пересчитаны чувствительностью корпуса (30px*0.0025 = {30f * _ship.MouseSensitivity:F6})");

        Check(_ship.Pilot.AccumulatedLookPx == Vector2.Zero && _suit.AccumulatedLookPx == Vector2.Zero,
            "п.4: накопленный канал обнулён после сбора (не задваивается на следующем шаге)");

        InputFrame a2 = _ship.TestCollectInputFrame();
        InputFrame b2 = _suit.Collect(tick, ViewportSize, _ship.MaxAngularSpeed,
            FlightModel.FixedDt, _ship.MouseSensitivity);
        Check(a2.LookYaw == 0f && a2.LookPitch == 0f && b2.LookYaw == 0f && b2.LookPitch == 0f,
            "п.4: второй сбор без новых пикселей даёт нулевой look");
    }

    // ==================================================================
    // П.5 — режим взаимодействия: момент глушится, тяга живёт (одинаково)
    // ==================================================================
    private void RunInteractionModeTests()
    {
        GD.Print("[Shim] п.5: режим взаимодействия одинаков с обеих сторон");

        _ship.SetInteractionMode(true);
        _suit.InteractionMode = true;
        _ship.Pilot.HasCursor = false;
        _suit.HasCursor = false;

        uint tick = _ship.Tick;
        InputFrame a = _ship.TestCollectInputFrame();
        InputFrame b = _suit.Collect(tick, ViewportSize, _ship.MaxAngularSpeed,
            FlightModel.FixedDt, _ship.MouseSensitivity);

        Check(a.Mask == b.Mask, $"п.5: маска в Interaction совпадает (0x{a.Mask:X} == 0x{b.Mask:X})");
        Check((a.Mask & InputMask.AngularBits) == 0,
            $"п.5: биты момента вырезаны (mask = 0x{a.Mask:X})");
        Check((a.Mask & InputMask.ThrustF) != 0 && (a.Mask & InputMask.Boost) != 0,
            "п.5: тяга и форсаж в Interaction живы (игрок освобождает мышь, не двигатели)");

        _ship.SetInteractionMode(false);
        _suit.InteractionMode = false;
        uint tick2 = _ship.Tick;
        InputFrame c = _ship.TestCollectInputFrame();
        InputFrame d = _suit.Collect(tick2, ViewportSize, _ship.MaxAngularSpeed,
            FlightModel.FixedDt, _ship.MouseSensitivity);
        Check((c.Mask & InputMask.AngularBits) != 0 && c.Mask == d.Mask,
            $"п.5: после выхода из Interaction момент вернулся, кадр снова идентичен (0x{c.Mask:X} == 0x{d.Mask:X})");
    }

    // ==================================================================
    // П.6 — не-локальный пилот: collector глух к курсору (гейт одним флагом)
    // ==================================================================
    private void RunInactivePilotTests()
    {
        GD.Print("[Shim] п.6: чужой пилот — курсор не копится, look-нулевой");

        _ship.SetInteractionMode(false);
        // Гейт «локального пилота» в Ship выставил бы Active сам (HandleMouseMotion /
        // CollectInputFrame пишут Active = IsLocalPilot), но здесь collector
        // дёргается напрямую — поэтому флаг снимаем руками, как это делает
        // снятие с кресла. Тест п.16 и требует мерять именно факт записи в поля.
        _ship.Pilot.Active = false;
        _suit.Active = false;
        _ship.Pilot.HasCursor = false;
        _suit.HasCursor = false;
        _ship.Pilot.ClearLookDelta();
        _suit.ClearLookDelta();

        var motion = new InputEventMouseMotion { Position = new Vector2(1200f, 60f), Relative = new Vector2(90f, -60f) };
        _ship.Pilot.HandleMouse(motion, ViewportSize);   // Active false (чужой пир)
        _suit.HandleMouse(motion, ViewportSize);          // Active false явно

        Check(!_ship.Pilot.HasCursor && !_suit.HasCursor,
            "п.6: при снятом флаге Active бит «курсор есть» не выставляется");
        Check(_ship.Pilot.CursorPx != new Vector2(1200f, 60f),
            $"п.6: позиция прицела не обновляется чужому пиру ({_ship.Pilot.CursorPx})");
        Check(_ship.Pilot.LastMousePx == new Vector2(1200f, 60f) && _suit.LastMousePx == motion.Position,
            "п.6: LastMousePx пишется всегда (луч выстрела живёт без look-канала)");

        _ship.TestSetPilotForTest(999);   // Ship: Active снова false по роли
        uint tick = _ship.Tick;
        InputFrame a = _ship.TestCollectInputFrame();
        InputFrame b = _suit.Collect(tick, ViewportSize, _ship.MaxAngularSpeed,
            FlightModel.FixedDt, _ship.MouseSensitivity);
        Check(a.LookYaw == 0f && a.LookPitch == 0f && b.LookYaw == 0f && b.LookPitch == 0f,
            "п.6: look-канал обнулён на обеих сторонах (несуществующий курсор не крутит объект)");
        _ship.TestSetPilotForTest(1);
    }

    // ==================================================================
    // П.7 — формат провода не изменился (инвариант блока A)
    // ==================================================================
    private void RunWireFormatTests()
    {
        GD.Print("[Shim] п.7: InputFrame на проводе — 16 байт, round-trip точен");

        Check(InputFrame.Size == 16, $"п.7: размер кадра = {InputFrame.Size} байт (ждём 16)");

        var motion = new InputEventMouseMotion { Position = new Vector2(700f, 300f), Relative = new Vector2(15f, 8f) };
        _ship.Pilot.Active = true;
        _ship.Pilot.InteractionMode = false;
        _ship.Pilot.HandleMouse(motion, ViewportSize);
        InputFrame a = _ship.TestCollectInputFrame();
        byte[] bytes = a.ToBytes();
        InputFrame back = InputFrame.FromBytes(bytes);

        Check(bytes.Length == InputFrame.Size, $"п.7: ToBytes вернул {bytes.Length} байт");
        Check(back.Tick == a.Tick && back.Mask == a.Mask &&
              back.LookYaw == a.LookYaw && back.LookPitch == a.LookPitch,
            "п.7: round-trip сохранил тик/маску/look побитово");
        Check(a.AxisF == back.AxisF && a.AxisB == back.AxisB && a.AxisPitchDown == back.AxisPitchDown &&
              a.AxisRollLeft == back.AxisRollLeft,
            "п.7: на приёмной стороне оси восстановлены из маски те же самые");
    }

    // ==================================================================
    // Служебное
    // ==================================================================
    private Vector2 ViewportSize
    {
        get
        {
            Viewport? vp = GetViewport();
            return vp == null ? Vector2.Zero : vp.GetVisibleRect().Size;
        }
    }

    private void Check(bool condition, string label)
    {
        if (condition)
        {
            _pass++;
            GD.Print($"  [PASS] {label}");
        }
        else
        {
            _fail++;
            _failures.Add(label);
            GD.PrintErr($"  [FAIL] {label}");
        }
    }

    private void Finish()
    {
        GD.Print($"=== PilotInputShimTest: {_pass} PASS / {_fail} FAIL ===");
        foreach (string f in _failures)
            GD.PrintErr($"  провал: {f}");
        GetTree().Quit(_fail == 0 ? 0 : 1);
    }
}
