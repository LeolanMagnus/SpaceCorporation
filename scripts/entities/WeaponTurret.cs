using Godot;

namespace SpaceCorporation;

/// <summary>
/// Орудие корабля как ОТДЕЛЬНАЯ сцена (<c>scenes/WeaponTurret.tscn</c>, этап «наводка»,
/// docs/weapon-aim-plan.md). Узел-компонент: башня с мешем + поворотный ствол.
///
/// Структура сцены:
///  • Body   — меш-«башня» (цвет = <see cref="WeaponSpec.MeshColor"/>, по умолчанию синий);
///  • Pivot  — узел доворота ствола (его Basis и поворачиваем, лимит — «углы наводки»);
///  • Barrel — меш ствола (вдоль -Z, цвет тоже MeshColor);
///  • Muzzle — срез ствола: начало трассера и хитскана (замена голого узла Muzzle
///    из Ship.tscn: Ship берёт начало луча отсюда).
///
/// Локальная ось ствола — -Z (совпадает с носом корабля). Доверт:
/// <see cref="AimBarrel"/> ставит Pivot так, чтобы ствол смотрел на локальное
/// направление прицела, но не дальше конуса <see cref="AimLimitRad"/> (математика —
/// <see cref="WeaponAim"/>). Ничего сетевого/боевого турель не знает — specs
/// приносит корабль через <see cref="ApplyWeapon"/> (задел под замену оснащения).
/// </summary>
public partial class WeaponTurret : Node3D
{
    private Node3D _pivot = null!;
    private MeshInstance3D _body = null!;
    private MeshInstance3D _barrel = null!;
    private Node3D _muzzle = null!;

    private StandardMaterial3D? _bodyMaterial;
    private StandardMaterial3D? _barrelMaterial;
    private WeaponSpec? _spec;

    /// <summary>Текущий лимит наводки в радианах (0, если оружие ещё не назначено).</summary>
    public float AimLimitRad { get; private set; }
    /// <summary>Назначенное орудие (null до первого ApplyWeapon).</summary>
    public WeaponSpec? Spec => _spec;
    /// <summary>Мировая позиция среза ствола — начало луча.</summary>
    public Vector3 MuzzleGlobal => _muzzle.GlobalPosition;
    /// <summary>Узел среза ствола (для Ship: начало трассера/хитскана).</summary>
    public Node3D MuzzleNode => _muzzle;

    public override void _Ready()
    {
        _pivot = GetNode<Node3D>("Pivot");
        _body = GetNode<MeshInstance3D>("Body");
        _barrel = GetNode<MeshInstance3D>("Pivot/Barrel");
        _muzzle = GetNode<Node3D>("Pivot/Muzzle");
        // Цвет по умолчанию — синий (ТЗ), пока корабль не принёс specs орудия.
        ApplyColor(WeaponSpec.DefaultMeshColor);
    }

    /// <summary>Смена орудия: перекраска мешей в цвет орудия и хранение лимита
    /// наводки. Единственная точка «оснастки» турели кораблём (используется из
    /// <c>Ship.ApplyWeaponSpec</c> — задел под будущую замену оснащения).</summary>
    public void ApplyWeapon(WeaponSpec spec)
    {
        _spec = spec;
        AimLimitRad = spec.AimLimitRad;
        ApplyColor(spec.MeshColor);
    }

    /// <summary>Доворот ствола за мировой целью прицела (локальный игрок).
    /// Направление переводится в локальные координаты турели и ограничивается
    /// конусом «углов наводки» вокруг -Z: за углом ствол остаётся на границе,
    /// не дёргаясь дальше.</summary>
    public void AimBarrel(Vector3 worldAimDir)
    {
        // Basis ортонормален → обратный = транспонированный.
        Vector3 localDir = GetGlobalTransform().Basis.Transposed() * worldAimDir;
        if (localDir.LengthSquared() < 1e-12f)
            return;
        Vector3 clamped = WeaponAim.ClampLocalToBarrel(localDir, AimLimitRad);

        // Basis ствола: -Z смотрит вдоль clamped. Поворот от -Z к clamped —
        // вокруг нормали плоскости (axis, clamped) на их угол (дегенерации —
        // тождество/разворот вокруг любой перпендикулярной оси).
        Vector3 axis = WeaponAim.BarrelAxis.Cross(clamped);
        float angle = WeaponAim.BarrelAxis.AngleTo(clamped);
        _pivot.Basis = axis.LengthSquared() < 1e-12f
            ? (angle < 0.001f ? Basis.Identity : new Basis(Vector3.Right, Mathf.Pi))
            : new Basis(axis.Normalized(), angle);
    }

    /// <summary>Меши турели без своих материалов — заводим РАЗДЕЛЬНЫЕ материалы
    /// башни и ствола (разная геометрия/роли: в будущем у ствола может быть
    /// свой цвет/эмиссия орудия), цвет = `WeaponSpec.MeshColor`.</summary>
    private void ApplyColor(Color color)
    {
        _bodyMaterial ??= NewTurretMaterial();
        _barrelMaterial ??= NewTurretMaterial();
        _bodyMaterial.AlbedoColor = color;
        _bodyMaterial.Emission = color * 0.6f;
        _barrelMaterial.AlbedoColor = color;
        _barrelMaterial.Emission = color * 0.6f;
        _body.MaterialOverride = _bodyMaterial;
        _barrel.MaterialOverride = _barrelMaterial;
    }

    private static StandardMaterial3D NewTurretMaterial() => new StandardMaterial3D
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        EmissionEnabled = true,
    };
}
