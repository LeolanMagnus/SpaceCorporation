namespace SpaceCorporation;

/// <summary>
/// Фракция объекта мира (Этап H4 hud-plan.md §1: byte-поле `faction`).
/// Хранится в реплике байтом (<c>NetState.FactionValue</c> у корабля,
/// <c>TargetState.FactionValue</c> у мишени) — как <c>ShipTypeValue</c>:
/// пишет только авторитет при спавне, транспорт — спавн-снимок + OnChange.
/// Значения закреплены НАВСЕГДА (байт в сети): new-фракции добавлять только
/// в конец списка.
/// </summary>
public enum Faction : byte
{
    Neutral = 0,
    Player = 1,
    Hostile = 2,
    Ally = 3,
}
