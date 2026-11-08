using Godot;
using System.Collections.Generic;

namespace SpaceCorporation;

/// <summary>
/// Выбор цели локально на пиру (hud-plan.md, Этап H3): Tab (`target_cycle`) —
/// цикл по кораблям и мишеням из реестров NetworkManager. Список строится
/// каждый цикл заново (реестры живые, узлы могут спавниться/умирать) и
/// сортируется по углу к курсу камеры (ближе к взгляду — раньше), при равенстве
/// — по дистанции. Свой корабль из списка исключается.
///
/// Ничего сетевого не делает: цель — локальное понятие картинки (как камера).
/// </summary>
public partial class TargetingService : Node
{
    /// <summary>Текущая цель: узел-носитель (у мишени это Node TargetState —
    /// позиция берётся через WorldPosOf) + подпись. null = цели нет.</summary>
    public Node? TargetNode { get; private set; }
    public string TargetName { get; private set; } = "";

    /// <summary>Фракция текущей цели (byte из реплики, H4). Меняется вместе с
    /// целью; 0 = Neutral.</summary>
    public byte TargetFaction { get; private set; }

    /// <summary>Мировая позиция цели: у Node3D — своя; у TargetState (Node) —
    /// у родительского тела Target.tscn. false — позицию взять негде.</summary>
    public static bool WorldPosOf(Node n, out Vector3 pos)
    {
        if (n is Node3D n3) { pos = n3.GlobalPosition; return true; }
        if (n.GetParent() is Node3D p3) { pos = p3.GlobalPosition; return true; }
        pos = Vector3.Zero;
        return false;
    }

    /// <summary>Смена цели по хоткею (для теста/внешнего вызова).</summary>
    public int CycleCount { get; private set; }

    /// <summary>Tab: пересобрать список и переключиться на следующую цель.
    /// true — новая цель выбрана; false — кандидатов нет (цель снята).</summary>
    public bool CycleNext()
    {
        CycleCount++;
        var candidates = CollectCandidates();
        if (candidates.Count == 0)
        {
            Clear();
            return false;
        }

        int idx = 0;
        if (TargetNode != null && GodotObject.IsInstanceValid(TargetNode))
        {
            // Следующая за текущей в отсортированном списке (циклически).
            for (int i = 0; i < candidates.Count; i++)
                if (candidates[i].Node == TargetNode)
                {
                    idx = (i + 1) % candidates.Count;
                    break;
                }
        }
        TargetNode = candidates[idx].Node;
        TargetName = candidates[idx].Name;
        TargetFaction = candidates[idx].Faction; //.пара (узел, фракция) — одной точкой выхода
        return true;
    }

    /// <summary>Снять цель (цель умерла/исчезла).</summary>
    public void Clear()
    {
        TargetNode = null;
        TargetName = "";
        TargetFaction = (byte)Faction.Neutral;
    }

    /// <summary>Выбрать цель по сетевому адресу (клик по блипу радара, R3):
    /// Ship/TargetState из реестров NetworkManager. true — цель найдена.</summary>
    public bool Select(int netId)
    {
        var nm = NetworkManager.Instance;
        if (nm == null || netId == 0)
            return false;

        if (nm.FindShip(netId) is Ship ship && GodotObject.IsInstanceValid(ship))
        {
            var h = ship.GetNodeOrNull<ShipHealthState>("ShipHealthState");
            if (h != null && h.Hp <= 0)
                return false;
            var spec = ShipStatCatalog.FindByByte(ship.Net?.ShipTypeValue ?? (byte)ShipType.Courier);
            TargetNode = ship;
            TargetName = spec.DisplayName;
            TargetFaction = ship.Net?.FactionValue ?? (byte)Faction.Neutral;
            return true;
        }
        if (nm.FindTarget(netId) is TargetState t && GodotObject.IsInstanceValid(t) && t.Hp > 0)
        {
            TargetNode = t;
            TargetName = $"Target #{t.TargetNetId}";
            TargetFaction = t.FactionValue;
            return true;
        }
        // Станции (targeting-plan): тоже цели — клик по square-метке радара/оверлея.
        if (nm.FindStation(netId) is StationState st && GodotObject.IsInstanceValid(st))
        {
            TargetNode = st;
            TargetName = $"Station #{st.StationNetId}";
            TargetFaction = (byte)Faction.Neutral;
            return true;
        }
        return false;
    }

