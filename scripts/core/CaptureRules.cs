using Godot;

namespace SpaceCorporation;

/// <summary>
/// План промежуточной стадии (§4, frame-plan.md): порог-константы правила захвата.
/// Все пороги — здесь, чтобы тесты и балансировка смотрели в одно место.
/// </summary>
public static class CaptureTuning
{
    /// <summary>Вход: масса родителя ≥ масса объекта × RatioMin.</summary>
    public const float RatioMin = 3f;
    /// <summary>Выход: масса родителя &lt; масса объекта × RatioRelease (гистерезис по массе).</summary>
    public const float RatioRelease = 2f;
    /// <summary>Вход: |v_rel| при пересечении пузыря не больше этого (иначе «пролетел мимо»).</summary>
    public const float VAttachMax = 25f;
    /// <summary>Выход по дистанции: радиус × Hysteresis (гистерезис против дрожащего порога).</summary>
    public const float Hysteresis = 1.4f;
    /// <summary>Проверка правила раз в N физ. шагов (R1: момент перехода и так размыт).</summary>
    public const int CheckEveryN = 4;
    /// <summary>Масса игрока для критерия превосходящей массы (§5).</summary>
    public const float PlayerMass = 100f;
    /// <summary>Запас пузыря от габарита родителя: радиус = половина диагонали
    /// корпуса + Margin (frame-plan §4: margin 25 м).</summary>
    public const float Margin = 25f;
    /// <summary>Промежуточная стадия: ω-член скорости кадра выключен (R3 —
    /// эффект пращи на вращающемся родителе; включение — отдельная стадия).</summary>
    public const bool FrameRotational = false;
    /// <summary>Крейсерский гейт (§ cruise-capture-plan): требовать, чтобы
    /// родитель ШЁЛ НА МАРШРУТЕ (не крутился и не форсажил), чтобы подобрать
    /// объект. По умолчанию ВЫКЛ — включает фичу, не ломая существующие тесты.
    /// НЕ const, а static: юнит-тест включает/выключает гейт в обе стороны
    /// (§5 cruise-capture-plan), иначе ON-ветку проверить нельзя. Гейт работает
    /// только на ветке ВХОДА (см. CaptureRules.Evaluate).</summary>
    public static bool RequireCruisingParent = false;
}

/// <summary>Минимальный вход правила: значения, не узлы (§4 — чистая функция).</summary>
public struct CaptureBody
{
    public float Mass;
    public Vector3 Position;
    public Vector3 Velocity;
    /// <summary>Радиус пузыря родителя (от габарита корпуса). У объекта не используется.</summary>
    public float Radius;
}

/// <summary>Состояние родителя, гасящее пузырь (вход — флаги, не кодировка числами).</summary>
public struct CaptureParentState
{
    /// <summary>Родитель в dock-фазе (Capturing/Latched/Docked) — пружина стадии 5
    /// уже ведёт пару, пузырь гаснет (R6).</summary>
    public bool InDockPhase;
    /// <summary>Родитель сам захвачен — глубина цепочки 1 (R5).</summary>
    public bool Captured;
    /// <summary>Родитель идёт НА МАРШРУТЕ (крейсерский режим): не крутится и не
    /// форсажит. Считает авторитет из маски последнего кадра шага родителя
    /// (Ship.IsCruisingParent). Учитывается только при
    /// CaptureTuning.RequireCruisingParent=true и только на ветке ВХОДА.</summary>
    public bool Cruising;
}

public readonly struct CaptureDecision
{
    /// <summary>Итог: объект захвачен этим родителем.</summary>
    public readonly bool Captured;
    /// <summary>Относительная скорость объекта в кадре родителя (всегда считается).</summary>
    public readonly Vector3 RelativeVelocity;
    /// <summary>Причина отказа/выхода — для отладки (F9) и тестов.</summary>
    public readonly CaptureReject Reason;

    public CaptureDecision(bool captured, Vector3 vRel, CaptureReject reason)
    { Captured = captured; RelativeVelocity = vRel; Reason = reason; }
}

