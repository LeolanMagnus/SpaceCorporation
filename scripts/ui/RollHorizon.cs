using Godot;

namespace SpaceCorporation;

/// <summary>
/// Ось ROLL (крен): авиагоризонт (PFD) вокруг центра экрана: дуга шкалы крена
/// сверху (0/±10/±20/±30, зелёный клин допуска ±AlignTolDeg), указатель крена
/// скользит по дуге, линия горизонта вращается вместе с кораблём, неподвижный
/// эталон в центре. Спрайт дизайнера — подложка шкалы (смотрит «ровно»,
/// указатель и горизонт рисуем кодом).
/// </summary>
public partial class RollHorizon : AlignAxisIndicator
{
    public override void _Draw()
    {
        Vector2 c = GetViewportRect().Size * 0.5f;
        float r = AlignOverlay.Radius(GetViewportRect().Size);

        bool ok = Mathf.Abs(E.RollDeg) <= Docking.AlignTolDeg;
        var col = ok ? AlignOverlay.Green : AlignOverlay.Red;
        var frame = AlignOverlay.Frame;
        float roll = Mathf.Clamp(E.RollDeg, -90f, 90f);
        float rollRad = Mathf.DegToRad(roll);

        // Линия горизонта: вращается на −крен корабля вокруг центра, внутри
        // внутреннего кольца схемы (0.62r).
        float ri = r * 0.62f;
        var horizon = new Vector2(Mathf.Cos(-rollRad), Mathf.Sin(-rollRad));
        DrawLine(c - horizon * ri * 0.8f, c + horizon * ri * 0.8f,
                 new Color(frame.R, frame.G, frame.B, 0.6f), 2f);
        // Наклонные штрихи горизонта (стандарт PFD ±10/±20), по стороне «земли».
        var down = new Vector2(-horizon.Y, horizon.X);
        foreach (float d in new[] { ri * 0.28f, ri * 0.5f })
            DrawLine(c + down * d - horizon * ri * 0.15f, c + down * d + horizon * ri * 0.15f,
                     new Color(frame.R, frame.G, frame.B, 0.35f), 1f);

        if (!DrawSpriteCentered(c, 2.2f * r, new Color(1, 1, 1, 0.9f)))
        {
            // Дуга шкалы крена (вверху) + деления 0/±30/±60/±90 + зелёный клин
            // допуска. П13: размах шкалы ±90° — указатель заметнее ходит.
            float ro = r;
            float maxDeg = Mathf.DegToRad(90f);
            DrawArc(c, ro, -Mathf.Pi / 2f - maxDeg, -Mathf.Pi / 2f + maxDeg, 61,
                    new Color(frame.R, frame.G, frame.B, 0.7f), 2f);
            float tolRad = Mathf.DegToRad(Docking.AlignTolDeg);
            DrawArc(c, ro, -Mathf.Pi / 2f - tolRad, -Mathf.Pi / 2f + tolRad, 9,
                    new Color(AlignOverlay.Green.R, AlignOverlay.Green.G, AlignOverlay.Green.B, 0.8f), 4f);
            foreach (float d in new[] { -90f, -60f, -45f, -30f, 0f, 30f, 45f, 60f, 90f })
            {
                float a = -Mathf.Pi / 2f + Mathf.DegToRad(d);
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                float len = (d % 30f == 0f) ? r * 0.09f : r * 0.05f;
                DrawLine(c + dir * ro, c + dir * (ro + len), frame, 2f);
            }
        }

        // Указатель крена: треугольник, скользящий по дуге шкалы.
        float aPtr = -Mathf.Pi / 2f + rollRad;
        var pDir = new Vector2(Mathf.Cos(aPtr), Mathf.Sin(aPtr));
        var pBase = c + pDir * (r + 4f);
        DrawColoredPolygon(pBase + pDir * 10f,
                           pBase + new Vector2(-pDir.Y, pDir.X) * 6f,
                           pBase - new Vector2(-pDir.Y, pDir.X) * 6f, col);

        // Неподвижный эталон (самолётик) в центре.
        DrawLine(c + new Vector2(-26f, 0), c + new Vector2(-8f, 0), col, 3f);
        DrawLine(c + new Vector2(8f, 0), c + new Vector2(26f, 0), col, 3f);
        DrawCircle(c, 3f, col);
    }

    private void DrawColoredPolygon(Vector2 a, Vector2 b, Vector2 p, Color col) =>
        DrawColoredPolygon(new Vector2[] { a, b, p }, col);
}

/// <summary>
/// Оси YAW+PITCH (рыскание/тангаж): большая точка прицеливания в центре HUD,
/// в которую надо совместить маркер носа. Точка отклоняется от центра:
/// X — рыскание (±30° → ±r), Y — тангаж. Зелёная — обе оси в допуске,
/// красная — хотя бы одна вне. Спрайт дизайнера — сама точка (AimDot).