    /// <summary>Жива ли ещё цель (вызывает HudLayer каждый кадр; если цель
    /// удалена или убита — снимает её и говорит перерисоваться).</summary>
    public bool ValidateTarget()
    {
        if (TargetNode == null || !GodotObject.IsInstanceValid(TargetNode))
            return false;
        if (TargetNode is TargetState ts && ts.Hp <= 0)
            return false;
        if (TargetNode.GetParent() is Ship s)
        {
            var h = s.GetNodeOrNull<ShipHealthState>("ShipHealthState");
            if (h != null && h.Hp <= 0)
                return false;
        }
        return true;
    }

    private record struct Candidate(Node Node, string Name, Vector3 Pos, byte Faction);

    /// <summary>Корабли (живые, не свой) + мишени (живые), отсортированные по
    /// углу к направлению камеры, затем по дистанции.</summary>
    private List<Candidate> CollectCandidates()
    {
        var list = new List<Candidate>();
        var nm = NetworkManager.Instance;
        if (nm == null)
            return list;

        var eye = GetViewport()?.GetCamera3D();
        Vector3 from = eye?.GlobalPosition ?? Vector3.Zero;
        Vector3 fwd = eye != null ? -eye.GlobalTransform.Basis.Z : Vector3.Forward;

        var own = OwnShipNode();

        foreach (var (id, ship) in nm.Ships)
        {
            if (!GodotObject.IsInstanceValid(ship) || ship == own)
                continue;
            var h = ship.GetNodeOrNull<ShipHealthState>("ShipHealthState");
            if (h != null && h.Hp <= 0)
                continue; // мёртвый корабль — не цель
            var spec = ShipStatCatalog.FindByByte(ship.Net?.ShipTypeValue ?? (byte)ShipType.Courier);
            list.Add(new Candidate(ship, spec.DisplayName, ship.GlobalPosition,
                ship.Net?.FactionValue ?? (byte)Faction.Neutral)); //.фракция из реплики (H4)
        }

        foreach (var (id, t) in nm.Targets)
        {
            if (!GodotObject.IsInstanceValid(t) || t.Hp <= 0)
                continue;
            if (!WorldPosOf(t, out Vector3 tpos))
                continue;
            list.Add(new Candidate(t, $"Target #{t.TargetNetId}", tpos, t.FactionValue));
        }

        // Станции (targeting-plan): участвуют и в Tab-цикле, и в клик-захвате.
        foreach (var (id, st) in nm.Stations)
        {
            if (!GodotObject.IsInstanceValid(st))
                continue;
            if (!WorldPosOf(st, out Vector3 spos))
                continue;
            list.Add(new Candidate(st, $"Station #{st.StationNetId}", spos, (byte)Faction.Neutral));
        }

        list.Sort((a, b) =>
        {
            float da = AngleScore(a.Pos - from, fwd);
            float db = AngleScore(b.Pos - from, fwd);
            int c = da.CompareTo(db);
            return c != 0 ? c : from.DistanceSquaredTo(a.Pos).CompareTo(from.DistanceSquaredTo(b.Pos));
        });
        return list;
    }

    /// <summary>Мера «далеко от взгляда» (0 = точно впереди). Нормализованная
    /// обратная проекция: чем больше угол, тем больше значение.</summary>
    private static float AngleScore(Vector3 to, Vector3 fwd)
    {
        if (to.LengthSquared() < 0.0001f)
            return 0f;
        float cos = fwd.Normalized().Dot(to.Normalized());
        return 1f - cos; // 0 впереди, 2 позади
    }

    /// <summary>Свой корабль локального игрока — исключается из целей.</summary>
    private Node3D? OwnShipNode()
    {
        foreach (var n in GetTree().GetNodesInGroup("player"))
            if (n is Player p && GodotObject.IsInstanceValid(p)
                && p.IsAttached && p.AttachedTo != null)
                for (Node c = p.AttachedTo; c != null; c = c.GetParent())
                    if (c is Ship s)
                        return s;
        return null;
    }
}
