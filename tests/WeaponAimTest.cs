using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Автотест этапа «наводка» (docs/weapon-aim-plan.md). Запуск:
///   godot --headless --path . res://tests/WeaponAimTest.tscn
///
/// Проверки:
///   п.1 — WeaponAim.ClampToCone: чистая математика конуса (внутри — без
///         изменений, снаружи — ровно на границе в плоскости (ось, dir),
///         коллинеарные вырождения, произвольные оси);
///   п.2 — каталог: новые орудия (5°/15°, красный/зелёный) + параметры-копии
///         лазера + «дефолт» лазера = исторический допуск 0.15 рад;
///   п.3 — сцена WeaponTurret.tscn: структура (Body/Pivot/Barrel/Muzzle),
///         ApplyWeapon перекрашивает, AimBarrel доворачивает ствол, за сектором
///         ствол стоит ровно на границе конуса (замер угла -Z ствола);
///   п.4 — Ship: срез ствола через турель (Weapon/Pivot/Muzzle), EquipWeapon
///         меняет спецификацию/лимит/серверный гейт AimToleranceM/цвет трассера,
///         NextWeaponType циклит каталог.
/// </summary>
public partial class WeaponAimTest : Node
{
    private int _checks;
    private int _failures;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        CheckMath();
        CheckCatalog();
        CheckTurretScene();
        CheckShipEquip();
        Finish();
    }

    private void Check(bool ok, string what)
    {
        _checks++;
        if (ok)
            GD.Print($"PASS: {what}");
        else
        {
            _failures++;
            GD.PrintErr($"FAIL: {what}");
        }
    }

    private void Finish()
    {
        GD.Print($"==== WeaponAimTest: проверок={_checks}, провалов={_failures} ====");
        GD.Print(_failures == 0 ? "==== WEAPONAIMTEST OK ====" : "==== WEAPONAIMTEST FAILED ====");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    // ──────────────────────────── п.1 математика ────────────────────────────

    private void CheckMath()
    {
        const float lim = 0.2f; // ~11.46°

        // Внутри конуса — направление без изменений.
        var inside = new Vector3(0f, 0f, -1f).Rotated(Vector3.Right, 0.1f);
        Check(WeaponAim.ClampToCone(WeaponAim.BarrelAxis, inside, lim).DistanceTo(inside) < 1e-5f,
            "clamp: направление внутри конуса не меняется");

        // Снаружи — ровно на границе, угол к оси = lim.
        var outside = new Vector3(0f, 0f, -1f).Rotated(Vector3.Right, 1.0f);
        var clamped = WeaponAim.ClampToCone(WeaponAim.BarrelAxis, outside, lim);
        Check(Mathf.Abs(WeaponAim.BarrelAxis.AngleTo(clamped) - lim) < 1e-4f,
            $"clamp: снаружи → угол к оси = лимиту ({lim} рад), получено {WeaponAim.BarrelAxis.AngleTo(clamped)}");

        // Плоскость сохраняется: clamped ⊥ (axis × outside).
        Vector3 n = WeaponAim.BarrelAxis.Cross(outside).Normalized();
        Check(Mathf.Abs(n.Dot(clamped)) < 1e-4f,
            "clamp: схлопывание в плоскости (ось, dir) — линия «центр → курсор» не смещается");

        // Направление внутри конуса в другой четверти (низ-лево).
        var outside2 = new Vector3(0f, 0f, -1f).Rotated(Vector3.Up, -0.9f).Rotated(Vector3.Right, 0.3f);
        var cl2 = WeaponAim.ClampToCone(WeaponAim.BarrelAxis, outside2, lim);
        Check(Mathf.Abs(WeaponAim.BarrelAxis.AngleTo(cl2) - lim) < 1e-4f
              && Mathf.Abs(WeaponAim.BarrelAxis.Cross(outside2).Normalized().Dot(cl2)) < 1e-4f,
            "clamp: произвольное направление снаружи → граница в своей плоскости");

        // Вырождения: dir строго по оси и строго против — возвращаем ось.
        Check(WeaponAim.ClampToCone(WeaponAim.BarrelAxis, new Vector3(0f, 0f, -5f), lim)
              .DistanceTo(new Vector3(0f, 0f, -1f)) < 1e-5f,
            "clamp: dir по оси → без изменений");
        Check(WeaponAim.ClampToCone(WeaponAim.BarrelAxis, new Vector3(0f, 0f, 3f), lim)
              .DistanceTo(new Vector3(0f, 0f, -1f)) < 1e-5f,
            "clamp: dir против оси (цель за спиной) → ось, без NaN");

        // Нулевой лимит — ствол прикован к оси; нулевой dir — как есть (без NaN).
        Check(WeaponAim.ClampToCone(WeaponAim.BarrelAxis, outside, 0f)
              .DistanceTo(WeaponAim.BarrelAxis) < 1e-5f,
            "clamp: лимит 0 → ствол вдоль оси");
        Check(WeaponAim.ClampToCone(WeaponAim.BarrelAxis, Vector3.Zero, lim) == Vector3.Zero,
            "clamp: нулевой dir возвращается как есть");

        // Не-единичные входы нормализуются.
        var big = WeaponAim.ClampToCone(WeaponAim.BarrelAxis * 7f, outside * 100f, lim);
        Check(Mathf.Abs(big.Length() - 1f) < 1e-4f
              && Mathf.Abs(WeaponAim.BarrelAxis.AngleTo(big) - lim) < 1e-4f,
            "clamp: не-единичные axis/dir — результат единичный на границе");

        // Другая ось конуса (не -Z): та же логика.
        var ax = new Vector3(0f, 1f, 0f);
        var dOut = ax.Rotated(Vector3.Right, 1.2f);
        var cOut = WeaponAim.ClampToCone(ax, dOut, 0.5f);
        Check(Mathf.Abs(ax.AngleTo(cOut) - 0.5f) < 1e-4f,
            "clamp: произвольная ось конуса работает");
    }

    // ──────────────────────────── п.2 каталог ────────────────────────────

    private void CheckCatalog()
    {
        var laser = WeaponCatalog.Find(WeaponType.LaserTurret);
        var red = WeaponCatalog.Find(WeaponType.RedFiveDegTurret);
        var green = WeaponCatalog.Find(WeaponType.GreenFifteenDegTurret);

        Check(red != null && green != null, "новые орудия RedFiveDeg/GreenFifteenDeg в каталоге");
        Check(Mathf.Abs(red.AimLimitDeg - 5f) < 1e-3f && Mathf.Abs(green.AimLimitDeg - 15f) < 1e-3f,
            "углы наводки: красный 5°, зелёный 15°");
        Check(red.MeshColor.R > 0.5f && red.MeshColor.G < 0.4f && red.BeamColor.R > 0.5f,
            "красное орудие: красный меш И красный луч");
        Check(green.MeshColor.G > 0.5f && green.MeshColor.R < 0.4f && green.BeamColor.G > 0.5f,
            "зелёное орудие: зелёный меш И зелёный луч");
        Check(red.RangeM == laser.RangeM + 200f,
            "Red5 (наименьший угол наводки): дальность +200 м к лазеру (600 м, тест марки вне зоны)");
        Check(red.HullDamage == laser.HullDamage
              && red.ShieldDamage == laser.ShieldDamage && red.EnergyCost == laser.EnergyCost
              && Mathf.Abs(red.FireIntervalSec - laser.FireIntervalSec) < 1e-6f
              && red.IsHitscan == laser.IsHitscan,
            "тестовые орудия: остальные параметры — копия лазера");
        Check(green.RangeM == laser.RangeM,
            "Green15: дальность как у лазера (+200 м только у наименьшего угла наводки)");
        Check(WeaponSpec.DefaultMeshColor.B > 0.5f && WeaponSpec.DefaultMeshColor.R < 0.4f,
            "меш орудия по умолчанию — синий (ТЗ)");
        Check(Mathf.Abs(laser.AimLimitRad - 0.15f) < 2e-3f,
            "лазер: дефолт углов наводки = исторический допуск 0.15 рад (тесты Этапа 6 не плывут)");
        Check(WeaponCatalog.FindByByte(2) == green && WeaponCatalog.FindByByte(99) == laser,
            "FindByByte: 2 → зелёное, неизвестный байт → лазер (дефолт каталога)");
        Check(WeaponCatalog.DefaultWeaponSpec.Type == WeaponType.LaserTurret
              && WeaponCatalog.Types.Length == 3,
            "дефолтное орудие — лазер; каталог из 3 записей");
    }

    // ──────────────────────────── п.3 сцена турели ────────────────────────────

    private void CheckTurretScene()
    {
        var packed = GD.Load<PackedScene>("res://scenes/WeaponTurret.tscn");
        Check(packed != null, "сцена scenes/WeaponTurret.tscn грузится");
        if (packed == null) return;
        var turret = packed.Instantiate<WeaponTurret>();
        AddChild(turret);

        Check(turret.GetNodeOrNull<Node3D>("Pivot/Muzzle") != null
              && turret.GetNodeOrNull<MeshInstance3D>("Body") != null
              && turret.GetNodeOrNull<MeshInstance3D>("Pivot/Barrel") != null,
            "структура турели: Body + Pivot/Barrel + Pivot/Muzzle");
        Check(turret.Spec == null && Mathf.Abs(turret.AimLimitRad) < 1e-6f,
            "свежая турель: орудие ещё не назначено");

        var red = WeaponCatalog.Find(WeaponType.RedFiveDegTurret);
        turret.ApplyWeapon(red);
        Check(turret.Spec == red && Mathf.Abs(turret.AimLimitRad - red.AimLimitRad) < 1e-6f,
            "ApplyWeapon: спецификация и лимит наводки запомнены");
        var bodyMat = turret.GetNode<MeshInstance3D>("Body").MaterialOverride as StandardMaterial3D;
        var barrelMat = turret.GetNode<MeshInstance3D>("Pivot/Barrel").MaterialOverride as StandardMaterial3D;
        Check(bodyMat != null && bodyMat.AlbedoColor.R > 0.5f && bodyMat.AlbedoColor.B < 0.4f,
            "ApplyWeapon: меш перекрашен в цвет орудия (красный)");
        Check(barrelMat != null && !ReferenceEquals(barrelMat, bodyMat)
              && barrelMat.AlbedoColor.Equals(bodyMat.AlbedoColor),
            "ApplyWeapon: раздельные материалы башни и ствола (одинаковый цвет, независимые ресурсы)");

        turret.GlobalTransform = new Transform3D(Basis.Identity, Vector3.Zero);

        // Цель ровно по носу — ствол не отклоняется.
        turret.AimBarrel(new Vector3(0f, 0f, -1f));
        Vector3 barrelDir = -turret.GetNode<Node3D>("Pivot").GlobalTransform.Basis.Z;
        Check(barrelDir.AngleTo(new Vector3(0f, 0f, -1f)) < 1e-4f,
            "AimBarrel: цель по носу — ствол вдоль оси");

        // Цель за сектором (40° вбок): ствол строго на границе 5° и в плоскости.
        var target = new Vector3(0f, 0f, -1f).Rotated(Vector3.Up, Mathf.DegToRad(40f));
        turret.AimBarrel(target);
        barrelDir = -turret.GetNode<Node3D>("Pivot").GlobalTransform.Basis.Z;
        float ang = Mathf.RadToDeg(new Vector3(0f, 0f, -1f).AngleTo(barrelDir));
        Check(Mathf.Abs(ang - 5f) < 0.1f,
            $"AimBarrel: цель в 40° — ствол на границе 5° (получено {ang:F2}°)");
        Check(Mathf.Abs(Mathf.RadToDeg(Vector3.Up.AngleTo(barrelDir)) - 90f) < 0.1f,
            "AimBarrel: доворот в плоскости прицела (горизонтально), без крена");

        // Внутри сектора (3°): ствол смотрит точно на цель.
        var near = new Vector3(0f, 0f, -1f).Rotated(Vector3.Up, Mathf.DegToRad(3f));
        turret.AimBarrel(near);
        barrelDir = -turret.GetNode<Node3D>("Pivot").GlobalTransform.Basis.Z;
        Check(barrelDir.AngleTo(near) < 1e-4f, "AimBarrel: цель в секторе (3°) — ствол точно на цели");

        // Поворотный корабль: локальные координаты пересчитываются (мир -X,
        // корабль смотрит на -X → цель по носу мира).
        turret.GlobalTransform = new Transform3D(new Basis(Vector3.Up, Mathf.Pi / 2f), Vector3.Zero);
        turret.AimBarrel(new Vector3(-1f, 0f, 0f));
        barrelDir = -turret.GetNode<Node3D>("Pivot").GlobalTransform.Basis.Z;
        Check(barrelDir.AngleTo(new Vector3(-1f, 0f, 0f)) < 1e-4f,
            "AimBarrel: поворот турели в мире — доворот в локальной системе");
    }

    // ──────────────────────────── п.4 корабль ────────────────────────────

    private void CheckShipEquip()
    {
        var ship = (Ship)GD.Load<PackedScene>("res://scenes/Ship.tscn").Instantiate();
        AddChild(ship);

        Check(ship.GetNodeOrNull<WeaponTurret>("Weapon") != null,
            "Ship.tscn: орудие — инстанс отдельной сцены WeaponTurret (узел Weapon)");
        Check(ship.BeamOriginForTest() != Vector3.Zero
              && (ship.BeamOriginForTest() - ship.GlobalPosition).Length() > 0.5f,
            "Ship: начало луча — срез ствола турели (не центр корпуса)");

        Check(ship.CurrentWeaponType == WeaponType.LaserTurret
              && Mathf.Abs(ship.AimToleranceForTest() - 0.15f) < 2e-3f,
            "стартовое орудие корабля — лазер (конус В17 = 0.15 рад, гейты Этапа 6 целы)");

        ship.EquipWeapon(WeaponType.RedFiveDegTurret);
        Check(ship.CurrentWeaponType == WeaponType.RedFiveDegTurret,
            "EquipWeapon(Red): тип орудия сменился");
        Check(Mathf.Abs(ship.AimToleranceForTest() - Mathf.DegToRad(5f)) < 1e-4f,
            "EquipWeapon(Red): серверный угловой допуск = угол наводки 5° (единый конус)");
        var turretBodyMat = ship.GetNode<MeshInstance3D>("Weapon/Body").MaterialOverride as StandardMaterial3D;
        Check(turretBodyMat != null && turretBodyMat.AlbedoColor.R > 0.5f,
            "EquipWeapon(Red): меш турели перекрашен в красный");
        Check(ship.Weapon.BeamColor.R > 0.5f, "EquipWeapon(Red): трассер — красный");

        // NextWeaponType: цикл по каталогу (лазер → красный → зелёный → лазер).
        Check(ship.NextWeaponType() == WeaponType.GreenFifteenDegTurret,
            "NextWeaponType: после красного — зелёное");
        ship.EquipWeapon(ship.NextWeaponType());
        Check(ship.CurrentWeaponType == WeaponType.GreenFifteenDegTurret
              && Mathf.Abs(ship.AimToleranceForTest() - Mathf.DegToRad(15f)) < 1e-4f
              && ship.Weapon.BeamColor.G > 0.5f,
            "EquipWeapon(Green): 15° и зелёный трассер");
        Check(ship.NextWeaponType() == WeaponType.LaserTurret,
            "NextWeaponType: цикл каталога замыкается на лазере");

        // ── Ветка захваченной цели (Tab): единый конус с курсором (правки критики) ──
        // LockedTargetPos ищет TargetingService по имени в дереве — регистрируем
        // сервис и фиктивную цель-узел прямо здесь (ship уже в дереве теста).
        var targeting = new TargetingService { Name = "TargetingService" };
        AddChild(targeting);
        var target = new Node3D();
        AddChild(target);
        targeting.TestSetTargetForTest(target);

        ship.EquipWeapon(WeaponType.RedFiveDegTurret); // ветка захвата — под сектором 5°
        Vector3 origin = ship.BeamOriginForTest(); // ствол: начало луча/прицела
        Vector3 nose = -ship.GlobalTransform.Basis.Z;

        // Цель В секторе (5°, Red): точно на носу в 100 м — точка прицела = цель.
        target.GlobalPosition = origin + nose * 100f;
        Vector3 pIn = ship.AimPointWorld();
        Check((pIn - target.GlobalPosition).Length() < 0.05f,
            "захват внутри сектора: точка прицела = цель без изменений");
        Check(ship.WeaponAimPointWorld() is Vector3 mIn
              && (mIn - target.GlobalPosition).Length() < 0.05f,
            "захват внутри сектора: марка HUD сидит НА цели (совпадает с точкой прицела, фикс. дистанции нет)");
        Check(ship.WeaponAimMarkerInRange() == true,
            "захват внутри сектора на 100 м (< дальность 600 м): марка В зоне поражения (HUD: круг)");

        // Цель ВНЕ сектора: под 30° вправо от носа — направление на границе 5°
        // в плоскости «нос → цель», марка на глубине цели (не на самой цели).
        Vector3 offDir = nose.Rotated(Vector3.Up, Mathf.DegToRad(30f));
        target.GlobalPosition = origin + offDir * 100f;
        ship.AimPointWorld(); // пересчитывает марку (побочный эффект _weaponAimMarker)
        Check(ship.WeaponAimMarkerInRange() == false,
            "захват вне сектора: марка ВНЕ зоны поражения (HUD: круг с косым крестом)");
        Check(ship.WeaponAimPointWorld() is Vector3 mOut
              && Mathf.Abs((mOut - origin).Length() - 100f) < 0.05f,
            "захват вне сектора: марка на глубине цели (та же линия, граница конуса)");
        if (ship.WeaponAimPointWorld() is Vector3 mOut2)
        {
            Vector3 markerDir = (mOut2 - origin).Normalized();
            Check(Mathf.Abs(markerDir.AngleTo(nose) - Mathf.DegToRad(5f)) < 1e-3f,
                "захват вне сектора: направление марки ровно на границе конуса 5°");
            Check((mOut2 - target.GlobalPosition).Length() > 1f,
                "захват вне сектора: марка не на самой цели (сектор только 5°)");
            Check(Mathf.Abs(markerDir.Dot(offDir.Cross(nose).Normalized())) < 1e-2f,
                "захват вне сектора: марка в плоскости «нос → цель» (не сходит с линии)");
        }

        // Цель В секторе, но дальше дальности (Red5 = 600 м, тест +200 м):
        // марка за пределами зоны поражения → HUD рисует круг с косым крестом.
        target.GlobalPosition = origin + nose * 700f;
        ship.AimPointWorld();
        Check(ship.WeaponAimMarkerInRange() == false,
            "захват в секторе, но за дальностью 600 м: марка вне зоны поражения");
        target.GlobalPosition = origin + nose * 500f; // в зоне: 500 м < 600 м
        ship.AimPointWorld();
        Check(ship.WeaponAimMarkerInRange() == true,
            "захват в секторе и внутри дальности 600 м: марка в зоне поражения");

        // ── Режим взаимодействия (ТЗ «марка в INTERACT»): курсор не прицел ──
        // Цель вне сектора — марка по-прежнему следует за ЦЕЛЬЮ (на границе
        // конуса на глубине цели), а не за мышью.
        ship.SetInteractionMode(true);
        target.GlobalPosition = origin + offDir * 100f;
        ship.AimPointWorld();
        Check(ship.WeaponAimPointWorld() is Vector3 mInteract
              && Mathf.Abs((mInteract - origin).Length() - 100f) < 0.05f
              && (mInteract - origin).Normalized().AngleTo(nose) < Mathf.DegToRad(6f),
            "INTERACT: марка следует за захваченной целью (не за курсором)");
        Check(ship.WeaponAimMarkerInRange() == false,
            "INTERACT: цель вне сектора — марка вне зоны поражения (крест)");

        // Без захваченной цели в INTERACT — марка сводится к стволу по
        // умолчанию: направление носа, дистанция дальности орудия, «в зоне».
        targeting.Clear();
        ship.AimPointWorld();
        Check(ship.WeaponAimPointWorld() is Vector3 mNose
              && (mNose - (origin + nose * 600f)).Length() < 0.05f
              && ship.WeaponAimMarkerInRange() == true,
            "INTERACT без цели: марка на носу (ствол по умолчанию) на дальности 600 м, в зоне");
        ship.SetInteractionMode(false);

        RemoveChild(target);
        target.Free();
        RemoveChild(targeting);
        targeting.Free(); // иначе ObjectDB leak на выходе (2 инстанса)

        // Дефолтная синяя турель до первой смены — в Ship.tscn меш синий.
        var fresh = (Ship)GD.Load<PackedScene>("res://scenes/Ship.tscn").Instantiate();
        AddChild(fresh);
        var freshMat = fresh.GetNode<MeshInstance3D>("Weapon/Body").MaterialOverride as StandardMaterial3D;
        Check(freshMat != null && freshMat.AlbedoColor.B > 0.5f && freshMat.AlbedoColor.R < 0.4f,
            "свежий корабль: меш орудия — синий (дефолт WeaponSpec.DefaultMeshColor)");
    }
}
