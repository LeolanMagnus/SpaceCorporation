using Godot;

namespace SpaceCorporation;

/// <summary>
/// Прицельная марка HUD (hud-plan.md §1.1): тонкий крест с разрывом в центре,
/// рисуется в центре экрана. Текст не содержит — переносить нечего.
/// </summary>
public partial class Crosshair : Control
{
    /// <summary>Длина одного штриха креста.</summary>
    private const float ArmLength = 9f;
    /// <summary>Отступ штрихов от центра (разрыв под точку прицела).</summary>
    private const float Gap = 5f;

    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(60, 60);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Draw()
    {
        var c = Size / 2f;
        var color = HudTheme.TextAccent;
        color.A = 0.85f;

        // Четыре штриха креста с разрывом в центре.
        DrawLine(new Vector2(c.X - Gap - ArmLength, c.Y), new Vector2(c.X - Gap, c.Y), color, 1.5f);
        DrawLine(new Vector2(c.X + Gap, c.Y), new Vector2(c.X + Gap + ArmLength, c.Y), color, 1.5f);
        DrawLine(new Vector2(c.X, c.Y - Gap - ArmLength), new Vector2(c.X, c.Y - Gap), color, 1.5f);
        DrawLine(new Vector2(c.X, c.Y + Gap), new Vector2(c.X, c.Y + Gap + ArmLength), color, 1.5f);
    }
}
