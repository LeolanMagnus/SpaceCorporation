using System;
using SpaceCorporation.Net;

// Одноразовая отладка кодека поворота (не входит в приёмку R1).
internal static class RotDebug
{
    public static void Run()
    {
        var rng = new Random(20260729);
        var buf = new byte[ShipRecordCodec.PilotRecordSize];
        double worst = 0; int worstIdx = -1; Quat a = Quat.Identity, b = Quat.Identity;
        for (int i = 0; i < 20000; i++)
        {
            float x = (float)(rng.NextDouble() * 2 - 1), y = (float)(rng.NextDouble() * 2 - 1);
            float z = (float)(rng.NextDouble() * 2 - 1), w = (float)(rng.NextDouble() * 2 - 1);
            float len = MathF.Sqrt(x * x + y * y + z * z + w * w);
            if (len < 1e-6f) continue;
            var q = new Quat(x / len, y / len, z / len, w / len);
            var s = new ShipSnapshotQ { NetId = 1, SimTick = 0, Position = Vec3.Zero, Rotation = q };
            int n = ShipRecordCodec.Write(buf, s, Vec3.Zero, 0, Quant.PosStepNear);
            ShipRecordCodec.Read(buf.AsSpan(0, n), out var r, Vec3.Zero, 0, Quant.PosStepNear, false, false);
            double dot = Math.Abs((double)q.X * r.Rotation.X + q.Y * r.Rotation.Y
                                 + q.Z * r.Rotation.Z + q.W * r.Rotation.W);
            double ang = 2.0 * Math.Acos(Math.Min(1.0, dot));
            if (ang > worst) { worst = ang; worstIdx = i; a = q; b = r.Rotation; }
        }
        Console.WriteLine($"worst rot err = {worst * 180 / Math.PI:F3}° at {worstIdx}");
        Console.WriteLine($"  in : ({a.X:F4}, {a.Y:F4}, {a.Z:F4}, {a.W:F4})");
        Console.WriteLine($"  out: ({b.X:F4}, {b.Y:F4}, {b.Z:F4}, {b.W:F4})");
        // decode the 4 rotation bytes (OffRot = 12)
        var s2 = new ShipSnapshotQ { NetId = 1, SimTick = 0, Position = Vec3.Zero, Rotation = a };
        var b2 = new byte[ShipRecordCodec.PilotRecordSize];
        ShipRecordCodec.Write(b2, s2, Vec3.Zero, 0, Quant.PosStepNear);
        uint packed = (uint)(b2[10] | (b2[11] << 8) | (b2[12] << 16) | (b2[13] << 24));
        int drop = (int)((packed >> 30) & 3);
        int c0 = (int)(packed & 0x3FF), c1 = (int)((packed >> 10) & 0x3FF), c2 = (int)((packed >> 20) & 0x3FF);
        Console.WriteLine($"  drop={drop} c0={c0} c1={c1} c2={c2}  (packed={packed:X8})");
    }
}
