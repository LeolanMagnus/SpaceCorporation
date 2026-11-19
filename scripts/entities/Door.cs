using Godot;

namespace SpaceCorporation;

/// <summary>Полотно двери шлюза: `StaticBody3D` на слое `Deck` (256) — закрытая
/// дверь перекрывает проём коллизией, открытая коллизию теряет. Сдвиг дочернего
/// Mesh «Mesh» вбок при открытии — визуальный намёк, что проём свободен (в живом
/// коде — анимация).</summary>
public partial class Door : StaticBody3D
{
    [Export] public Vector3 SlideDir { get; set; } = Vector3.Right;
    /// <summary>Ход полотна в метрах (0 = сдвига нет, только коллизия).</summary>
    [Export] public float SlideDistance { get; set; } = 0.75f;

    public void SetSlideOpen(bool open)
    {
        var mesh = GetNodeOrNull<MeshInstance3D>("Mesh");
        if (mesh == null) return;
        mesh.Position = open ? SlideDir.Normalized() * SlideDistance : Vector3.Zero;
    }
}
