using System;
using Godot;

namespace SpaceCorporation;

/// <summary>
/// F2.4 (план «обитаемые палубы», фаза 2): HUD-контекст местоположения.
/// Строка источника «DeckCtx» в DebugInfoBus (тот же образец, что «Suit»,
/// п.11/п.32): CABIN — внутри габарита палубы своего/пристыкованного
/// корабля, HULL — снаружи на обшивке в ботинках, STATION — на палубе
/// станции, плюс пометка «внутри захваченного корабля», если палубу
/// засосало в пузырь другого корабля (Net.CaptureParentNetId владельца).
///
/// Чисто косметический слой: никаких игровых решений от него не зависит.
/// Источник — публичный метод DeckContextText() (как SuitInfoText):
/// стендовый тест сверяет его строки напрямую (план F2.4: «DebugInfoBus-тест
/// образца п.11»).
///
/// Геометрия габаритов:
///  • курьер (палуба сценой) — вписываем габарит в корпус по CollisionShape3D
///    корпуса (BoxShape3D_hull 2×1×4): cabin = |x|≤0.9, |y−0.7|≤0.6, |z|≤1.9
///    в ЛОКАЛЬНЫХ координатах Ship (мировые переводим через XformInv);
///    допуск +0.5 м покрывает шлюз и точку спавна у стены (у стены |z|=1.9,
///    центр бокса шлюза z=2.15 — тоже CABIN: шлюз — часть кабины);
///  • код-каюта каталожных типов (F2.3) — генератор кладёт её габарит в
///    meta `GenCabinCenter`/`GenCabinHalf` палубы `Decks` (только живые
///    каюты; в осиротевшем DeckStorage meta не читается — габарита нет).
/// </summary>
public partial class Player
{
    public const string DeckContextSourceKey = "DeckCtx";

    private Func<string>? _deckCtxSource;

    private string DeckContextTextSafe() =>
        GodotObject.IsInstanceValid(this) ? DeckContextText() : string.Empty;

    /// <summary>Строка HUD-контекста. Формат стабильный, тест сверяет
    /// подстроки: `loc=` + одна из CABIN/HULL/STATION/NONE (в кресле —
    /// CABIN), суффикс ` captured` у CABIN, если носитель захвачен.</summary>
    public string DeckContextText()
    {
        // Снаружи и не в ботинках — контекста нет (свободный EVA в космосе).
        if (_mode != EvaMode.Boots && _bootsBody == null)
            return "deck: loc=NONE";

        // Опора ботинок / носитель в кресле.
        Node3D? support = _mode == EvaMode.Seat
            ? (AttachedTo != null && GodotObject.IsInstanceValid(AttachedTo)
                ? FindShipAncestor(AttachedTo) : null)
            : _bootsBody;
        if (support == null || !GodotObject.IsInstanceValid(support))
            return "deck: loc=NONE";

        // Станция: палуба станции — её тела на слое Station (BootsMask),
        // вверх по дереву ищем StaticBody3D со станционным слоем.
        if (support is StaticBody3D sb
            && (sb.CollisionLayer & CollisionLayers.Station) != 0u)
            return "deck: loc=STATION";

        Ship? ship = BootsCarrierShip();
        if (ship == null)
            ship = FindShipAncestor(support);
        if (ship == null)
            return "deck: loc=NONE";

        Vector3 local = ship.ToLocal(GlobalPosition);
        bool cabin = IsPointInCabin(ship, local);
        if (!cabin)
            return _mode == EvaMode.Boots ? "deck: loc=HULL" : "deck: loc=NONE";

        // CABIN: захваченный носитель — пометка. effective-родитель как в
        // Ship.CaptureParentEffective: у авторитета истинное поле, у клиента
        // реплика Net.CaptureParentNetId (тот же источник, что HUD шага 8).
        int parentId = ship.CaptureParentNetId != 0
            ? ship.CaptureParentNetId : (ship.Net?.CaptureParentNetId ?? 0);
        string cap = parentId != 0 ? " captured" : "";
        return $"deck: loc=CABIN{cap}";
    }

    /// <summary>Точка в ЛОКАЛЬНЫХ координатах корабля внутри габарита его
    /// жилой палубы? Габарит код-каюты — из meta генератора; габарит
    /// сцена-палубы — объединённый AABB палубных тел слоя Deck (плиты +
    /// стены кабины; считается один раз и кэшируется в meta).</summary>
    private static bool IsPointInCabin(Ship ship, Vector3 local)
    {
        var decks = ship.GetNodeOrNull<Node3D>("Decks");
        if (decks == null)
            return false;

        if (decks.HasMeta("GenCabinCenter"))
        {
            Vector3 c = (Vector3)decks.GetMeta("GenCabinCenter");
            Vector3 h = (Vector3)decks.GetMeta("GenCabinHalf");
            return Mathf.Abs(local.X - c.X) <= h.X
                && Mathf.Abs(local.Y - c.Y) <= h.Y
                && Mathf.Abs(local.Z - c.Z) <= h.Z;
        }

        if (!decks.HasMeta("DeckAABBMin"))
        {
            // Палубные тела сцены — осевые коробки без поворотов: минимум и
            // максимум по локальным осям корабля.
            Vector3 bmin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 bmax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            bool any = false;
            foreach (Node n in decks.FindChildren("*", "CollisionShape3D"))
            {
                var cs = (CollisionShape3D)n;
                if (cs.Shape is not BoxShape3D box)
                    continue;
                var body = cs.GetParent() as StaticBody3D;
                if (body == null || (body.CollisionLayer & CollisionLayers.Deck) == 0u)
                    continue;
                Transform3D xf = body.GlobalTransform * cs.Transform;
                Vector3 c = ship.ToLocal(xf.Origin);
                Vector3 e = box.Size * 0.5f;
                bmin = new Vector3(Mathf.Min(bmin.X, c.X - e.X),
                                   Mathf.Min(bmin.Y, c.Y - e.Y),
                                   Mathf.Min(bmin.Z, c.Z - e.Z));
                bmax = new Vector3(Mathf.Max(bmax.X, c.X + e.X),
                                   Mathf.Max(bmax.Y, c.Y + e.Y),
                                   Mathf.Max(bmax.Z, c.Z + e.Z));
                any = true;
            }
            if (!any)
                return false;
            decks.SetMeta("DeckAABBMin", bmin);
            decks.SetMeta("DeckAABBMax", bmax);
        }

        Vector3 mn = (Vector3)decks.GetMeta("DeckAABBMin");
        Vector3 mx = (Vector3)decks.GetMeta("DeckAABBMax");
        return local.X >= mn.X && local.X <= mx.X
            && local.Y >= mn.Y && local.Y <= mx.Y
            && local.Z >= mn.Z && local.Z <= mx.Z;
    }
}
