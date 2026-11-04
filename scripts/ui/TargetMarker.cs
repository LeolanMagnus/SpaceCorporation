using Godot;

namespace SpaceCorporation;

/// <summary>
/// Маркер выбранной цели на HUD (hud-plan.md §1.1 / Этап H3): рамка-уголки
/// следует за проекцией цели; если цель за экраном или за камерой — маркер
/// прижимается (clamp) к краю экрана в направлении цели. Под рамкой — имя и
/// дистанция. Рисуется кодом на Control во весь экран (mouse_filter Ignore).
///
/// Проекция считается вручную через view- space камеры (без UnprojectPosition —
/// за камерой он даёт вывернутые координаты, а нам нужен честный флаг «за»).
/// </summary>
public partial class TargetMarker : Control
{
    private const float CornerLen = 14f;   // длина уголка рамки
    private const float MinR = 22f;        // полуразмер рамки вблизи
    private const float MaxR = 90f;        // ...вдали (не бесконечный)
    private const float EdgeMargin = 26f;  // отступ кламп-маркера от края экрана
    private const float FocalRef = 1150f;  // базис для масштаба рамки по дистанции

    private TargetingService _svc = null!;
    private Label _lbl = null!;
    private bool _labelsOn;

    /// <summary>Показывать ли подписи world-маркеров (хоткей L, hud_labels).</summary>
    public bool LabelsOn { get => _labelsOn; set => _labelsOn = value; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        // TargetingService — соседу HudRoot (дитя HudLayer): путь от HudRoot вверх.
        _svc = GetParent().GetNode<TargetingService>("../TargetingService");
        _lbl = new Label
        {
            Name = "LblTargetInfo",
            MouseFilter = MouseFilterEnum.Ignore,
            HorizontalAlignment = HorizontalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _lbl.AddThemeColorOverride("font_color", HudTheme.TextPrimary);
        _lbl.AddThemeFontSizeOverride("font_size", 12);
        AddChild(_lbl);
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        QueueRedraw(); // рамка должна следовать за целью каждый кадр
        UpdateLabel();
    }

    private void UpdateLabel()
    {
        var t = _svc.TargetNode;
        if (t == null || !GodotObject.IsInstanceValid(t))
        {
            _lbl.Visible = false;
            return;
        }
        if (!TargetingService.WorldPosOf(t, out Vector3 wpos)) { _lbl.Visible = false; return; }
        var cam = GetViewport().GetCamera3D();
        if (cam == null) { _lbl.Visible = false; return; }

        float dist = cam.GlobalPosition.DistanceTo(wpos);
        _lbl.Visible = true;
        // Позицию подписи ставит _Draw (PlaceLabel / PlaceLabelClamped — там же
        // кламп к краю экрана); здесь только текст, цвет и видимость.
        _lbl.Text = $"{_svc.TargetName}\n{FormatDist(dist)}";
        _lbl.AddThemeColorOverride("font_color", HudTheme.FactionColor(_svc.TargetFaction)); //.H4
    }

    private static string FormatDist(float m) =>
        m >= 1000f ? $"{m / 1000f:F2} km" : $"{m:F0} m";

    public override void _Draw()
    {
        var t = _svc.TargetNode;
        if (t == null || !GodotObject.IsInstanceValid(t)) return;
        if (!TargetingService.WorldPosOf(t, out Vector3 wpos)) return;
        var cam = GetViewport().GetCamera3D();
        if (cam == null) return;

        Vector2 center = Size / 2f;
        var view = cam.GlobalTransform.AffineInverse();
        Vector3 local = view * wpos;              //_cam-space: -Z вперёд
        bool behind = local.Z >= -0.5f;

        Color col = HudTheme.FactionColor(_svc.TargetFaction); //.цвет рамки — по фракции цели (H4)
        float r = Size.X; // радиус рамки: крупнее вблизи, мельчит с дистанцией
        float dist = Mathf.Abs(local.Z) > 0.01f ? Mathf.Abs(local.Z) : 0.01f;
        r = Mathf.Clamp(FocalRef / dist * 18f, MinR, MaxR);

        Vector2 p;   // точка центра маркера на экране
        if (!behind && local.Z < -0.01f)
        {
            float fy = Size.Y * 0.5f / Mathf.Tan(Mathf.DegToRad(cam.Fov) * 0.5f);
            p = center + new Vector2(local.X * fy / -local.Z, -local.Y * fy / -local.Z);
            bool offscreen = p.X < 0 || p.X > Size.X || p.Y < 0 || p.Y > Size.Y;
            if (offscreen) p = ClampToEdge(center, p - center);
            else
            {
                DrawFrame(p, r, col);
                PlaceLabel(p, r);
                return;
            }
            // цель спроецировалась за пределы экрана — рисуем кламп-маркер
            DrawClampMarker(center, p - center, r, col);
            PlaceLabelClamped(center, p - center, r);
        }
        else
        {
            // цель ЗА камерой: направление на экране — обратное от бокового
            // отклонения (X4 показывает маркер «своей спины» со стороной цели).
            var dir = new Vector2(-local.X, -local.Y);
            if (dir.LengthSquared() < 0.0001f) dir = new Vector2(0, 1);
            DrawClampMarker(center, dir, r, col);
            PlaceLabelClamped(center, dir, r);
        }
    }

    /// <summary>Рамка-уголки (ломаные 4 угла, как в X4).</summary>
    private void DrawFrame(Vector2 c, float r, Color col)
    {
        float h = r, k = CornerLen;
        var pts = new (Vector2 a, Vector2 b, Vector2 d)[]
        {
            (new Vector2(c.X - h, c.Y - h), new Vector2(c.X - h + k, c.Y - h), new Vector2(c.X - h, c.Y - h + k)),
            (new Vector2(c.X + h, c.Y - h), new Vector2(c.X + h - k, c.Y - h), new Vector2(c.X + h, c.Y - h + k)),
            (new Vector2(c.X - h, c.Y + h), new Vector2(c.X - h + k, c.Y + h), new Vector2(c.X - h, c.Y + h - k)),
            (new Vector2(c.X + h, c.Y + h), new Vector2(c.X + h - k, c.Y + h), new Vector2(c.X + h, c.Y + h - k)),
        };
        foreach (var (a, b, d) in pts)
        {
            DrawLine(a, b, col, 1.5f);
            DrawLine(a, d, col, 1.5f);
        }
    }

    /// <summary>Кламп-маркер у края: тот же уголок + стрелка-штрих к центру.</summary>
    private void DrawClampMarker(Vector2 center, Vector2 dir, float r, Color col)
    {
        Vector2 p = ClampToEdge(center, dir);
        DrawFrame(p, r * 0.6f, col);
        Vector2 n = dir.Normalized();
        DrawLine(p, p + n * (r * 0.6f + 6f), col, 1.5f);
    }

    /// <summary>Точка на прямоугольнике экрана (с отступом) вдоль направления dir.</summary>
    private Vector2 ClampToEdge(Vector2 center, Vector2 dir)
    {
        if (dir.LengthSquared() < 0.0001f) dir = new Vector2(0, 1);
        Vector2 half = new Vector2(Size.X / 2f - EdgeMargin, Size.Y / 2f - EdgeMargin);
        Vector2 n = dir.Normalized();
        float tx = n.X != 0 ? half.X / Mathf.Abs(n.X) : float.PositiveInfinity;
        float ty = n.Y != 0 ? half.Y / Mathf.Abs(n.Y) : float.PositiveInfinity;
        return center + n * Mathf.Min(tx, ty);
    }

    private void PlaceLabel(Vector2 p, float r)
    {
        _lbl.Position = new Vector2(p.X - 100, p.Y + r + 4);
        _lbl.Size = new Vector2(200, 40);
    }

    private void PlaceLabelClamped(Vector2 center, Vector2 dir, float r)
    {
        Vector2 p = ClampToEdge(center, dir);
        float w = 160;
        _lbl.Position = new Vector2(Mathf.Clamp(p.X - w / 2, 4, Size.X - w - 4),
                                    Mathf.Clamp(p.Y - 26, 4, Size.Y - 44));
        _lbl.Size = new Vector2(w, 40);
    }
}
