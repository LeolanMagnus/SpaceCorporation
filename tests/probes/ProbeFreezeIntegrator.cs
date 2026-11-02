// Проба: зовётся ли _IntegrateForces у авторитетного тела с Freeze=true и
// CustomIntegrator=true (нужно для шага 5.10: dock-заморозка не должна глушить
// серверную пружину на фазах Latched).
using Godot;

public partial class ProbeFreezeIntegrator : RigidBody3D
{
    private int _calls;

    public override void _Ready()
    {
        Freeze = true;
        FreezeMode = FreezeModeEnum.Static;
        CustomIntegrator = true;
    }

    public override void _IntegrateForces(PhysicsDirectBodyState3D sd)
    {
        _calls++;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Engine.GetPhysicsFrames() >= 30)
        {
            GD.Print($"PROBE integrator calls with Freeze=true: {_calls}");
            GetTree().Quit(_calls > 0 ? 0 : 1);
        }
    }
}