public enum CaptureReject
{
    None = 0,
    /// <summary>Родитель пристыкован (dock-фаза) — пузырь гаснет (R6).</summary>
    ParentInDockPhase,
    /// <summary>Родитель сам захвачен — глубина цепочки 1 (R5).</summary>
    ParentCaptured,
    /// <summary>Масса родителя не превосходит достаточно (вход: &lt; RatioMin).</summary>
    MassRatioLow,
    /// <summary>Объект вне пузыря (вход: &gt; Radius).</summary>
    OutsideBubble,
    /// <summary>Влетел слишком быстро (вход: |v_rel| &gt; VAttachMax).</summary>
    TooFast,
    /// <summary>Выход: вышел за Radius × Hysteresis.</summary>
    LeftBubble,
    /// <summary>Родитель вышел из-под критерия массы (&lt; RatioRelease).</summary>
    ParentMassDrop,
    /// <summary>Родитель не в крейсерском режиме (вход, гейт крейсера):
    /// крутится или форсажит — не буксир на маршруте (cruise-capture-plan).</summary>
    ParentNotCruising,
}

/// <summary>
/// Правило захвата — чистая функция состояний (frame-plan §4). Детерминирована:
/// вход — только числа, никаких узлов/случайности, поэтому сервер считает её раз
/// в CheckEveryN шагов и рассылает переходы, а тесты гоняют её без игрового мира.
///
/// Семантика: A (объект) захватывается B (родителем), если B превосходит A по
/// массе, A внутри пузыря B, и относительная скорость не слишком велика.
/// Уже захваченный A остаётся захваченным, пока не выйдет за радиус×Hysteresis
/// или масса B не провалится ниже RatioRelease (гистерезисы против дребезга).
/// </summary>
public static class CaptureRules
{
    public static CaptureDecision Evaluate(CaptureBody a, CaptureBody b,
                                           CaptureParentState parent, bool currentlyCaptured)
    {
        Vector3 vRel = a.Velocity - b.Velocity;

        // Гасфазы родителя — в обе стороны перехода (и вход, и выход), иначе
        // захваченный объект «висит» на родителе, который пристыковался.
        if (parent.InDockPhase)
            return new CaptureDecision(false, vRel, CaptureReject.ParentInDockPhase);
        if (parent.Captured)
            return new CaptureDecision(false, vRel, CaptureReject.ParentCaptured);

        float massRatio = a.Mass > 0f ? b.Mass / a.Mass : float.MaxValue;
        float dist = a.Position.DistanceTo(b.Position);

        if (currentlyCaptured)
        {
            if (dist > b.Radius * CaptureTuning.Hysteresis)
                return new CaptureDecision(false, vRel, CaptureReject.LeftBubble);
            if (massRatio < CaptureTuning.RatioRelease)
                return new CaptureDecision(false, vRel, CaptureReject.ParentMassDrop);
            return new CaptureDecision(true, vRel, CaptureReject.None);
        }

        // --- вход (ветка !currentlyCaptured): гейт крейсерского режима. ---
        // Стоит ПОСЛЕ выхода активной ветки, поэтому потеря крейсера родителем
        // НЕ рвёт активный зацеп (решение §2.1.2 cruise-capture-plan): активный
        // выход остаётся по LeftBubble/ParentMassDrop. По умолчанию гейт ВЫКЛ
        // (RequireCruisingParent=false), тесты 24/24 не задевает.
        if (CaptureTuning.RequireCruisingParent && !parent.Cruising)
            return new CaptureDecision(false, vRel, CaptureReject.ParentNotCruising);

        if (massRatio < CaptureTuning.RatioMin)
            return new CaptureDecision(false, vRel, CaptureReject.MassRatioLow);
        if (dist > b.Radius)
            return new CaptureDecision(false, vRel, CaptureReject.OutsideBubble);
        if (vRel.Length() > CaptureTuning.VAttachMax)
            return new CaptureDecision(false, vRel, CaptureReject.TooFast);
        return new CaptureDecision(true, vRel, CaptureReject.None);
    }

    /// <summary>Скорость переноса в точке pos для родителя с v/ω
    /// (на промежуточной стадии ω-член выключен, R3 — rotational=false).</summary>
    public static Vector3 FrameVelocity(Vector3 parentPos, Vector3 parentVel, Vector3 parentOmega,
                                        Vector3 pos, bool rotational = false)
        => rotational
            ? parentVel + parentOmega.Cross(pos - parentPos)
            : parentVel;
}
