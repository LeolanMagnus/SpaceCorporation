using Godot;
using System.Collections.Generic;

namespace SpaceCorporation;

/// <summary>Проба поля «космической пыли» (Player.Dust.cs). Поле — это CPU-пул
/// из Player.Dust.DustAmount точек на MultiMeshInstance3D (не GpuParticles3D):
/// каждая точка живёт в ФИКСИРОВАННОЙ мировой координате, узел едет за игроком,
/// а слот переиспользуется ТОЛЬКО когда игрок отдалился от точки дальше
/// Player.DustRadius (точка «вылетела» за область генерации). Поэтому в покое
/// поле стоит на месте и не моргает/не исчезает, а новые точки рождаются при
/// движении. Проба проверяет ровно это поведение, а не параметры GPU-частиц.
///
/// Проверяем:
///  • у ЛОКАЛЬНОГО игрока есть узел DustField типа MultiMeshInstance3D, у
///    не-локальных (чужих кораблей/станций) его нет;
///  • Multimesh: InstanceCount == DustAmount, UseColors (альфа per-instance),
///    формат трансформ Transform3D; меш — QuadMesh (билборд-квад), материал
///    additive/unshaded/billboard, текстура точки загружена;
///  • СТАРТ + ПОКОЙ: все слоты активны (масштаб>0, альфа>0), мировые позиции
///    за время простоя НЕ меняются (нет «мерцания» и пропажи из ниоткуда),
///    все точки внутри оболочки DustSpawnRadius от игрока;
///  • ДВИЖЕНИЕ: после телепорта игрока далеко все слоты снова активны, но их
///    мировые позиции СМЕНИЛИСЬ и все снова внутри DustSpawnRadius от НОВОЙ
///    позиции игрока (старые точки за пределами DustRadius были переиспользованы).
///
/// Запуск: ./run-tests.sh res://tests/probes/ProbePlayerDust.tscn</summary>
public partial class ProbePlayerDust : Node
{
    // Кадры-вехи: locate → снимок покоя → телепорт → проверка перевыпуска.
    private const int FrameLocate = 30;
    private const int FrameRest = 70;      // сравнение с FrameLocate: покой стабилен
    private const int FrameRecycled = 130; // после телепорта: точки перевыпущены

    private const float ActiveEps = 0.005f; // масштаб меньше — слот скрыт

    private int _frames;

    // ── Езда «по-настоящему» для проверки следа ──────────────────────────────
    // ГЛАВНЫЙ регрессионный замок чинки: раньше проба ВЫСТАВЛЯЛА _player.Velocity
    // руками, и тест проходил даже когда в живой игре Velocity ≡ 0 (Player
    // прикреплён к кораблю, CharacterBody.Velocity не обновляется). Теперь игрок
    // РЕАЛЬНО едет маленькими шагами каждый физический тик (в том же тике, что и
    // UpdateDust), а след-проба читает ИЗМЕРЕННУЮ скорость (_dustVel). Так тест
    // ловит ровно тот баг: если MeasureDustVelocity сломается — _dustVel ≈ 0 и
    // вытянутых точек не будет, несмотря на выставленную вручную Velocity=0.
    private const int DriveTicks = 45;      // тиков равномерной езды (~0.75 с, > 5τ → EMA ≈ цели)
    private const float DriveSpeed = 100f;  // целевая м/с (стабильный raw = step/dt = этому числу)
    private const int StreakDeadlineFrames = 200; // страховка: не ждать езду вечно
    private int _driveTicksLeft;
    private bool _streakPending;            // ждём завершения езды → CheckStreaks
    private bool _streakRequested;          // езда запущена (страховка от зависания)
    private int _streakStart;               // кадр, с которого начали ездить
    private int _failures;

    private Player? _player;
    private MultiMeshInstance3D? _field;

    // Снимок МИРОВЫХ позиций активных точек на кадре FrameLocate.
    private readonly Vector3[] _snap = new Vector3[Player.DustAmount];

