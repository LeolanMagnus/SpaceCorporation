using Godot;
using System.Collections.Generic;
using SpaceCorporation.Net;
using SpaceCorporation.UI;

namespace SpaceCorporation;

/// <summary>
/// Сборщик контактов радара (radar-plan.md §2.2, этап R2): Godot-слой между
/// реестрами NetworkManager и чистой <see cref="RadarModel"/>.
///
/// Источник — клиентская копия мира: обход итерируемых реестров
/// <c>Ships/Targets/Stations</c> (уже есть в NetworkManager). Для каждого —
/// репликованные NetState/TargetState/StationState поля, поэтому радар видит то
/// же, что доехало репликой (FarRadius 4000 ⊃ радиус радара 2000).
///
/// Частота сборки — 5 Гц (таймер 0.2 с): данных реплики между обновлениями не
/// больше, чем кадров. Между сборками модель экстраполирует позиции сама
/// (RadarModel.Advance: RelPos += RelVel·Δt) — блипы движутся плавно.
///
/// Ничего сетевого не делает: это картинка, как камера/TargetingService.
/// </summary>
public partial class RadarSource : Node
{
    /// <summary>Период сборки контактов, с (5 Гц).</summary>
    public const float SyncPeriod = 0.2f;

    /// <summary>Модель для панели/теста: блипы, радиус, затухание.</summary>
    public RadarModel Model { get; } = new();

    // ── данные для экранного lock-оверлея (TargetOverlay, targeting-plan) ──
    // Оверлею нужны «сырые» контакты (RelPos/AbsVel/SizeRadius/тип) и центр
    // сборки: мир между сборками (0.2 с) оверлей экстраполирует сам —
    // world = SyncCenter + RelPos + AbsVel · ContactAge (AbsVel — абсолютная
    // мировая скорость, поэтому выделение не зависит от манёвров наблюдающего
    // и не дёргается ни у статичных, ни у движущихся сущностей).

    /// <summary>Последняя сборка контактов (копия буфера, только для чтения).</summary>
    public IReadOnlyList<RadarContact> Contacts => _snapshot;

    /// <summary>Центр последней сборки (позиция своего корабля, мир).</summary>
    public Vector3 SyncCenter { get; private set; }

    /// <summary>Возраст последней сборки, с (растёт между сборками).</summary>
    public float ContactAge => _syncElapsed;

    /// <summary>Габаритный радиус мишени (куб 4×4×4 из Target.tscn):
    /// половина диагонали = 2·√3 ≈ 3.46 м.</summary>
    public const float TargetSizeRadius = 3.46f;

    /// <summary>Габаритный радиус станции (цилиндр r=60 h=40 из Station.tscn):
    /// √(60² + 20²) ≈ 63.2 м.</summary>
    public const float StationSizeRadius = 63.2f;

    /// <summary>Габаритный радиус корпуса корабля данного типа, м
    /// (половина диагонали бокса ShipTypeSpec.HullSize).</summary>
    public static float HullRadiusOf(byte shipTypeValue)
    {
        var spec = ShipStatCatalog.FindByByte(shipTypeValue);
        return spec.HullSize.Length() * 0.5f;
    }

    private readonly List<RadarContact> _snapshot = new(64);
    private float _syncElapsed = SyncPeriod; // первая сборка — сразу
    private readonly List<RadarContact> _buf = new(64);

