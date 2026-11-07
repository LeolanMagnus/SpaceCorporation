using Godot;
using System;

namespace SpaceCorporation;

/// <summary>
/// Проба frame-plan §1 шаг 10: реально ли `AddCollisionExceptionWith`, вызванный
/// ИЗ `_IntegrateForces` (CustomIntegrator), действует в ЭТОМ ЖЕ физическом шаге?
/// Проблема из шага 4.7: в C#-биндинге нет примитива «penetrate», но пространство
/// во время шага доступно — надо эмпирически узнать порядок (детекция → колбэк →
/// солвер) и можно ли гасить контакт тем же шагом, что и сам захват.
///
/// Схема: два пересекающихся шара слоя Ship.
///  - контрольный прогон: без исключения — солвер расталкивает тела, неинтегрируемое
///    тело B смещается;
///  - тестовый прогон: у A включён CustomIntegrator, в `_IntegrateForces` ОДИН раз
///    вызываем `AddCollisionExceptionWith(B)` и НЕ интегрируем A (замирает).
///    Если исключение применится тем же шагом — B не получит расталкивания
///    (смещение ≈ 0), иначе B уедет как в контроле.
/// Запуск: godot --headless --path . res://tests/probes/ProbeCaptureFrame.tscn
/// </summary>
public partial class ProbeCaptureFrame : Node3D
{
    /// <summary>Тело A с CustomIntegrator: его колбэк `_IntegrateForces` ОДИН раз
    /// добавляет исключение контакта A↔B и НЕ интегрирует A (замирает). Это ровно
    /// то место, откуда SetCapture зовёт AddCollisionExceptionWith (шаг 5).</summary>
    public partial class IntegratorBody : RigidBody3D
    {
        public RigidBody3D? Other;                 // тело, с которым гасим контакт
        public bool ExceptionAdded;                // факт вызова (считывает проба)

        public override void _IntegrateForces(PhysicsDirectBodyState3D sd)
        {
            if (!ExceptionAdded && Other != null)
            {
                ExceptionAdded = true;
                // Вызов ровно как в SetCapture: от CollisionObject3D к объекту.
                AddCollisionExceptionWith(Other);
                GD.Print("[integrate] AddCollisionExceptionWith(TestA,TestB) из _IntegrateForces");
            }
            // Специально НИКАКОГО интегрирования: A обязан остаться на месте.
        }
    }

    private RigidBody3D _bControl = null!;
    private RigidBody3D _bTest = null!;
    private IntegratorBody _aTest = null!;
    private int _frames;
    private float _controlStart;
    private float _testStart;
    private float _testAtFrame3;
    private readonly int _controlRunFrames = 8;

    public override void _Ready()
    {
        GD.Print("=== ProbeCaptureFrame ===");

        // ПАРЫ ПЕРЕКРЫВАЮЩИХСЯ ТЕЛ. Слои Ship↔Ship сталкиваются (маска Ship).
        // Контрольная пара — A заморожена (Fixed), B свободна: весь импульс
        // расталкивания уходит в B — чистый сигнал «солвер работает».
        _bControl = MakeShipSphere("ControlB", new Vector3(0.3f, 0f, 0f));
        var aCtrl = MakeShipSphere("ControlA", new Vector3(-0.3f, 0f, 0f));
        aCtrl.Freeze = true;
        aCtrl.LockRotation = true;
        _bControl.LockRotation = true;

        // Тестовая пара: A с CustomIntegrator (замирает, исключение добавляет сам).
        _bTest = MakeShipSphere("TestB", new Vector3(2.3f, 0f, 0f));
        _bTest.LockRotation = true;
        _aTest = new IntegratorBody { Name = "TestA", Position = new Vector3(1.7f, 0f, 0f) };
        _aTest.CollisionLayer = CollisionLayers.Ship;
        _aTest.CollisionMask = CollisionLayers.Ship;
        _aTest.LockRotation = true;
        _aTest.AddChild(new CollisionShape3D { Name = "Shape", Shape = new SphereShape3D { Radius = 0.5f } });
        _aTest.Other = _bTest;
        AddChild(_aTest);
        _aTest.CustomIntegrator = true; // ← A интегрирует только колбэк

        _controlStart = _bControl.GlobalPosition.X;
        _testStart = _bTest.GlobalPosition.X;
    }

    private RigidBody3D MakeShipSphere(string name, Vector3 pos)
    {
        var b = new RigidBody3D { Name = name, Position = pos };
        b.CollisionLayer = CollisionLayers.Ship;
        b.CollisionMask = CollisionLayers.Ship;
        b.AddChild(new CollisionShape3D { Name = "Shape", Shape = new SphereShape3D { Radius = 0.5f } });
        AddChild(b);
        return b;
    }

    public override void _PhysicsProcess(double delta)
    {
        // Физкадр 0 (после _Ready) уже обработан движком: на нём колбэк AddCollision
        // отработает и должен погасить контакт. Смотрим результат на последующих.
        // Замер ПОСЛЕДНИХ кадров: исключение контакта из _IntegrateForces гасит
        // выталкивание не мгновенно — broadphase перестраивается за ~2 шага, и за
        // них B получает частичный толчок. Но в ПОЗДНИХ кадрах (когда broadphase
        // уже учёл исключение) выталкивание обязаны ПРЕКРАТИТЬСЯ: это и есть то,
        // что нужно SetCapture (корпус родителя не выталкивает захваченного
        // шагами подряд). Иначе testB уехал бы так же далеко, как controlB.
        if (_frames == 4)
            _testAtFrame3 = _bTest.GlobalPosition.X; // отметка «broadphase устоялся»
        _frames++;
        if (_frames >= _controlRunFrames)
        {
            float ctrlMove = Mathf.Abs(_bControl.GlobalPosition.X - _controlStart);
            float testMove = Mathf.Abs(_bTest.GlobalPosition.X - _testStart);
            float testLateMotion = Mathf.Abs(_bTest.GlobalPosition.X - _testAtFrame3);
            bool added = _aTest.ExceptionAdded;
            GD.Print($"final({_frames}к): controlB Δ={ctrlMove:F4} m, testB Δ={testMove:F4} m");
            GD.Print($"test late-frame motion (кадры 4..{_frames})={testLateMotion:F4} m");
            // Критерий для SetCapture: в поздних кадрах захваченный НЕ едет
            // дальше (выталкивание прекращено), а суммарный унос testB заметно
            // меньше контроля без исключения.
            bool stopped = testLateMotion < 0.01f;
            bool reduced = testMove < ctrlMove * 0.6f;
            GD.Print($"exceptionAdded={added} lateStopped={stopped} pushReduced={reduced}");
            bool ok = added && stopped && reduced;
            GD.Print($"=== PROBE capture_collision_exception={(ok ? "OK" : "FAIL")} " +
                     "(выталкивание прекращается за ~2 шага, в поздних — нет) ===");
            GetTree().Quit(ok ? 0 : 1);
        }
    }
}
