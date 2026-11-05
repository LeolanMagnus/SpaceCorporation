using Godot;

namespace SpaceCorporation;

/// <summary>
/// «Скин» прицельной марки пилота: набор именованных заготовок под будущие
/// спрайты концепт-арта + процедурные параметры на случай, когда текстур ещё
/// нет. <see cref="AimReticle"/> рисует каждый элемент либо текстурой из этого
/// ресурса, либо примитивом — поэтому замена на арт — это просто назначение
/// NoseDot / CursorCross / Dash / Chevron в инспекторе, без правки кода рисования.
///
/// Ресурс (а не константы в коде) — чтобы художник мог собрать один .tres с
/// четырьмя текстурами и применить его к любой марке.
/// </summary>
[GlobalClass]
public partial class ReticleSkin : Resource
{
    // ───────────────────── Текстурные слоты (пусто = рисуем примитивом) ─────────
    // Каждый — маленькая «пиктограмма», рисуется центром в нужную точку и, где
    // уместно, повёрнутая вдоль направления на курсор.

    /// <summary>Маркер направления носа корабля — точка в центре экрана.</summary>
    [Export] public Texture2D? NoseDot;

    /// <summary>Курсор-перекрестие — рисуется в позиции курсора мыши.</summary>
    [Export] public Texture2D? CursorCross;

    /// <summary>Один штрих пунктирной линии «центр → курсор». Если не задан,
    /// линия рисуется процедурным пунктиром (<see cref="DashLength"/>/<see cref="DashGap"/>).</summary>
    [Export] public Texture2D? Dash;

    /// <summary>«Галочка» (шеврон), указывающая на курсор. Ставится на линию в
    /// 1/4, 1/2 и 3/4 и поворачивается вершиной вдоль направления на курсор.</summary>
    [Export] public Texture2D? Chevron;

    // ───────────────────────── Цвета и геометрия примитивов ─────────────────────

    /// <summary>Цвет точки-маркера носа.</summary>
    [Export] public Color NoseColor = new(0.75f, 0.95f, 1.0f, 0.95f);

    /// <summary>Полу-радиус точки-маркера носа (пикселей), если текстура не задана.</summary>
    [Export] public float NoseRadius = 4.0f;

    /// <summary>Цвет пунктирной линии «центр → курсор». Полупрозрачный по ТЗ.</summary>
    [Export] public Color DashColor = new(0.75f, 0.95f, 1.0f, 0.45f);

    /// <summary>Толщина пунктирной линии (пикселей). По ТЗ = 2.</summary>
    [Export] public float DashWidth = 2.0f;

    /// <summary>Длина одного штриха процедурного пунктира (пикселей).</summary>
    [Export] public float DashLength = 8.0f;

    /// <summary>Пробел между штрихами процедурного пунктира (пикселей).</summary>
    [Export] public float DashGap = 6.0f;

    /// <summary>Цвет галочек-шеврона.</summary>
    [Export] public Color ChevronColor = new(0.75f, 0.95f, 1.0f, 0.8f);

    /// <summary>«Плечо» галочки-шеврона (пикселей): расстояние от вершины до
    /// каждого из двух концов, если текстура не задана.</summary>
    [Export] public float ChevronSize = 9.0f;

    /// <summary>Толщина штрихов галочки (пикселей).</summary>
    [Export] public float ChevronWidth = 2.0f;

    /// <summary>Цвет курсор-перекрестия.</summary>
    [Export] public Color CursorColor = new(1.0f, 1.0f, 1.0f, 0.9f);

    /// <summary>«Плечо» курсор-перекрестия (пикселей): длина каждого луча креста.</summary>
    [Export] public float CursorArm = 8.0f;

    /// <summary>Зазор курсор-перекрестия вокруг центра (пикселей): крест не
    /// сплошной, а четыре луча с дыркой посередине.</summary>
    [Export] public float CursorGap = 3.0f;

    /// <summary>Толщина лучей курсор-перекрестия (пикселей).</summary>
    [Export] public float CursorWidth = 2.0f;
}
