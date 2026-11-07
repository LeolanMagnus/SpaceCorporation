using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial Ship: вырезано из Ship.cs при partial-декомпозиции.
public partial class Ship : RigidBody3D
{
    // frame-plan §6.3 (шаг 7): наблюдатель захваченного корабля. Точка
    // интерполяции смещается ВДОЛЬ v_frame на собственный адаптивный
    // _interpDelay: снимки A приходят на delay в прошлом, а родитель B у
    // своего пилота-клиента рисуется «живым» предсказанием — пузырь визуально
    // отстаёт на |v_frame|·delay (при 100 м/с и 0.1 с — 10 м). Сдвиг — НЕ
    // экстраполяция (поза по-прежнему между снимков), это сдвиг точки съёмки:
    // уносная составляющая позы A на 100% причинно равна позе B, сдвинуть её
    // вперёд на delay — значит поставить A туда, где его видит пилот B.
    // ВЕЛИЧИНА нарастает/снимается ПЛАВНО ( slew-лимит: |Δсдвиг| за кадр не
    // больше CapShiftSlewPerSec·dt), иначе вход в захват рисует скачок в 10 м —
    // ровно то, что ловит метрика (в) MaxHullDelta (порог 0.15 м при 60 к/с).
    // Вектор сдвига держится залатченным: при выходе из фазы он гаснет к нулю
    // по тому же лимиту, даже когда родительская реплика уже недоступна.
    // Реплика скоростей родителя — из NetState родителя (far-тир их не несёт и
    // оставляет прежние; захват в far — унос не включаем, R8).
    private Vector3 _capShiftCur;       // текущий вектор сдвига точки съёмки
    private double _capShiftLastNow = -1; // для dt между вызовами InterpolateHull
    private const float CapShiftSlewPerSec = 8.0f; // ≤ порога метрики (в) 0.15 м·60/с
    private bool _lastWireFarTier;      // тир последнего wire-снимка (R8: в far нет скоростей)

    // frame-plan §6.2 (шаг 6): то же решение для фазы ЗАХВАТА. Кольцо
    // предсказания с переигровкой (ReplayInputsAfter) не знает v_frame родителя
    // на переигрываемых тиках — полной истории родителя в предсказании нет,
    // поэтому предсказание пилота на пузыре расходилось бы с истиной авторитета
    // почти на 100% и давало постоянные hardRollbacks. Дешёвая проверенная
    // схема dock-фазы: на захвате кадры ВВОДА идут как обычно (тяга работает
    // по локальной скорости, сервер интегрирует их в своей причине), но
    // локальное предсказание и реконсиляция ВЫКЛЮЧЕНЫ, позу ведёт авторитет,
    // картинка — через интерполяцию батча. Выход из фазы = разрыв: одно
    // обнуление предсказания, старт от актуальной реплики.
    private bool _clientCapturePhaseActive;

    // frame-plan, шаг 3: захват объектом превосходящей массы (0 = нет). Истину
    // пишет только авторитет в TickCaptureRule (правило — чистая функция
    // CaptureRules, проверка раз в CaptureTuning.CheckEveryN физшагов).
    // Шаг 6: переход РАЗЫСЫВАЕТСЯ репликой (Net.CaptureParentNetId/CaptureTick:
    // спавн-снимок + OnChange-дельта), но это поле — источник ИСТИНЫ авторитета;
    // у клиентов-наблюдателей факт захвата живёт в Net, а физика/предсказание
    // гейтуются по нему (§6.2 frame-plan).
    private int _captureParentNetId;

    /// <summary>Проба кадра родителя (v_frame), внедрённая на ПОСЛЕДНЕМ шаге
    /// этого тела. Снимается с ПРЕДЫДУЩЕЙ пробы — перенос при разгоне/торможении
    /// родителя (см. `AdvanceFlight`); при смене родителя обнуляется, чтобы
    /// «переходный» кадр не дал пинка от старого родителя.</summary>
    private Vector3 _captureFrameV;

