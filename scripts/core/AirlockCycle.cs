namespace SpaceCorporation;

/// <summary>
/// Чистое правило цикла шлюза (фаза 2, F2.1; docs/habitable-deck-phase2-plan.md).
/// Переходы дверей — детерминированная функция состояния шлюза и намерения
/// игрока; ни узлов, ни физики. `Airlock` (thin carrier) применяет результат,
/// сеть — только транспорт намерений и состояний.
///
/// Инвариант (проверяется и правилом, и носителем): обе двери одновременно
/// открыты НЕДОСТИЖИМЫ — любое открытие второй двери возможно только когда
/// первая закрыта.
/// </summary>
public enum HatchAction
{
    /// <summary>Игрок снаружи у наружной двери: открыть наружную (вход в шлюз).</summary>
    OpenExternalFromOutside,
    /// <summary>Игрок внутри на джетпаке, наружная открыта: цикл внутрь —
    /// закрыть наружную и открыть внутреннюю (носитель по пути ставит ботинки).</summary>
    CycleIn,
    /// <summary>Игрок внутри в ботинках, внутренняя открыта: цикл наружу —
    /// закрыть внутреннюю и открыть наружную (носитель снимает ботинки).</summary>
    CycleOut,
    /// <summary>Только сеть (F2.1): откат CycleIn на клиенте, если ботинки не
    /// включились (нет опоры) — из состояния после принятого CycleIn (внутри,
    /// наружная закрыта, внутренняя открыта, игрок на джетпаке) снова открыть
    /// наружную. Без него это состояние — тупик: CycleOut требует ботинки.
    /// Оффлайн/хост этот переход не использует (локальный откат отменяет
    /// CycleIn поштучно).</summary>
    CancelToSpace,
}

public enum HatchReject
{
    None,
    /// <summary>Действие требует полностью герметичного шлюза (обе закрыты).</summary>
    NotSealed,
    /// <summary>CycleIn требует: игрок внутри, джетпак, наружная открыта, внутренняя закрыта.</summary>
    WrongStateIn,
    /// <summary>CycleOut требует: игрок внутри, ботинки, внутренняя открыта, наружная закрыта.</summary>
    WrongStateOut,
    /// <summary>Запрос наружу, а игрок снаружи (у двери нечего делать).</summary>
    OutsideNotAtDoor,
}

/// <summary>Вход правила: состояние шлюза + контекст игрока относительно него.</summary>
public struct HatchState
{
    public bool ExternalOpen;
    public bool InternalOpen;
    /// <summary>Игрок внутри коробки шлюза (габарит — у носителя).</summary>
    public bool Inside;
    /// <summary>Режим EVA игрока: Jetpack/Boots (Seat у шлюза невозможен).</summary>
    public bool OnBoots;
}

public struct HatchDecision
{
    public bool Allowed;
    public HatchReject Reason;
    /// <summary>Итоговое состояние дверей (валидно при Allowed).</summary>
    public bool ExternalOpen;
    public bool InternalOpen;
    /// <summary>Носитель обязан развить ботинки (CycleIn) / снять их (CycleOut).</summary>
    public bool BootsOn;
}

public static class AirlockCycle
{
    public static HatchDecision Evaluate(HatchState s, HatchAction action)
    {
        var no = new HatchDecision { Allowed = false };

        switch (action)
        {
            case HatchAction.OpenExternalFromOutside:
                // Наружную с улицы открываем только из герметичного шлюза —
                // иначе открываем вторую дверь при открытой первой (разгерметизация).
                if (s.Inside)
                    return new HatchDecision { Allowed = false, Reason = HatchReject.OutsideNotAtDoor };
                if (s.ExternalOpen || s.InternalOpen)
                    return new HatchDecision { Allowed = false, Reason = HatchReject.NotSealed };
                return new HatchDecision
                {
                    Allowed = true, Reason = HatchReject.None,
                    ExternalOpen = true, InternalOpen = false, BootsOn = s.OnBoots,
                };

            case HatchAction.CycleIn:
                //джетпак внутри при открытой наружной и закрытой внутренней.
                if (!s.Inside || s.OnBoots || !s.ExternalOpen || s.InternalOpen)
                    return new HatchDecision { Allowed = false, Reason = HatchReject.WrongStateIn };
                return new HatchDecision
                {
                    Allowed = true, Reason = HatchReject.None,
                    ExternalOpen = false, InternalOpen = true, BootsOn = true,
                };

            case HatchAction.CycleOut:
                //ботинки внутри при открытой внутренней и закрытой наружной.
                if (!s.Inside || !s.OnBoots || !s.InternalOpen || s.ExternalOpen)
                    return new HatchDecision { Allowed = false, Reason = HatchReject.WrongStateOut };
                return new HatchDecision
                {
                    Allowed = true, Reason = HatchReject.None,
                    ExternalOpen = true, InternalOpen = false, BootsOn = false,
                };

            case HatchAction.CancelToSpace:
                // Откат CycleIn (ботинки не включились — нет опоры): состояние
                // ровно после принятого CycleIn (наружная закрыта, внутренняя
                // открыта, игрок внутри всё ещё на джетпаке) — снова открыть
                // наружную и закрыть внутреннюю. Без этого перехода это
                // состояние — тупик: CycleOut требует ботинки, и F нечем
                // отыграть. Гейт дизъюнктивен с CycleOut по признаку ботинок.
                if (!s.Inside || s.OnBoots || s.ExternalOpen || !s.InternalOpen)
                    return new HatchDecision { Allowed = false, Reason = HatchReject.WrongStateIn };
                return new HatchDecision
                {
                    Allowed = true, Reason = HatchReject.None,
                    ExternalOpen = true, InternalOpen = false, BootsOn = false,
                };

            default:
                return no;
        }
    }
}
