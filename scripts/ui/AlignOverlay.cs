using Godot;

namespace SpaceCorporation;

/// <summary>
/// Механика посадки, П6 + П12 (docs/landing-mechanic-plan.md): корень сцены
/// scenes/AlignOverlay.tscn — голографическая схема выравнивания по центру
/// экрана, пока свой корабль в зоне посадочной площадки после «посадка
/// разрешена». Рамка-окуляр (кольцо + крест) и отклик «фиксации» (угловые
/// скобки + «ЗАХВАТ») — здесь; каждая ось живёт отдельным узлом сцены
/// (AimDot — рыскание/тангаж, RollHorizon — крен, OffsetIndicator — смещение),
/// каждый заменяется спрайтом дизайнера ([Export] TexturePath).
/// Красный — вне допуска (5° / AlignOffsetTol м), зелёный — в допуске.
/// </summary>
public partial class AlignOverlay : Control
{
    internal static readonly Color Green = new(0.15f, 0.95f, 0.45f, 0.95f);
    internal static readonly Color Red = new(0.95f, 0.25f, 0.25f, 0.95f);
    internal static readonly Color Frame = new(0.45f, 0.8f, 1.0f, 0.55f);

    /// <summary>Радиус главной рамки схемы — общая мера для всех осей
    /// (узлы-оси считают геометрию от центра экрана через неё).</summary>
    public static float Radius(Vector2 win) => Mathf.Min(win.Y * 0.18f, 150f);

    private Docking.AlignErrorSet _e;
    private bool _active;
    private AlignAxisIndicator[] _axes = System.Array.Empty<AlignAxisIndicator>();

    public bool IsActive => _active;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        ZIndex = 24; // под стрелкой (25), над прицельными слоями (20)
        CollectAxes(this);
        Visible = false;
    }

    private static void CollectAxes(AlignOverlay root)
    {
        var list = new System.Collections.Generic.List<AlignAxisIndicator>();
        void Recurse(Control n)
        {
            foreach (var ch in n.GetChildren())
                if (ch is Control c)
                {
                    if (c is AlignAxisIndicator a)
                        list.Add(a);
                    Recurse(c);
                }
        }
        Recurse(root);
        root._axes = list.ToArray();
    }

    public void ShowWith(in Docking.AlignErrorSet e)
    {
        _e = e;
        if (!_active)
        {
            _active = true;
            Visible = true;
        }
        foreach (var a in _axes)
            a.SetError(in e);
        QueueRedraw();
    }

    public void HideOverlay()
    {
        if (!_active)
            return;
        _active = false;
        Visible = false;
    }

    public override void _Draw()
    {
        if (!_active)
            return;
        Vector2 c = GetViewportRect().Size * 0.5f;
        float r = Radius(GetViewportRect().Size);

        // Рамка окуляра: кольцо + крест.
        DrawArc(c, r, 0f, Mathf.Tau, 48, Frame, 2f);
        DrawArc(c, r * 0.62f, 0f, Mathf.Tau, 40, new Color(Frame.R, Frame.G, Frame.B, 0.22f), 1f);
        float tick = r * 0.08f;
        foreach (var a in new[] { 0f, Mathf.Pi / 2f, Mathf.Pi, 3f * Mathf.Pi / 2f })
        {
            var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            DrawLine(c + d * (r - tick), c + d * r, Frame, 2f);
        }

        // Фиксация: все оси в допуске — угловые скобки + подпись.
        if (_e.Within)
        {
            float bx = r + 18f;
            foreach (var s in new[] { new Vector2(-1, -1), new Vector2(1, -1),
                                      new Vector2(-1, 1), new Vector2(1, 1) })
            {
                var p = c + new Vector2(s.X * bx, s.Y * bx);
                DrawLine(p, p + new Vector2(-s.X * 16f, 0f), Green, 3f);
                DrawLine(p, p + new Vector2(0f, -s.Y * 16f), Green, 3f);
            }
        }
    }
}
