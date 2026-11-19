using Godot;

namespace SpaceCorporation;

/// <summary>
/// Типовой шлюз: герметичная коробка 1×1 м, высота 2 м, двери на противоположных
/// стенах. Наружная дверь (External) смотрит в космос (локальный +Z), внутренняя
/// (Internal) — в обитаемый объём (локальный −Z). Все стены и двери — на слое
/// `Deck` (256): в них упирается капсула игрока в ботинках, поэтому «выйти из
/// кабины можно только через шлюз» работает как физический гейт, а не как
/// скриптовый запрет.
///
/// Двери — `StaticBody3D` с формой-заглушкой проёма: закрыта ⇒ коллизия
/// включена, открыта ⇒ коллизия выключена и полотно сдвигается в сторону
/// (только визуал; в живом коде — анимация). Состояние дверей — единственная
/// «логика» шлюза на этом шаге; циклом шлюзования (внешняя закрыта → разворот
/// ногами к палубе → внутренняя открыта) управляет игрок (Player.HatchCycle).
/// </summary>
public partial class Airlock : Node3D
{
    /// <summary>Группа шлюзов сцены (их ищет F-подсказка/ХатчTryInteract).</summary>
    public const string GroupName = "airlock";

    /// <summary>Полотно наружной двери (в космос, +Z).</summary>
    [Export] public NodePath ExternalDoorPath { get; set; } = "DoorExternal";
    /// <summary>Полотно внутренней двери (в кабину, −Z).</summary>
    [Export] public NodePath InternalDoorPath { get; set; } = "DoorInternal";

    public StaticBody3D ExternalDoor { get; private set; }
    public StaticBody3D InternalDoor { get; private set; }

    public bool ExternalOpen { get; private set; }
    public bool InternalOpen { get; private set; }

    /// <summary>Направление сдвига полотна при открытии (в локальных осях двери,
    /// нормировано). Только визуал: дыра в стене становится видна.</summary>
    public override void _Ready()
    {
        AddToGroup(GroupName);
        ExternalDoor = GetNodeOrNull<StaticBody3D>(ExternalDoorPath);
        InternalDoor = GetNodeOrNull<StaticBody3D>(InternalDoorPath);
        // Стартовое состояние: обе двери закрыты — шлюз герметичен.
        ExternalOpen = false;
        InternalOpen = false;
        SetDoor(ExternalDoor, false);
        SetDoor(InternalDoor, false);
    }

    public void SetExternalOpen(bool open)
    {
        ExternalOpen = open;
        SetDoor(ExternalDoor, open);
    }

    public void SetInternalOpen(bool open)
    {
        InternalOpen = open;
        SetDoor(InternalDoor, open);
    }

    /// <summary>Полностью герметичен ли шлюз (обе двери закрыты) — условие, при
    /// котором разрешён цикл шлюзования.</summary>
    public bool IsSealed => !ExternalOpen && !InternalOpen;

    private static void SetDoor(StaticBody3D door, bool open)
    {
        if (door == null) return;
        var shape = door.GetNodeOrNull<CollisionShape3D>("Shape");
        if (shape != null) shape.Disabled = open;
        if (door is Door d) d.SetSlideOpen(open);
    }
}
