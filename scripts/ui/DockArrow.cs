using Godot;

namespace SpaceCorporation;

/// <summary>
/// Механика посадки, П5 (docs/landing-mechanic-plan.md): зелёная стрелка на HUD,
/// указывающая на посадочную платформу станции, когда посадка одобрена для
/// локального корабля. Текстура — заглушка (assets/ui/dock_arrow_placeholder.png),
/// заменяемая художником: менять нужно только экспорт TexturePath.
/// Поведение:
///  - платформа перед камерой: стрелка лежит на линии «центр экрана → проекция
///    площадки», на расстоянии проекции (если та внутри экрана) либо прижата к
///    экранному кольцу с полями; повёрнута вдоль этой линии;
///  - платформа за камерой: прижата к кольцу в направлении за спиной пилота.
/// Текстура смотрит «вверх» (угол 0).
/// </summary>
public partial class DockArrow : Control
{
    public const string DefaultTexturePath = "res://assets/ui/dock_arrow_placeholder.png";

    /// <summary>Текстура-заглушка (заменяется дизайнером без правки кода).</summary>
    [Export] public string TexturePath { get; set; } = DefaultTexturePath;

    /// <summary>Отступ от края окна, на котором прижимается стрелка (кольцо).</summary>
    [Export] public float EdgeMargin { get; set; } = 70f;

    /// <summary>Размер отрисовки стрелки в пикселях.</summary>
    [Export] public float ArrowSize { get; set; } = 48f;

    private Texture2D? _tex;
    private Vector3 _worldTarget;
    private bool _active;

    /// <summary>Мировая цель видна снаружи (отладка/тесты).</summary>
    public bool IsActive => _active;

    public override void _Ready()
    {
        _tex = GD.Load<Texture2D>(TexturePath);
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        // Поверх прицельных слоёв (z_index 20 у оверлеев) — видна всегда при активации.
        ZIndex = 25;
        Visible = false;
    }

    /// <summary>Включить стрелку на мировую точку (HudLayer вызывает, пока
    /// docking-фаза локального корабля Approved/Capturing/Latched).</summary>
    public void ShowAt(Vector3 worldTarget)
    {
        _worldTarget = worldTarget;
        _active = true;
        if (!Visible)
            Visible = true;
    }

    public void HideArrow()
    {
        _active = false;
        if (Visible)
            Visible = false;
    }

    public override void _Process(double delta)
    {
        if (!_active)
            return;
        var vp = GetViewport();
        var cam = vp?.GetCamera3D();
        if (cam == null)
            return;

        Vector2 win = GetViewportRect().Size;
        var center = win * 0.5f;
        Vector3 to = _worldTarget - cam.GlobalPosition;
        if (to.LengthSquared() < 0.01f)
            return;

        Vector2 dir;
        if (to.Dot(-cam.GlobalBasis.Z) > 0f)
        {
            // Платформа перед камерой: направление на её экранную проекцию.
            Vector2 proj = cam.UnprojectPosition(_worldTarget);
            dir = proj - center;
            if (dir.LengthSquared() < 1f)
                dir = new Vector2(0, -1);
            dir = dir.Normalized();
            // Проекция внутри экрана — рисуем прямо на ней; снаружи — на кольце.
            float dx = Mathf.Clamp(proj.X - center.X, -win.X * 0.5f, win.X * 0.5f);
            float dy = Mathf.Clamp(proj.Y - center.Y, -win.Y * 0.5f, win.Y * 0.5f);
            float projDist = new Vector2(dx, dy).Length();
            float ring = RingRadius(center, dir, win);
            float dist = projDist < 8f ? ring : Mathf.Min(projDist, ring);
            Size = new Vector2(ArrowSize, ArrowSize);
            Position = center + dir * dist - Size * 0.5f;
        }
        else
        {
            // За камерой: зеркальное направление «за спину» по правой оси камеры.
            float right = to.Dot(cam.GlobalBasis.X);
            dir = new Vector2(right >= 0f ? 1f : -1f, 0f);
            float ring = RingRadius(center, dir, win);
            Size = new Vector2(ArrowSize, ArrowSize);
            Position = center + dir * ring - Size * 0.5f;
        }

        Rotation = dir.Angle() + Mathf.Pi / 2f; // текстура смотрит вверх
        QueueRedraw();
    }

    /// <summary>Радиус экранного кольца (прямоугольник с полями EdgeMargin) по
    /// направлению dir: пересечение луча из центра с краем кольца.</summary>
    private float RingRadius(Vector2 center, Vector2 dir, Vector2 win)
    {
        float rx = Mathf.Max(win.X * 0.5f - EdgeMargin, 24f);
        float ry = Mathf.Max(win.Y * 0.5f - EdgeMargin, 24f);
        float tx = Mathf.Abs(dir.X) > 1e-4f ? rx / Mathf.Abs(dir.X) : float.MaxValue;
        float ty = Mathf.Abs(dir.Y) > 1e-4f ? ry / Mathf.Abs(dir.Y) : float.MaxValue;
        return Mathf.Min(tx, ty);
    }

    public override void _Draw()
    {
        if (_tex == null || !_active)
            return;
        DrawTextureRect(_tex, new Rect2(Vector2.Zero, Size), false);
    }
}
