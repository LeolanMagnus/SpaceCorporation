using Godot;

namespace SpaceCorporation;

/// <summary>
/// П12 (docs/landing-mechanic-plan.md): базовый класс индикатора одной оси
/// выравнивания для сцены scenes/AlignOverlay.tscn. Каждая ось — свой узел
/// сцены и свой скрипт-файл (OffsetIndicator / RollHorizon / AimDot), свой
/// спрайт дизайнера ([Export] TexturePath: PNG в assets/ui/, пустой —
/// code-drawn заглушка). shortcut: спрайт — статичная подложка оси,
/// динамический маркер (угол/смещение) рисуем кодом поверх; апгрейд — когда
/// дизайнер отдаст покадровые состояния вместо подложки.
/// Все узлы — FullRect оверлеи, рисуют от центра экрана (тот же центр, что у
/// рамки AlignOverlay), координатную схему задаёт AlignOverlay.Radius.
/// </summary>
public abstract partial class AlignAxisIndicator : Control
{
    /// <summary>Спрайт индикатора оси (замена дизайнером без правки кода).</summary>
    [Export] public string TexturePath { get; set; } = "";

    protected Texture2D? Tex;
    protected Docking.AlignErrorSet E;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        if (TexturePath != "")
            Tex = GD.Load<Texture2D>(TexturePath);
    }

    /// <summary>Новый набор ошибок — перерисоваться (AlignOverlay при ShowWith).</summary>
    public void SetError(in Docking.AlignErrorSet e)
    {
        E = e;
        QueueRedraw();
    }

    /// <summary>Спрайт по центру экрана шириной size px (сохраняя пропорции),
    /// с модуляцией col; false — спрайта нет.</summary>
    protected bool DrawSpriteCentered(Vector2 c, float size, Color col)
    {
        if (Tex == null)
            return false;
        float s = size / Tex.GetWidth();
        var sz = new Vector2(Tex.GetWidth() * s, Tex.GetHeight() * s);
        DrawTextureRect(Tex, new Rect2(c - sz * 0.5f, sz), false, col);
        return true;
    }
}
