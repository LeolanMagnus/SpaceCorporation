using System;
using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// Пилотский ввод (eva-plan Блок A, п.1-6): единый накопитель клавиш и курсора,
/// из которого и корабль, и скафандр (Блок C) собирают ОДИН И ТОТ ЖЕ `InputFrame`.
///
/// Вынесен из `Ship` по трём причинам:
///  1) скафандру нужны ровно те же `thrust_*`/`torque_*`/`boost` и та же схема
///     «доворот на позицию курсора» (ТЗ п.9 «управление идентично пилотированию»);
///     дублировать `Input.IsActionPressed` нельзя — раскладка разъедется;
///  2) пиксель-пер-клик (Блок E) требует луч из камеры по курсору, он уже есть в
///     `Ship.AimOnShip` — второй копии быть не должно (п.6);
///  3) тест п.16: проверка «бит пропал» обязана меряться НЕ состоянием InputMap
///     (имитация мыши/клавиш в headless не надёжна), а фактом записи в поля
///     collector'а. Поэтому позиция курсора и бит «курсор есть» пишутся под ОДНИМ
///     флагом-гейтом collector'а (<see cref="Active"/>) — тест управляет им.
///
/// ИНВАРИАНТ блока A (ТЗ п.5): байт-в-байт те же числа на входе `FlightModel.Step`
/// и тот же `InputFrame.Size = 16` в `RpcSubmitInput`. Класс только СОБИРАЕТ кадр;
/// решение о том, чей ввод использовать (гейт `IsLocalPilot`, очередь кадров
/// сервера, автопилот стенда), принимает вызывающий — как и раньше.
/// </summary>
public sealed class PilotInput
{
    // ── имена действий (те же, что в project.godot [input]) ──────────────────
    public const string ActThrustF = "thrust_f";
    public const string ActThrustB = "thrust_b";
    public const string ActThrustL = "thrust_l";
    public const string ActThrustR = "thrust_r";
    public const string ActThrustU = "thrust_u";
    public const string ActThrustD = "thrust_d";
    public const string ActTorquePitchUp = "torque_pitch_up";
    public const string ActTorquePitchDown = "torque_pitch_down";
    public const string ActTorqueYawLeft = "torque_yaw_left";
    public const string ActTorqueYawRight = "torque_yaw_right";
    public const string ActTorqueRollLeft = "torque_roll_left";
    public const string ActTorqueRollRight = "torque_roll_right";
    public const string ActBoost = "boost";

    /// <summary>Пары «имя действия → бит маски». Строится один раз рефлексией по
    /// полям <see cref="InputMask"/>: имя действия = имя бита в нижнем регистре с
    /// подчёркиваниями (`ThrustF` → `thrust_f`, `TorquePitchUp` →
    /// `torque_pitch_up`). Связь «константа ↔ раскладка» держится на ИМЕНИ, а не на
    /// номере бита: новый бит в `InputMask` + действие в `InputMap` работают без
    /// правки здесь; опечатка в имени даёт нулевой бит (тест ловит «бит не
    /// выставлен»), а не молчаливый сдвиг соседних.</summary>
    private static readonly (string Action, uint Bit)[] Bindings = BuildBindings();

