using Godot;

namespace SpaceCorporation;

/// <summary>
/// Ось ALT (высота, П13): вертикальная лестница справа от кольца схемы.
/// Цель — точка стыка (нулевая риска с эталонной засечкой); маркер корабля
/// скользит по лестнице по AltM (выше точки стыка — выше). Зелёная зона —
/// допуск AlignAltTol; в неё входит и «фиксация». Вид сверху не нужен:
/// шкала вертикальна, как высотомер на заходе. Спрайт дизайнера — подложка
/// лестницы (смотрит «на нуле», маркер рисуем кодом).
/// </summary>
public partial class AltitudeIndicator : AlignAxisIndicator
{
    public override void _Draw()
    {
        Vector2 c = GetViewportRect().Size * 0.5f;
        float r = AlignOverlay.Radius(GetViewportRect().Size);
        float x = c.X + r + 34f;              // лестница снаружи кольца справа
        float half = r * 0.8f;                // половина высоты лестницы
        float k = half / Docking.CaptureRadius; // м → px (весь радиус зоны захвата)

        bool ok = Mathf.Abs(E.AltM) <= Docking.AlignAltTol;
        var col = ok ? AlignOverlay.Green : AlignOverlay.Red;
        var frame = AlignOverlay.Frame;

        if (!DrawSpriteCentered(new Vector2(x, c.Y), half * 2.2f, new Color(1, 1, 1, 0.9f)))
        {
            // Рейка лестницы + риски каждые 5 м.
            DrawLine(new Vector2(x, c.Y - half), new Vector2(x, c.Y + half),
                     new Color(frame.R, frame.G, frame.B, 0.6f), 2f);
            for (float m = -15f; m <= 15f; m += 5f)
            {
                float y = c.Y - m * k;
                float len = Mathf.Abs(m) % 10f == 0f ? 10f : 5f;
                DrawLine(new Vector2(x - len, y), new Vector2(x + len, y),
                         new Color(frame.R, frame.G, frame.B, 0.4f), 1.5f);
            }
            // Зелёная зона допуска ±AlignAltTol вокруг нуля.
            float zt = Docking.AlignAltTol * k;
            DrawLine(new Vector2(x, c.Y - zt), new Vector2(x, c.Y + zt),
                     new Color(AlignOverlay.Green.R, AlignOverlay.Green.G, AlignOverlay.Green.B, 0.5f), 5f);
        }

        // Эталон точки стыка: засечка-уголок на нуле (куда ставить корабль).
        DrawLine(new Vector2(x - 14f, c.Y), new Vector2(x - 4f, c.Y), col, 3f);
        DrawLine(new Vector2(x - 14f, c.Y - 5f), new Vector2(x - 14f, c.Y + 5f), col, 2f);

        // Маркер корабля: AltM (выше точки стыка — выше на лестнице), кламп к рейке.
        float ym = Mathf.Clamp(c.Y - E.AltM * k, c.Y - half, c.Y + half);
        DrawLine(new Vector2(x - 6f, ym), new Vector2(x + 12f, ym), col, 3f);
        DrawColoredPolygon(new Vector2[] {
            new Vector2(x + 12f, ym - 5f), new Vector2(x + 12f, ym + 5f),
            new Vector2(x + 5f, ym) }, col);
    }
}
