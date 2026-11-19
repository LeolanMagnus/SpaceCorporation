using System.Collections.Generic;
using Godot;

namespace SpaceCorporation;

// partial Player: контекстные исключения коллизий (docs/habitable-deck-plan.md,
// Шаг 0). Общее правило: маска игрока включает слой Ship (сталкиваемся с
// обшивкой), а «мягчить» коллизию с кораблём, ВНУТРИ которого мы стоим/сидим,
// нужно исключениями — они живут ровно столько, сколько контекст:
//   · Seat (Reparent под hull)  — всё дерево корпуса своего корабля;
//   · Boots/пристыкован на палубе корабля — само дерево корабля: и палубы
//     (иначе липкость-снап при вращении hull проваливает капсулу в пол,
//     стендовый кейс Clip3), и corpo-обшивка (изнутри hull она не режет).
//     Наружные фаски (выпуклые грани hull) остаются блокирующими: стоять на
//     них можно (слой Deck), а пройти сквозь стену внутрь — нельзя (Clip4).
//   · джетпак в открытом косме, чужой корпус снаружи — исключений нет,
//     капсула упирается в обшивку (Clip1).
// Хелперы вызываются из единственных мест смены контекста: AttachTo/Detach
// (Player.Attach.cs) и ActivateBoots/DeactivateBoots (Player.Eva.cs).
public partial class Player : CharacterBody3D
{
    private uint _baseCollisionMask;
    private readonly HashSet<Rid> _clipExceptions = new();
    // Rid тела, которому назначены исключения: при пересоздании тела (снятие
    // с мира/возврат стенда) старые Rid умирают вместе с телом — звать
    // BodyRemoveCollisionException новым Rid по старым фигурам бессмысленно.
    private Rid _clipOwnerRid = new();

    /// <summary>Узлы-источники фигур, с которыми сейчас снята коллизия
    /// (обновляются целиком при смене контекста).</summary>
    private readonly HashSet<Node3D> _clipNodes = new();

    /// <summary>Все фигуры под узлом (для BodyAddCollisionException: API
    /// принимает фигуру, а не тело — у RigidBody3D их может быть несколько).</summary>
    private static void CollectShapes(Node3D root, ICollection<Rid> into, bool selfOnly)
    {
        if (root is PhysicsBody3D body)
        {
            Rid rid = body.GetRid();
            if (rid.IsValid)
                into.Add(rid);
        }
        if (selfOnly) return;
        foreach (Node c in root.GetChildren())
            if (c is Node3D n3)
                CollectShapes(n3, into, false);
    }

    /// <summary>Сменить контекст столкновений с кораблём:null/пусто — снять все
    /// исключения (свободный режим: обшивка блокирует), иначе — исключить
    /// коллизии со всеми фигурами перечисленных узлов-носителей.</summary>
    private void SetClipContext(IEnumerable<Node3D>? carriers)
    {
        var want = new HashSet<Node3D>();
        if (carriers != null)
            foreach (var n in carriers)
                if (n != null && GodotObject.IsInstanceValid(n))
                    want.Add(n);

        // Шаг ботинок дёргает пересборку каждый физический шаг — если носитель
        // не менялся и тело то же, трогать физсервер незачем.
        if (want.SetEquals(_clipNodes) && _clipOwnerRid == GetRid())
            return;

        // Пересобираем исключения целиком: контекст короткий и редкий,
        // инкрементальная диффа здесь дороже, чем риск залипших Rid.
        Rid me = GetRid();
        if (_clipOwnerRid == me)
        {
            foreach (Rid old in _clipExceptions)
                PhysicsServer3D.BodyRemoveCollisionException(me, old);
        }
        // Иначе тело пересоздано — старые Rid мертвы, снимать не с чего.
        _clipOwnerRid = me;
        _clipExceptions.Clear();
        _clipNodes.Clear();

        foreach (Node3D carrier in want)
        {
            var shapes = new HashSet<Rid>();
            CollectShapes(carrier, shapes, false);
            foreach (Rid shape in shapes)
            {
                PhysicsServer3D.BodyAddCollisionException(GetRid(), shape);
                _clipExceptions.Add(shape);
            }
            _clipNodes.Add(carrier);
        }
    }

    /// <summary>Корабль, НА ПАЛУБЕ которого я стою (Boots/пристыкован): несущее
    /// дерево ищём вверх по предкам тела опоры (`Decks/Deck*` висят под Ship).</summary>
    private Ship? BootsCarrierShip()
    {
        if (_bootsBody == null || !GodotObject.IsInstanceValid(_bootsBody))
            return null;
        for (Node n = _bootsBody.GetParent(); n != null; n = n.GetParent())
            if (n is Ship ship)
                return ship;
        return null;
    }

    /// <summary>Пересобрать контекст под текущий режим EVA (вызывается после
    /// смены режима/опоры: Detach, DeactivateBoots, посадка опоры в StepBoots).</summary>
    private void RefreshClipContext()
    {
        switch (_mode)
        {
            case EvaMode.Seat:
                // Кресло:Repater под hull — исключаем весь свой корабль.
                if (AttachedTo != null && GodotObject.IsInstanceValid(AttachedTo))
                    SetClipContext(new Node3D?[] { FindShipAncestor(AttachedTo) }!);
                else
                    SetClipContext(null);
                break;
            case EvaMode.Boots:
                // На палубе собственного корабля — исключаем его дерево;
                // на чистой палубе (станция/стенд) — ничего: чужой hull блокирует.
                Ship? carrier = BootsCarrierShip();
                SetClipContext(carrier != null
                    ? new[] { (Node3D)(object)carrier } : null);
                break;
            default:
                SetClipContext(null);
                break;
        }
    }

    // Тестовые швы (образец EvaTest*): стенд HabDeckTest сверяет клипы напрямую.
    public int ClipTestCount() => _clipExceptions.Count;
    public uint ClipTestMask() => CollisionMask;
    public uint ClipTestBaseMask() => _baseCollisionMask;

    /// <summary>Ближайший предок-корабль (или сам узел).</summary>
    private static Ship? FindShipAncestor(Node n)
    {
        for (Node x = n; x != null; x = x.GetParent())
            if (x is Ship ship)
                return ship;
        return null;
    }
}
