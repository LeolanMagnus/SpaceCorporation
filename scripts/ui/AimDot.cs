using Godot;

namespace SpaceCorporation;

/// <summary>
/// Оси YAW+PITCH (рыскание/тангаж): большая точка прицеливания в центре HUD,
/// в которую надо совместить маркер носа. Точка отклоняется от центра:
/// X — рыскание (±30° → ±r), Y — тангаж. Зелёная — обе оси в допуске,
/// красная — хотя бы одна вне. Спрайт дизайнера — сама точка (AimDot).
/// </summary>
public partial class AimDot : AlignAxisIndicator
{
    public override void _Draw()
    {
        Vector2 c = GetViewportRect().Size * 0.5f;
        float r = AlignOverlay.Radius(GetViewportRect().Size);

        bool ok = Mathf.Abs(E.YawDeg) <= Docking.AlignTolDeg
                  && Mathf.Abs(E.PitchDeg) <= Docking.AlignTolDeg;
        var col = ok ? AlignOverlay.Green : AlignOverlay.Red;

        // Смещение точки: куда пилоту вести маркер носа. Рыскание по X,
        // тангаж по Y — знаки反向 к ошибке: точка показывает, где цель
        // относительно носа (нос увален вправо → точка влево).
        // масштаб ±30° = радиус рамки, кламп в кольцо.
        float k = r / 30f;
        var off = new Vector2(Mathf.Clamp(-E.YawDeg * k, -r, r),
                              Mathf.Clamp(E.PitchDeg * k, -r, r));
        var p = c + off;

        if (!DrawSpriteCentered(p, 44f, col))
        {
            DrawCircle(p, 12f, new Color(col.R, col.G, col.B, 0.55f));
            DrawArc(p, 12f, 0f, Mathf.Tau, 24, col, 2f);
            DrawCircle(p, 2.5f, col);
        }

        // Нити к центра — видно, куда доворачивать.
        if (off.Length() > 14f)
        {
            var dir = off.Normalized();
            DrawLine(p + dir * 14f, p + dir * (off.Length() - 4f),
                     new Color(col.R, col.G, col.B, 0.5f), 1f, true);
        }
    }
}
