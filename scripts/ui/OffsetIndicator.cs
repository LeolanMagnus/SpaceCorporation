using Godot;

namespace SpaceCorporation;

/// <summary>
/// Ось OFFSET (смещение): мини-план посадочной площадки с маркером корабля.
/// П13: перенесён внутрь кольца схемы внизу (под центром) и исправлены оси:
/// план — вид сверху с осью захода вверх экрана (OffsetXY.X=fwd → вверх,
/// OffsetXY.Y=right → вправо), поэтому маркер на плане двигается туда, куда
/// корабль уходит относительно площадки. Масштаб CaptureRadius→радиус плана,
/// допуск AlignOffsetTol — зелёный квадрат в центре.
/// </summary>
public partial class OffsetIndicator : AlignAxisIndicator
{
    public override void _Draw()
    {
        Vector2 c = GetViewportRect().Size * 0.5f;
        float r = AlignOverlay.Radius(GetViewportRect().Size);
        float rp = r * 0.24f;                    // радиус мини-плана
        var cc = c + new Vector2(0f, r * 0.42f); // внутри кольца, ниже центра
        cc.Y = Mathf.Min(cc.Y, GetViewportRect().Size.Y * 0.5f + 90f);

        bool ok = E.OffsetM <= Docking.AlignOffsetTol;
        var col = ok ? AlignOverlay.Green : AlignOverlay.Red;
        var frame = AlignOverlay.Frame;

        if (!DrawSpriteCentered(cc, rp * 2.4f, new Color(1, 1, 1, 0.9f)))
        {
            // План площадки: квадрат рамки + ось захода вверх.
            DrawRect(new Rect2(cc - new Vector2(rp, rp), new Vector2(rp * 2f, rp * 2f)),
                     new Color(frame.R, frame.G, frame.B, 0.35f), false, 1f);
            // Допуск — квадрат ±AlignOffsetTol в масштабе CaptureRadius→rp.
            float qt = rp * Docking.AlignOffsetTol / Docking.CaptureRadius;
            DrawRect(new Rect2(cc - new Vector2(qt, qt), new Vector2(qt * 2f, qt * 2f)),
                     new Color(col.R, col.G, col.B, 0.25f), false, 1f);
            DrawLine(cc, cc + new Vector2(0f, -rp),
                     new Color(frame.R, frame.G, frame.B, 0.3f), 1f);
            // Стрелка оси захода (fwd) вверх — ориентир «куда смотрит площадка».
            var tip = cc + new Vector2(0f, -rp);
            DrawLine(tip + new Vector2(-4f, 5f), tip, frame, 1.5f);
            DrawLine(tip + new Vector2(4f, 5f), tip, frame, 1.5f);
        }

        // Маркер корабля на плане: fwd → вверх экрана (−Y), right → вправо.
        float m = rp / Docking.CaptureRadius;
        var p = cc + new Vector2(E.OffsetXY.Y * m, -E.OffsetXY.X * m);
        p = cc + (p - cc).LimitLength(rp);
        DrawCircle(p, 5f, col);
        DrawArc(p, 9f, 0f, Mathf.Tau, 20, col, 1.5f);
    }
}