    /// <summary>NetId родителя захвата (0 = не захвачен). Публично — HUD
    /// (шаг 8), игрок (шаг 9) и автотесты (шаг 4).</summary>
    public int CaptureParentNetId => _captureParentNetId;

    /// <summary>Локальная скорость в кадре родителя: |v − v_frame| — синяя
    /// строка HUD (frame-plan §1). Без захвата равна обычной скорости.</summary>
    public Vector3 LocalVelocity => LinearVelocity - CaptureFrameVelocityAt(GlobalPosition);

    /// <summary>frame-plan шаг 7 (HUD, R7): v_rel из СЕТЕВЫХ реплик обоих
    /// кораблей — одинаково на сервере и клиенте (источник фазы —
    /// `Net.CaptureParentNetId`, шаг 6). На этой стадии `FrameRotational=false`
    /// (R3), поэтому v_frame = скорость родителя, поза не нужна. Возвращает
    /// null, когда захвата нет (HUD не рисует строку вовсе).</summary>
    public float? HudLocalSpeed()
    {
        if (Net == null || Net.CaptureParentNetId == 0)
            return null;
        var nm = NetworkManager.Instance;
        if (nm == null || !nm.Ships.TryGetValue(Net.CaptureParentNetId, out var p)
            || p == null || !IsInstanceValid(p) || p.Net == null)
            return null;
        return (Net.NetLinearVelocity - p.Net.NetLinearVelocity).Length();
    }

    /// <summary>frame-plan §3: скорость кадра родителя в мировой точке pos
    /// (0 без захвата; ω-член выключен — CaptureTuning.FrameRotational, R3).
    /// Родитель исчез из реестра — кадр нулевой (правило снимет захват на
    /// ближайшей проверке, до него объект летит по инерции).</summary>
    private Vector3 CaptureFrameVelocityAt(Vector3 pos)
    {
        int pid = CaptureParentEffective;
        if (pid == 0)
            return Vector3.Zero;
        var nm = NetworkManager.Instance;
        if (nm == null || !nm.Ships.TryGetValue(pid, out var p) || p == null || p == this)
            return Vector3.Zero;
        return CaptureRules.FrameVelocity(p.GlobalPosition, p.LinearVelocity,
                                          p.AngularVelocity, pos, CaptureTuning.FrameRotational);
    }

    /// <summary>Родитель захвата для интеграции/интерполяции с ЛЮБОЙ стороны:
    /// у авторитета истинa — локальное поле (его пишет TickCaptureRule), у
    /// клиента — реплика `Net.CaptureParentNetId` (её пишет только сервер через
    /// SetCapture; клиент правило не считает — R1 frame-plan).</summary>
    private int CaptureParentEffective =>
        _captureParentNetId != 0 ? _captureParentNetId : (Net?.CaptureParentNetId ?? 0);

    /// <summary>Радиус пузыря захвата от габарита корпуса: половина диагонали
    /// + CaptureTuning.Margin (frame-plan §4).</summary>
    public float CaptureRadius =>
        _shipTypeSpec.HullSize.Length() * 0.5f + CaptureTuning.Margin;

    /// <summary>Родитель «на маршруте» (крейсерский режим): в последнем шаге
    /// НЕ крутился (нет торков) и НЕ форсажил (нет Boost). Линейная тяга
    /// разрешена. Правило зовёт это у кандидата напрямую (только авторитет).
    /// PilotAssist в предикат НЕ входит (cruise-capture-plan §2.1.3).</summary>
    public bool IsCruisingParent() =>
        (_lastFrameMask & (InputMask.AngularBits | InputMask.Boost)) == 0u;