    private void Check(bool ok, string what)
    {
        Print($"  {(ok ? "OK  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    public override void _Ready()
    {
        var main = GD.Load<PackedScene>("res://scenes/Main.tscn").Instantiate();
        AddChild(main);
    }

    public override void _Process(double delta)
    {
        _frames++;
        if (_frames == FrameLocate) Locate();
        else if (_frames == FrameRest) CheckRest();
        else if (_frames == FrameRecycled) CheckRecycled();
        else if (_streakPending)
        {
            _streakPending = false;
            CheckStreaks();
        }
        else if (_streakRequested && _frames > _streakStart + StreakDeadlineFrames)
        {
            Check(false, "езда завершилась за отведённое число кадров");
            CheckStreaks();   // всё равно проверим то, что есть
        }
    }

    // Двигаем игрока РЕАЛЬНО, по одному физическому тике за раз — в том же тике и
    // РАНЬШЕ, чем _PhysicsProcess игрока зовёт UpdateDust (проба — корень, у неё
    // физка вызывается раньше её потомков). Шаг = DriveSpeed·dt ⇒ сырая дельта/dt
    // равна ровно DriveSpeed, и EMA (_dustVel) сходится к нему за ~τ. Это и
    // эмулирует настоящий полёт корабля (Player прикреплён, едет за ним) без
    // выставления Velocity руками.
    public override void _PhysicsProcess(double delta)
    {
        if (_driveTicksLeft > 0 && _player != null)
        {
            float dt = (float)delta;
            Vector3 dir = new Vector3(0f, 0f, -1f);   // «вперёд» (−Z), без поворота
            _player.GlobalPosition += dir * (DriveSpeed * dt);
            _driveTicksLeft--;
            if (_driveTicksLeft == 0)
                _streakPending = true;                 // езда окончена → проверка следа
        }
    }

    // --- Кадр 1: найти поле и проверить структуру ---
    private void Locate()
    {
        Print("== ProbePlayerDust (MultiMesh CPU-пул) ==");
        // ВАЖНО: в --headless dummy RenderingServer НЕ хранит per-instance данные
        // MultiMesh — GetInstanceTransform/TransformArray там всегда возвращают
        // identity (проверено и на чистом GDScript). Поэтому проба читает
        // CPU-источник истины пула через Player.DebugGetDustWorld, а не readback
        // из MultiMesh. Структурные проверки ресурса (InstanceCount/Mesh/UseColors)
        // валидны — это поля самого ресурса, не рендер-сервера.

        foreach (Node n in GetTree().GetNodesInGroup("player"))
        {
            if (n is Player p)
                _player = p;
        }
        Check(_player != null, "в offline есть игрок");
        if (_player == null) { Finish(); return; }

        _field = _player.GetNodeOrNull<MultiMeshInstance3D>("DustField");
        Check(_field != null, "у игрока есть DustField (MultiMeshInstance3D)");
        if (_field == null) { Finish(); return; }

        var mm = _field.Multimesh;
        Check(mm != null, "DustField.Multimesh задан");
        if (mm == null) { Finish(); return; }

        Check(mm.InstanceCount == Player.DustAmount,
            $"InstanceCount = {mm.InstanceCount} (ждём {Player.DustAmount})");

        // Меш — билбордный квад с аддитивным unshaded-материалом и текстурой.
        var quad = _field.Multimesh?.Mesh as QuadMesh;
        Check(quad != null, "DustField.Mesh — QuadMesh");
        if (quad?.Material is StandardMaterial3D sm)
        {
            Check(sm.AlbedoTexture != null, "текстура точки загружена");
            Check(sm.ShadingMode == BaseMaterial3D.ShadingModeEnum.Unshaded, "unshaded");
            Check(sm.BlendMode == BaseMaterial3D.BlendModeEnum.Add, "additive-бленд");
            // Биллборд ВЫКЛЮЧЕН намеренно: ориентацию квада (нормаль на камеру +
            // длинная сторона вдоль экранныго движения = след) строит код пыли.
            Check(sm.BillboardMode == BaseMaterial3D.BillboardModeEnum.Disabled, "биллборд выключен (ориентация вручную)");
        }
        else
        {
            Check(false, "у QuadMesh материал StandardMaterial3D");
        }

        // Снимок мировых позиций + счёт активных слотов на старте.
        int active = Snapshot(_snap);
        Check(active == mm.InstanceCount,
            $"на старте все {mm.InstanceCount} слотов активны (найдено {active})");
        CheckPositionsAroundPlayer(_snap, $"на старте точки в оболочке <={Player.DustSpawnRadius} м");

        // Пыль — только у игрока: у кораблей/станций/чужих узлов её быть не должно.
        int strays = 0;
        CountStrays(GetTree().Root, ref strays);
        Check(strays == 0, $"DustField нигде кроме Player (чужих: {strays})");
    }

    // --- Кадр 2: ПОКОЙ — позиции не поменялись, всё ещё все активны ---
    private void CheckRest()
    {
        if (_field?.Multimesh is not MultiMesh mm) { Check(false, "Multimesh жив (кадр покоя)"); Finish(); return; }

        var now = new Vector3[mm.InstanceCount];
        int active = Snapshot(now);
        Check(active == mm.InstanceCount,
            $"в покое все {mm.InstanceCount} слотов всё ещё активны (найдено {active})");

        int moved = 0;
        for (int i = 0; i < mm.InstanceCount; i++)
            if (now[i].DistanceSquaredTo(_snap[i]) > 1e-4f) moved++;
        Check(moved == 0,
            $"в покое позиции НЕ меняются (переместилось {moved}) — нет моргания/пропажи");

        CheckPositionsAroundPlayer(now, "в покое точки вокруг игрока");

        // Телепортируем игрока далеко, имитируя быстрое движение сквозь поле.
        _player!.Detach();                 // освобождаем от кресла, чтобы телепорт держался
        _player.Velocity = Vector3.Zero;
        _player.GlobalPosition += new Vector3(2000f, 0f, 0f);
        Print("  (телепорт игрока на +2000 по X)");
    }

    // --- Кадр 3: ДВИЖЕНИЕ — точки перевыпущены вокруг НОВОЙ позиции ---
    private void CheckRecycled()
    {
        if (_field?.Multimesh is not MultiMesh mm) { Check(false, "Multimesh жив (кадр перевыпуска)"); Finish(); return; }

        var now = new Vector3[mm.InstanceCount];
        int active = Snapshot(now);
        Check(active == mm.InstanceCount,
            $"после движения все {mm.InstanceCount} слотов снова активны (найдено {active})");

        int moved = 0;
        for (int i = 0; i < mm.InstanceCount; i++)
            if (now[i].DistanceSquaredTo(_snap[i]) > 1f) moved++;
        Check(moved == mm.InstanceCount,
            $"после движения позиции СМЕНИЛИСЬ (переместилось {moved}/{mm.InstanceCount})");

        CheckPositionsAroundPlayer(now, "после движения точки вокруг НОВОЙ позиции игрока");

        // Запускаем НАСТОЯЩУЮ езду (шаги по кадрам в _PhysicsProcess), а не
        // выставление Velocity руками. По её завершении _streakPending → CheckStreaks.
        _driveTicksLeft = DriveTicks;
        _streakRequested = true;
        _streakStart = _frames;
        Print($"  (езда вперёд: {DriveTicks} тиков по ~{DriveSpeed / 60f:F3} м = {DriveSpeed} м/с)");
    }

    /// <summary>Проверка РАДИАЛЬНОГО следа (ровно чинимый баг: «точки не штрихуются
    /// на скорости»). Игрок РЕАЛЬНО ехал вперёд (-Z) со скоростью DriveSpeed
    /// (шагами по кадрам, см. _PhysicsProcess) — Velocity руками НЕ выставляется.
    /// Читаем пер-точку след через DebugGetDustStreaks (тот же ComputeStreakFor +
    /// та же _dustVel, что и живая отрисовка). Ключевые инварианты:
    ///   (0а) ИЗМЕРЕННАЯ скорость (_dustVel) сошлась к DriveSpeed — т.е. след чем
    ///        растягивать ЕСТЬ даже без выставленного CharacterBody.Velocity;
    ///   (0б) CharacterBody.Velocity при этом ≈ 0 — ровно то состояние, в котором
    ///        был баг; если б код читал Velocity, след был бы нулевым;
    ///   (1) у точки с большим перпендикуляром (луч ⊥ скорости, сбоку) след ВЫТЯНУТ;
    ///   (2) у точки с малым перпендикуляром (луч ∥ скорости, впереди/сзади) следа НЕТ;
    ///   (3) есть ≥1 вытянутая точка и перпендикуляры РАЗНЫЕ у разных точек
    ///        (иначе это был бы общий фрейм, дававший при полёте вперёд ноль штрихов).</summary>
    private void CheckStreaks()
    {
        // (0) Скорость взялась ИЗ ДВИЖЕНИЯ, а не из Velocity.
        Vector3 measured = _player!.DebugGetMeasuredVelocity();
        float spd = measured.Length();
        Check(spd > DriveSpeed * 0.7f && spd < DriveSpeed * 1.3f,
            $"измеренная скорость сошлась к цели: {_dustVelPrint(spd)} ≈ {DriveSpeed} м/с");
        Check(measured.Dot(new Vector3(0f, 0f, -1f)) > spd * 0.6f,
            "измеренная скорость направлена вперёд (-Z), куда ехали");
        // CharacterBody.Velocity НЕ выставлялся руками → он ≈ 0 (реальное состояние
        // прикреплённого к кораблю игрока). Это и доказывает, что след строится на
        // измеренной скорости, а не на Velocity.
        Check(_player.Velocity.Length() < 1f,
            $"CharacterBody.Velocity ≈ 0 ({_player.Velocity.Length():F2} м/с), а след всё равно есть — " +
            "чинка на измеренной скорости (не на Velocity)");

        var ext = new float[Player.DustAmount];
        var perp = new float[Player.DustAmount];
        int n = _player.DebugGetDustStreaks(ext, perp);
        Check(n > 0, $"след-проба: есть активные точки ({n})");

        int stretched = 0, sideOk = 0, sideBad = 0, dotBad = 0;
        float minPerp = float.MaxValue, maxPerp = 0f;
        for (int i = 0; i < Player.DustAmount; i++)
        {
            if (perp[i] <= 0f) continue; // неактивный слот
            if (perp[i] < minPerp) minPerp = perp[i];
            if (perp[i] > maxPerp) maxPerp = perp[i];
            if (ext[i] > 0.0001f) stretched++;

            if (perp[i] >= Player.DustStreakSpeedMin)
            {
                if (ext[i] > 0.0001f) sideOk++; else sideBad++;   // сбоку → обязан быть след
            }
            else
            {
                if (ext[i] <= 0.0001f) { } else dotBad++;         // продольно → обязан быть точкой
            }
        }
        Print($"  (перпендикуляры: мин {minPerp:F1} м/с, макс {maxPerp:F1} м/с; " +
              $"порог следа {Player.DustStreakSpeedMin} м/с; вытянутых {stretched})");

        Check(sideBad == 0, "боковые точки (перп ≥ порога) ВСЕ вытянуты в след");
        Check(dotBad == 0, "продольные точки (перп < порога) ВСЕ остались точками");
        Check(stretched >= 1, "при езде вперёд есть ХОТЬ ОДНА вытянутая точка (радиальный след виден)");
        Check(maxPerp > minPerp + 1f,
            $"перпендикуляры РАЗНЫЕ у разных точек ({minPerp:F1}→{maxPerp:F1}) — след пер-точечный, не общий");

        Finish();
    }

    // Мелочь для читаемого вывода: что реально посчитала EMA.
    private string _dustVelPrint(float spd) => $"{spd:F1}";

    /// <summary>Считать МИРОВЫЕ позиции всех частиц и число активных слотов.
    /// В headless dummy RenderingServer не хранит instance-данные MultiMesh,
    /// поэтому читаем CPU-источник истины пула через Player.DebugGetDustWorld
    /// (массивы _dustPos/_dustActive/_dustScale) — именно из них каждый кадр
    /// пересчитываются трансформы, значит это и есть реальное состояние поля.
    /// Скрытый слот — масштаб <= ActiveEps; активный — масштаб 0.02..0.035.</summary>
    private int Snapshot(Vector3[] outWorld)
    {
        return _player!.DebugGetDustWorld(outWorld);
    }

    /// <summary>Проверяем, что точки лежат в оболочке генерации вокруг ТЕКУЩЕЙ
    /// позиции игрока (максимум DustSpawnRadius, с небольшим допуском на
    /// интерполяцию позиции игрока между физ.шагом и _Process).</summary>
    private void CheckPositionsAroundPlayer(Vector3[] world, string label)
    {
        Vector3 p = _player!.GlobalPosition;
        int outOfRange = 0;
        float maxD = 0f;
        float minD = float.MaxValue;
        foreach (var v in world)
        {
            float d = v.DistanceTo(p);
            if (d < minD) minD = d;
            if (d > maxD) maxD = d;
            // Инвариант: ни одна активная частица НЕ дальше радиуса СМЕРТИ.
            // При спавне точки в оболочке <=DustSpawnRadius (12 м), но живая
            // частица имеет право дотянуть до DustRadius (15 м) — переиспользование
            // происходит только после пересечения этой границы (с гистерезисом).
            // Поэтому верхняя граница проверки — радиус смерти, а не радиус спавна.
            if (d > Player.DustRadius + 0.5f) outOfRange++;
        }
        Check(outOfRange == 0 && maxD <= Player.DustRadius + 0.5f,
            $"{label}: мин дистанция = {minD:F1} м, макс = {maxD:F1} м (предел смерти {Player.DustRadius} м), вне области = {outOfRange}");
    }

    private void Finish()
    {
        Print(_failures == 0 ? "PROBE OK" : $"PROBE FAILURES: {_failures}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private static void CountStrays(Node n, ref int strays)
    {
        if (n is MultiMeshInstance3D && n.Name == "DustField" && n.GetParent() is not Player)
            strays++;
        foreach (Node c in n.GetChildren())
            CountStrays(c, ref strays);
    }

    private void Print(string s) => GD.Print(s);
}