    public override void _Process(double delta)
    {
        // 1) раз в 0.2 с — свежая сборка контактов в модель;
        _syncElapsed += (float)delta;
        if (_syncElapsed >= SyncPeriod)
        {
            _syncElapsed = 0f;
            CollectContacts(_buf);
            Model.Sync(_buf);
            // Снимок для оверлея + центр сборки (тот же кадр, что и RelPos).
            _snapshot.Clear();
            _snapshot.AddRange(_buf);
            var ownAtSync = OwnFrame();
            SyncCenter = ownAtSync.Center;
        }

        // 2) каждый кадр — экстраполяция + поворот на полный локальный базис
        // своего корабля (forward+up: roll и pitch тоже вращают радар).
        var (center, vel, fwd, up) = OwnFrame();
        _ = center; // центр в RelPos уже вшит при сборке; базис нужен для проекции
        Model.Advance((float)delta, fwd.X, fwd.Y, fwd.Z, up.X, up.Y, up.Z);

        // 3) мощность радара — характеристика корабля (Σ RadarRange по слотам
        // Loadout); без корабля/модулей — ступень по умолчанию.
        var own = OwnShipNode();
        float max = own != null ? own.Loadout.RadarRange() : 0f;
        Model.MaxRange = max > RadarModel.NearRange ? max : RadarModel.DefaultMaxRange;
    }

    /// <summary>Собрать относительные контакты в <paramref name="outBuf"/>
    /// (буфер переиспользуется, аллокаций в кадре нет). Публично — для
    /// headless-теста: собрать и покормить модель вручную.</summary>
    public void CollectContacts(List<RadarContact> outBuf)
    {
        outBuf.Clear();
        var nm = NetworkManager.Instance;
        if (nm == null)
            return;

        var (center, ownVel, _, _) = OwnFrame();
        var own = OwnShipNode();

        // --- корабли: позиция/скорость/фракция из реплики NetState ---
        foreach (var (id, ship) in nm.Ships)
        {
            if (!GodotObject.IsInstanceValid(ship) || ReferenceEquals(ship, own))
                continue;
            var net = ship.Net;
            if (net == null || net.ShipNetId == 0)
                continue;
            var health = ship.GetNodeOrNull<ShipHealthState>("ShipHealthState");
            if (health != null && health.Hp <= 0)
                continue; // мёртвый корабль с блипа не рисуется

            Vector3 p = net.NetPosition, v = net.NetLinearVelocity;
            outBuf.Add(new RadarContact
            {
                NetId = net.ShipNetId,
                RelPos = ToVec3(p - center),
                RelVel = ToVec3(v - ownVel),
                AbsVel = ToVec3(v), // мировая скорость корабля (экстраполяция оверлея)
                Faction = net.FactionValue,
                Shape = RadarShape.Ship,
                Damaged = health != null && health.Hp < health.MaxHp,
                // frame-plan шаг 9: фаза захвата видна и наблюдателю — поле
                // реплицируется (шаг 6), RadarSource на клиенте читает реплику.
                Captured = net.CaptureParentNetId != 0,
                // lock-оверлей: тип для формы метки + габаритный радиус
                // (половина диагонали корпуса) для размера рамки-выделения.
                ShipTypeValue = net.ShipTypeValue,
                SizeRadius = HullRadiusOf(net.ShipTypeValue),
            });
        }

        // --- мишени: статичные, позиция из спавн-снимка TargetState ---
        foreach (var (id, t) in nm.Targets)
        {
            if (!GodotObject.IsInstanceValid(t) || t.Hp <= 0)
                continue;
            outBuf.Add(new RadarContact
            {
                NetId = t.TargetNetId,
                RelPos = ToVec3(t.NetPosition - center),
                RelVel = Vec3.Zero,
                Faction = t.FactionValue,
                Shape = RadarShape.Target,
                Damaged = t.Hp < t.MaxHp,
                // lock-оверлей: корпус мишени — куб 4×4×4 (Target.tscn),
                // габаритный радиус = половина диагонали ≈ 3.46 м.
                SizeRadius = TargetSizeRadius,
            });
        }

        // --- станции: статичные, square-маркер; фракции нет → Neutral ---
        foreach (var (id, st) in nm.Stations)
        {
            if (!GodotObject.IsInstanceValid(st))
                continue;
            outBuf.Add(new RadarContact
            {
                NetId = st.StationNetId,
                RelPos = ToVec3(st.NetPosition - center),
                RelVel = Vec3.Zero,
                Faction = (byte)Faction.Neutral,
                Shape = RadarShape.Station,
                // lock-оверлей: цилиндр r=60 h=40 (Station.tscn) → √(60²+20²).
                SizeRadius = StationSizeRadius,
            });
        }

        // --- флаг «цель» для текущего таргета (R3 клик/рамка) ---
        int targetId = TargetNetIdOf(TargetingServiceRef?.TargetNode);
        if (targetId != 0)
            for (int i = 0; i < outBuf.Count; i++)
            {
                if (outBuf[i].NetId != targetId)
                    continue;
                var c = outBuf[i]; //.struct: меняем через локальную копию и обратно
                c.IsTarget = true;
                outBuf[i] = c;
            }
    }