    /// <summary>
    /// frame-plan шаг 3: серверная проверка правила захвата раз в
    /// CaptureTuning.CheckEveryN физшагов (по образцу TickDockPhases — только
    /// авторитет, ПОСЛЕ шага модели: геометрия шага финальная). Один родитель —
    /// наибольший по массе из подходящих (при равенстве — меньший NetId, R5);
    /// захваченный не может быть родителем (parent.Captured у уже захваченных
    /// кандидатов); взаимоисключение с докингом (R6): у самого корабля в
    /// dock-фазе пузырь гаснет и правило не применяется. Гистерезис — внутри
    /// CaptureRules.Evaluate (по текущему состоянию _captureParentNetId).
    /// На этой стадии переходы НЕ реплицируются (шаг 6), поэтому правило
    /// работает только там, где авторитет и есть мир: offline/hot-сервер.
    /// </summary>
    private void TickCaptureRule()
    {
        if (Net == null || Net.ShipNetId == 0)
            return;
        // Авторитетная сторона процесса: сервер И offline (одиночная игра, как в
        // BUG-008/009 у креплением: гейт чистым IsServer в offline молчал).
        if (NetworkManager.Instance is not { IsAuthoritySide: true } nm)
            return;
        if ((uint)(_tick % (uint)CaptureTuning.CheckEveryN) != 0u)
            return;
        // R6: докинг стадии 5 имеет приоритет — на dock-фазе пузырь гаснет.
        if (Docking.IsCapturePhase((DockState)Net.DockStateValue))
        {
            if (_captureParentNetId != 0)
                SetCapture(0);
            return;
        }

        var me = new CaptureBody
        {
            Mass = Flight.Mass,
            Position = GlobalPosition,
            Velocity = LinearVelocity,
        };
        bool wasCaptured = _captureParentNetId != 0;

        CaptureBody best = default;
        int bestId = 0;
        CaptureReject bestReject = CaptureReject.OutsideBubble;
        foreach (var (id, other) in nm.Ships)
        {
            if (other == this || other.Net == null || other.Net.ShipNetId == 0)
                continue;
            var cand = new CaptureBody
            {
                Mass = other.Flight.Mass,
                Position = other.GlobalPosition,
                Velocity = other.LinearVelocity,
                Radius = other.CaptureRadius,
            };
            var parentState = new CaptureParentState
            {
                InDockPhase = Docking.IsCapturePhase((DockState)other.Net.DockStateValue),
                // глубина 1: сам кандидат — родитель или уже захвачен — в
                // родимые не годится (R5); второй проход (меняем местами) его
                // отсеет, но только если он сейчас активный родитель — иначе
                // «был когда-то» не считается.
                Captured = other._captureParentNetId != 0,
                // крейсерский гейт (cruise-capture-plan): маска последнего шага
                // родителя; учитывается только при RequireCruisingParent=true.
                Cruising = other.IsCruisingParent(),
            };
            var d = CaptureRules.Evaluate(me, cand, parentState,
                                         wasCaptured && _captureParentNetId == id);
            if (!d.Captured)
            {
                if (wasCaptured && _captureParentNetId == id)
                    bestReject = d.Reason; // чтобы лог выхода был честным
                continue;
            }
            // выбор наибольшего по массе, при равенстве — меньший NetId
            if (bestId == 0 || cand.Mass > best.Mass ||
                (Mathf.IsEqualApprox(cand.Mass, best.Mass) && id < bestId))
            {
                bestId = id;
                best = cand;
            }
        }
        if (bestId != _captureParentNetId)
        {
            if (bestId != 0)
                DebugInfoBus.Report($"capture: ship#{Net.ShipNetId} ← #{bestId} " +
                                    $"(|v_rel| = {(me.Velocity - best.Velocity).Length():F1} м/с)");
            else
                DebugInfoBus.Report($"capture: ship#{Net.ShipNetId} release " +
                                    $"(от #{_captureParentNetId}, {bestReject})");
            SetCapture(bestId);
        }
    }

