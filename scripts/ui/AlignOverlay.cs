using Godot;

namespace SpaceCorporation;

/// <summary>
/// Механика посадки, П6 (docs/landing-mechanic-plan.md): голографическая схема
/// выравнивания по центру экрана, пока свой корабль в зоне посадочной площадки
/// после «посадка разрешена». Три угловые ошибки (рыскание/тангаж/крен) и
/// горизонтальное смещение; красный — вне допуска (5° / AlignOffsetTol м),
/// зелёный — в допуске. Когда всё в допуске — «фиксация»: схема зеленеет,
/// появляются угловые скобки и подпись. Рисуется кодом (голограмма), не
/// дублирует прицел пилота (AimReticle соседствует — свои слои).
///
/// Композиция (голографический «окуляр»):
///  - главное кольцо + крест центра — рамка схемы;
///  - маркер рыскания — дуга вверху: стрелка уходит влево/вправо от верха
///    за метку «0» на величину угла;
///  - маркер тангажа — вертикальная шкала вправо от центра;
///  - маркер крена — дуга вокруг центра (по часовой/против);
///  - план площадки внизу справа — квадратик-площадка с точкой захода,
///    маркер корабля со смещением OffsetXY;
///  - текстовые значения ошибок по каждой оси (красный/зелёный).
/// </summary>
public partial class AlignOverlay : Control
{
    private static readonly Color Green = new(0.15f, 0.95f, 0.45f, 0.95f);
    private static readonly Color Red = new(0.95f, 0.25f, 0.25f, 0.95f);
    private static readonly Color Frame = new(0.45f, 0.8f, 1.0f, 0.55f);

    private Docking.AlignErrorSet _e;
    private bool _active;