    private static (string Action, uint Bit)[] BuildBindings()
    {
        var list = new List<(string Action, uint Bit)>();
        foreach (var f in typeof(InputMask).GetFields(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (f.FieldType != typeof(uint))
                continue;                    // LinearBits/AngularBits — маски, не действия
            string name = f.Name;
            if (name == nameof(InputMask.LinearBits) || name == nameof(InputMask.AngularBits))
                continue;
            list.Add((ToActionName(name), (uint)f.GetValue(null)!));
        }
        list.Sort((a, b) => a.Bit.CompareTo(b.Bit));
        return list.ToArray();
    }

    private static string ToActionName(string bitName)
    {
        var sb = new System.Text.StringBuilder(bitName.Length + 4);
        for (int i = 0; i < bitName.Length; i++)
        {
            char c = bitName[i];
            if (char.IsUpper(c) && i > 0 && char.IsLower(bitName[i - 1]))
                sb.Append('_');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    // ==================================================================
    // Состояние (единственное место, где живёт «курсор пилота»)
    // ==================================================================

    /// <summary>Флаг-гейт collector'а: есть ли у него локальный пилот (в `Ship` —
    /// <c>IsLocalPilot</c>, в скафандре — «локальный игрок»). Под ним пишутся поля
    /// курсора (п.16). Снимается при отстёжке/выходе из режима.</summary>
    public bool Active;

    /// <summary>Режим взаимодействия (мышь кликает по UI, а не крутит нос):
    /// обнуляет биты момента и look-канал, но НЕ тягу — игрок освобождает мышь
    /// для интерфейса, а не глушит двигатели (поведение этапа 3).</summary>
    public bool InteractionMode;

    /// <summary>Курсор-прицел, экранные пиксели. Центр экрана = направление носа
    /// (режим пилотирования 2.0, не дельта мыши).</summary>
    public Vector2 CursorPx;

    /// <summary>Приходило ли вообще мышиное событие с тех пор, как collector
    /// стал активен. Без этого гейта в headless несуществующий курсор (0,0) =
    /// угол окна крутил бы корабль.</summary>
    public bool HasCursor;

    /// <summary>Последняя позиция курсора БЕЗ гейтов — по ней `Ship`
    /// восстанавливает луч прицела для выстрела (шаг 3.3), даже когда мышь не
    /// двигалась и look-канал глух.</summary>
    public Vector2 LastMousePx;

    /// <summary>Последняя дельта мыши (пиксели) — только для диагностики/тестов
    /// транспорта ввода; в look-канал не входит (режим 2.0 меряет ПОЗИЦИЮ).</summary>
    public Vector2 LastMouseDeltaPx;

    /// <summary>Аддитивный look-канал (пиксели): его наполняет тестовый шов
    /// `Ship.TestAddLookDelta`, игровая мышь — нет. Съедается ровно один раз за шаг.</summary>
    public Vector2 AccumulatedLookPx;

    /// <summary>Маска последнего собранного шага — доступна вызывающему (в `Ship`
    /// её помнит `_lastFrameMask` для правила захвата).</summary>
    public uint LastMask;

    public PilotInput() { }

    /// <summary>Collector больше не активен: сбрасываем накопления, чтобы у
    /// следующего режима (или следующего пирата) не осталось хвоста.</summary>
    public void Release()
    {
        Active = false;
        InteractionMode = false;
        AccumulatedLookPx = Vector2.Zero;
        HasCursor = false;
    }

    /// <summary>Сброс накопленного look-канала (вход/выход из режима
    /// взаимодействия: старые пиксели, протасканные курсором по UI, не должны
    /// довернуть нос в момент переключения).</summary>
    public void ClearLookDelta() => AccumulatedLookPx = Vector2.Zero;

    /// <summary>Пиксельный шов тестов (бывший `Ship.TestAddLookDelta`).</summary>
    public void AddLookDeltaPx(Vector2 pixels) => AccumulatedLookPx += pixels;

    // ==================================================================
    // Мышь (п.4: накопление — здесь, значение отдаёт Collect)
    // ==================================================================

    /// <summary>Обработчик мышиного движения collector'а. Пишет курсор-прицел
    /// ПОСЛЕ гейта «есть пилот» (п.16: позиция и бит под одним флагом-гейтом),
    /// а `LastMousePx` — всегда: луч выстрела восстанавливается по последней
    /// позиции, даже когда look-канал глух.</summary>
    public void HandleMouse(InputEventMouseMotion m, Vector2 viewportSize)
    {
        LastMousePx = m.Position;
        LastMouseDeltaPx = m.Relative;
        if (!Active)
            return;
        // БЕЗ клампа по вьюпорту — как поля `_pilotCursorPx` до выноса (инвариант
        // блока A: байт-в-байт те же числа). Кламп нужен лишь скафандру (луч
        // пиксель-пер-клик за пределами экрана всё равно идёт по границе).
        CursorPx = m.Position;
        HasCursor = true;
    }

    /// <summary>Позиция курсора-прицела: валидные экранные пиксели, обрезанные по
    /// вьюпорту (за его пределами луч камеры всё равно тот же, что по границе).</summary>
    public static Vector2 ClampToViewport(Vector2 px, Vector2 viewportSize)
    {
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f)
            return px;
        return new Vector2(
            Mathf.Clamp(px.X, 0f, viewportSize.X),
            Mathf.Clamp(px.Y, 0f, viewportSize.Y));
    }

    /// <summary>Ставить курсор-прицел в центр экрана (посадка в кресло / вход в
    /// режим, чтобы доворот стартовал «на нуле» и без рывка).</summary>
    public void WarpCursorToCenter(Vector2 viewportSize)
    {
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f)
            return;
        CursorPx = viewportSize * 0.5f;
        HasCursor = true;
        // Warp безвреден и в headless (просто сдвиг OS-курсора в центр); в
        // безголовом режиме он ничего не делает, но `CursorPx` выше уже задаёт
        // корректную стартовую точку прицела. `Input.WarpMouse` — рабочий
        // биндинг Godot 4.7 (у Window метода WarpMousePosition нет).
        Input.WarpMouse(CursorPx);
    }

    // ==================================================================
    // Кадр (п.5: единая сборка для корабля и скафандра)
    // ==================================================================

    /// <summary>
    /// Собрать кадр ввода за один физический шаг: клавиши (маска + оси через
    /// <see cref="InputFrame.FromMask"/>) + look-канал (доворот НОСА на ПОЗИЦИЮ
    /// курсора через <see cref="PilotAim"/> плюс аддитивный пиксельный канал
    /// тестовых швов).
    ///
    /// Числа на выходе — байт-в-байт те, что давал код внутри `Ship` до выноса
    /// (инвариант блока A): тот же порядок множителей, та же константа
    /// чувствительности, тот же `InputFrame.Size = 16`.
    ///
    /// `extraMask` — биты, которые вызывающий решил сам (автопилот headless-
    /// стенда гонит `ThrustF`/`ThrustB`/`TorqueYaw*`): они добавляются в маску
    /// ПОСЛЕ гейта режима взаимодействия — ровно как в исходном коде.
    /// `lookSuppressed` глушит позиционный look-канал (автопилот стенда: его
    /// поворот живёт в битах `Torque*`, а курсора у автопилота нет). Аддитивный
    /// пиксельный канал при этом остаётся: на нём стоят швы Stage2Test/ProbeChaseAim.
    /// Возвращенный кадр «съедает» накопленный look-канал ровно один раз.
    /// </summary>
    public InputFrame Collect(uint tick, Vector2 viewportSize, float maxAngularSpeed,
        float dt, float lookSensitivityPxToRad, uint extraMask = 0, bool lookSuppressed = false)
    {
        uint mask = 0;
        foreach ((string action, uint bit) in Bindings)
            if (Input.IsActionPressed(action))
                mask |= bit;

        // Режим взаимодействия: объект перестаёт управляться по тангажу/рысканью/
        // крену (и мышью, и клавишами). Тяга (WASD/boost) остаётся: игрок
        // освобождает мышь для интерфейса, а не глушит двигатели.
        if (InteractionMode)
            mask &= ~InputMask.AngularBits;
        mask |= extraMask;

        Vector2 lookRad = lookSuppressed ? Vector2.Zero
            : LookRadiansPerStep(viewportSize, maxAngularSpeed, dt);
        // Аддитивный канал (пиксели) пересчитываем той же чувствительностью, что
        // и раньше: шов TestAddLookDelta мыслит пикселями, сеть — радианами.
        lookRad += AccumulatedLookPx * lookSensitivityPxToRad;

        InputFrame f = InputFrame.FromMask(tick, mask, yaw: lookRad.X, pitch: lookRad.Y);
        AccumulatedLookPx = Vector2.Zero;   // съедаем ровно один раз за шаг
        LastMask = mask;
        return f;
    }

    /// <summary>Позиционная команда доворота (рад за шаг) на курсор-прицел. Ноль,
    /// если: collector не активен, режим взаимодействия, мышь ни разу не
    /// «видели» (headless-детерминизм: без мыши позиция (0,0) = угол окна сама бы
    /// крутила объект), либо нет размера окна. Полный радиус — по ВЫСОТЕ окна
    /// (ТЗ: ¼ вертикального размера), слепая зона — константа <see cref="PilotAim"/>.</summary>
    public Vector2 LookRadiansPerStep(Vector2 viewportSize, float maxAngularSpeed, float dt)
    {
        if (!Active || InteractionMode || !HasCursor)
            return Vector2.Zero;
        if (viewportSize.X <= 0f || viewportSize.Y <= 0f)
            return Vector2.Zero;
        return PilotAim.LookRadiansPerStep(
            CursorPx,
            PilotAim.ScreenCenter(viewportSize),
            PilotAim.FullSpeedRadiusPx(viewportSize.Y),
            maxAngularSpeed,
            dt);
    }

    /// <summary>Количество собранных действий (для автотеста п.16/п.3 ТЗ: «бит
    /// пропал» меряется числом выставленных битов, а не состоянием InputMap).</summary>
    public static int BitCount(uint mask)
    {
        int n = 0;
        while (mask != 0) { mask &= mask - 1; n++; }
        return n;
    }
}