    /// <summary>Сервис целей (одноимённый узел-сосед в HudLayer) или null.</summary>
    public TargetingService? TargetingServiceRef { get; set; }

    // ------------------------------------------------------------- свой корабль

    /// <summary>Кадры центра радара: позиция/скорость своего корабля (или игрока
    /// без корабля) + полный локальный базис (forward, up) для проекции:
    /// forward = −Basis.Z (нос), up = Basis.Y (локальная вертикаль — нормаль
    /// горизонтальной плоскости корабля). Roll/pitch меняют up → точки на
    /// радаре ездят вместе с креном. Right модель считает как forward × up.</summary>
    private (Vector3 Center, Vector3 Vel, Vector3 Fwd, Vector3 Up) OwnFrame()
    {
        var own = OwnShipNode();
        if (own != null)
        {
            Vector3 vel = Vector3.Zero;
            if (own is RigidBody3D rb)
                vel = rb.LinearVelocity; // локальная физика своего тела (картинка)
            else
                vel = own.Net?.NetLinearVelocity ?? Vector3.Zero;
            var b = own.GlobalTransform.Basis;
            return (own.GlobalPosition, vel, -b.Z, b.Y);
        }

        // Игрок без корабля (внутри отсека): центр = позиция игрока, курс —
        // по камере (как взгляд: радар смотрит «туда, куда смотрю»).
        foreach (var n in GetTree().GetNodesInGroup("player"))
            if (n is Player p && GodotObject.IsInstanceValid(p) && p is Node3D p3)
            {
                var cam = GetViewport()?.GetCamera3D();
                var b = cam != null ? cam.GlobalTransform.Basis : Basis.Identity;
                return (p3.GlobalPosition, Vector3.Zero, -b.Z, b.Y);
            }
        return (Vector3.Zero, Vector3.Zero, Vector3.Forward, Vector3.Up);
    }

    /// <summary>Шов автотеста (Stage6): свой корабль центра радара.</summary>
    public Ship? OwnShipForTest() => OwnShipNode();

    /// <summary>Свой корабль локального игрока — исключается из контактов
    /// (по образцу TargetingService.OwnShipNode, но типизированно: Ship).</summary>
    private Ship? OwnShipNode()
    {
        foreach (var n in GetTree().GetNodesInGroup("player"))
            if (n is Player p && GodotObject.IsInstanceValid(p)
                && p.IsAttached && p.AttachedTo != null)
                for (Node c = p.AttachedTo; c != null; c = c.GetParent())
                    if (c is Ship s)
                        return s;
        return null;
    }

    /// <summary>Сетевой адрес узла-цели (Ship → ShipNetId, TargetState →
    /// TargetNetId; прочее — 0).</summary>
    private static int TargetNetIdOf(Node? n)
    {
        if (n == null)
            return 0;
        if (n is Ship s)
            return s.Net?.ShipNetId ?? 0;
        if (n is TargetState t)
            return t.TargetNetId;
        if (n is StationState st)
            return st.StationNetId;
        if (n.GetParent() is Ship sp)
            return sp.Net?.ShipNetId ?? 0;
        return 0;
    }

    private static Vec3 ToVec3(Vector3 v) => new(v.X, v.Y, v.Z);
}