    public bool IsActive => _active;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        ZIndex = 24; // под стрелкой (25), над прицельными слоями (20)
        Visible = false;
    }

    public void ShowWith(in Docking.AlignErrorSet e)
    {
        _e = e;
        if (!_active)
        {
            _active = true;
            Visible = true;
        }
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
        float r = Mathf.Min(GetViewportRect().Size.Y * 0.18f, 150f);
        float tolRad = Docking.AlignTolRad;

        Color cYaw = Mathf.Abs(_e.YawDeg) <= Docking.AlignTolDeg ? Green : Red;
        Color cPitch = Mathf.Abs(_e.PitchDeg) <= Docking.AlignTolDeg ? Green : Red;
        Color cRoll = Mathf.Abs(_e.RollDeg) <= Docking.AlignTolDeg ? Green : Red;
        Color cOff = _e.OffsetM <= Docking.AlignOffsetTol ? Green : Red;

        // Рамка окуляра: кольцо + крест.
        DrawArc(c, r, 0f, Mathf.Tau, 48, Frame, 2f);
        DrawArc(c, r * 0.62f, 0f, Mathf.Tau, 40, new Color(Frame.R, Frame.G, Frame.B, 0.22f), 1f);
        float tick = r * 0.08f;
        foreach (var a in new[] { 0f, Mathf.Pi / 2f, Mathf.Pi, 3f * Mathf.Pi / 2f })
        {
            var d = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            DrawLine(c + d * (r - tick), c + d * r, Frame, 2f);
        }

        // Рыскание: дуга вверху, «0» сверху; стрелка отклоняется на угол
        // рыскания (перерисовываем относительно верха экрана).
        float yaw = Mathf.Clamp(_e.YawDeg, -30f, 30f) * Mathf.DegToRad(1f);
        float yawA = -Mathf.Pi / 2f + yaw;
        DrawArc(c, r * 0.85f, -Mathf.Pi / 2f - tolRad, -Mathf.Pi / 2f + tolRad, 8,
                new Color(Green.R, Green.G, Green.B, 0.4f), 4f);
        var yawP = c + new Vector2(Mathf.Cos(yawA), Mathf.Sin(yawA)) * r * 0.85f;
        DrawLine(c, yawP, new Color(cYaw.R, cYaw.G, cYaw.B, 0.35f), 1f);
        DrawYawHead(yawP, yawA, cYaw);

        // Крен: маркер на среднем кольце — линия из центра под углом крена
        // плюс шкала допустимого сектора справа (крен относительно «верха»).
        float roll = Mathf.Clamp(_e.RollDeg, -30f, 30f) * Mathf.DegToRad(1f);
        DrawArc(c, r * 0.62f, -Mathf.Pi / 2f - tolRad, -Mathf.Pi / 2f + tolRad, 8,
                new Color(Green.R, Green.G, Green.B, 0.4f), 4f);
        var rollP = c + new Vector2(Mathf.Cos(-Mathf.Pi / 2f + roll),
                                    Mathf.Sin(-Mathf.Pi / 2f + roll)) * r * 0.62f;
        DrawLine(c, rollP, cRoll, 3f);
        DrawCircle(rollP, 4f, cRoll);

        // Тангаж: вертикальная шкала справа от центра: зелёный сектор допуска
        // и ползунок на величине угла.
        float barX = c.X + r + 26f;
        float barH = r;
        DrawLine(new Vector2(barX, c.Y - barH), new Vector2(barX, c.Y + barH),
                 new Color(Frame.R, Frame.G, Frame.B, 0.4f), 1f);
        float px = tolRad / (30f * Mathf.DegToRad(1f)) * barH;
        DrawLine(new Vector2(barX - 6f, c.Y - px), new Vector2(barX + 6f, c.Y - px),
                 Green, 3f); // допуск ±5° на шкале 30°
        float py = c.Y + Mathf.Clamp(_e.PitchDeg, -30f, 30f) / 30f * barH;
        DrawLine(new Vector2(barX - 10f, py), new Vector2(barX + 10f, py), cPitch, 3f);

        // План площадки (вид сверху) внизу по центру: квадратик площадки,
        // метка захода и маркер корабля со смещением.
        float mini = r * 0.5f;
        var mc = new Vector2(c.X, c.Y + r + mini + 34f);
        DrawRect(new Rect2(mc - Vector2.One * mini, Vector2.One * 2f * mini),
                 new Color(Frame.R, Frame.G, Frame.B, 0.3f), false, 1f);
        DrawLine(mc - Vector2.One * mini * 0.15f, mc + Vector2.One * mini * 0.15f, Frame, 1f);
        float scale = mini / 8f; // ±8 м площади плана
        var mOff = new Vector2(_e.OffsetXY.Y, -_e.OffsetXY.X) * scale;
        DrawLine(mc, mc + mOff, new Color(cOff.R, cOff.G, cOff.B, 0.4f), 1f);
        DrawCircle(mc + mOff, 5f, cOff);
        DrawArc(mc + mOff, 9f, 0f, Mathf.Tau, 16, cOff, 1f);

        // Числа ошибок: крен/тангаж/рыскание + смещение, цвет по допуску.
        var font = ThemeDB.FallbackFont;
        int fs = ThemeDB.FallbackFontSize;
        void Val(string txt, Color col, Vector2 pos) =>
            DrawString(font, pos, txt, HorizontalAlignment.Center, -1, fs, col);
        float vy = c.Y - r - 26f;
        Val($"РЫСК {_e.YawDeg:+0.0;-0.0}°", cYaw, new Vector2(c.X - 90f, vy));
        Val($"ТАНГ {_e.PitchDeg:+0.0;-0.0}°", cPitch, new Vector2(c.X, vy));
        Val($"КРЕН {_e.RollDeg:+0.0;-0.0}°", cRoll, new Vector2(c.X + 90f, vy));
        Val($"СМЕЩ {_e.OffsetM:0.0} м", cOff, new Vector2(mc.X, mc.Y + mini + 20f));

        // Фиксация: все оси зелёные — угловые скобки + подпись.
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
            Val("ЗАХВАТ", Green, new Vector2(c.X, c.Y + r + 24f));
        }
    }

    /// <summary>Наконечник маркера рыскания — короткий штрих перпендикулярно
    /// радиусу в точке дуги.</summary>
    private void DrawYawHead(Vector2 p, float angle, Color col)
    {
        var perp = new Vector2(-Mathf.Sin(angle), Mathf.Cos(angle));
        DrawLine(p - perp * 6f, p + perp * 6f, col, 3f);
        DrawCircle(p, 3.5f, col);
    }
}