    /// <summary>Переход фазы захвата (только авторитет — вызывается из
    /// TickCaptureRule). Шаг 6: поле пишется И В `NetState`
    /// (CaptureParentNetId + CaptureTick): реплика уходит OnChange-дельтой по
    /// образцу dock-фазы (SceneReplicationConfig properties/13-14), поздно
    /// зашедший пир получает её спавн-снимком. Это единственный рассылочный
    /// путь перехода — клиент правило не считает (R1). Шаг 5 (R4): исключение
    /// коллизий пары.</summary>
    private void SetCapture(int parentId)
    {
        int old = _captureParentNetId;
        if (old == parentId)
            return;
        var nm = NetworkManager.Instance;
        // шаг 5 (R4): на время захвата corps-коллижн пары отключается, иначе
        // корпус родителя выталкивает пассажира (дрожь внутри пузыря).
        if (nm != null)
        {
            if (nm.Ships.TryGetValue(old, out var oldParent) && oldParent != null && IsInstanceValid(oldParent))
                oldParent.RemoveCollisionExceptionWith(this);
            if (nm.Ships.TryGetValue(parentId, out var newParent) && newParent != null)
                newParent.AddCollisionExceptionWith(this);
        }
        _captureParentNetId = parentId;
        // Шаг 6: публикация перехода в NetState — OnChange-дельта уходит пирам
        // в том же порядке канала, что смена флага ассиста и dock-фазы (надёжный
        // канал, обязан прийти раньше ненадёжных кадров ввода). CaptureTick =
        // физкадр авторитета на момент перехода: клиент-пилот по смене тика
        // видит РАЗРЫВ фазы и обнуляет предсказание (снимок позиции и так
        // придёт с ближайшей записью батча).
        if (Net != null)
        {
            Net.CaptureParentNetId = parentId;
            Net.CaptureTick = System.Math.Max(0, Net.SimTick);
        }
        // Живой тест CaptureLiveTest: первичная проба кадра при ВХОДЕ — ровно
        // v_frame родителя сейчас. Без неё первый захваченный шаг снял бы
        // vFramePrev = 0 и внедрил бы v_frame заново — пинек двойной скоростью
        // (корпус вылетел бы из пузыря за доли секунды). При выходе — ноль.
        _captureFrameV = parentId != 0 ? CaptureFrameVelocityAt(GlobalPosition) : Vector3.Zero;
    }

    /// <summary>§6.3 (шаг 7): вектор уноса картинки захваченного корабля —
    /// `v_frame × _interpDelay`, slew-лимитированный. Считается по репликам
    /// родителя (`Net.CaptureParentNetId` + NetState родителя: его позиция и
    /// скорости — те же реплики, что рисует этот наблюдатель). Дальний тир
    /// (R8): wire-запись far скоростей не несёт → унос не включаем (только
    /// поза), уже начатый плавно гасится к нулю тем же slew-лимитом.</summary>
    private Vector3 CaptureCarryShift(double now)
    {
        // dt между вызовами (первый вызов — без шага).
        double dt = _capShiftLastNow < 0 ? 0 : System.Math.Max(0, now - _capShiftLastNow);
        _capShiftLastNow = now;

        Vector3 target = Vector3.Zero;
        int pid = Net?.CaptureParentNetId ?? 0;
        if (pid != 0 && !_lastWireFarTier)
        {
            var nm = NetworkManager.Instance;
            if (nm != null && nm.Ships.TryGetValue(pid, out var p) && p != null && p != this && IsInstanceValid(p))
            {
                // v_frame в той же формуле, что у авторитета (CaptureRules):
                // линейная часть (ротационная выключена, R3 — тот же флаг).
                Vector3 vFrame = CaptureRules.FrameVelocity(
                    p.Net.NetPosition, p.Net.NetLinearVelocity, p.Net.NetAngularVelocity,
                    Net.NetPosition, CaptureTuning.FrameRotational);
                target = vFrame * (float)_interpDelay;
            }
        }

        // slew: |цель − текущий| ограничиваем скоростью CapShiftSlewPerSec.
        // При dt==0 (первый вызов за кадр/инициализация) шаг НЕ добавляем вовсе:
        // иначе нулевой maxStep снимает кламп и вектор прыгает в цель за один
        // кадр — ровно тот скачок в |v_frame·delay|, который ловит метрика (в)
        // MaxHullDelta. Разгон начинается со второго кадра.
        float maxStep = CapShiftSlewPerSec * (float)dt;
        if (maxStep <= 0)
            return _capShiftCur;
        Vector3 diff = target - _capShiftCur;
        if (diff.LengthSquared() > maxStep * maxStep)
            diff = diff.Normalized() * maxStep;
        _capShiftCur += diff;
        return _capShiftCur;
    }

}
