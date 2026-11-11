using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// Автотест Блока B (eva-plan Б1–Б3): чистые модели скафандра — без узлов, без
/// физики, без времени Godot (dt подаётся руками). Карта проверок = пункты 1, 8,
/// 11 из Блока G в укороченном (пока без Player) виде: их нельзя проверить после
/// Блока C, они и сейчас полностью определяются структурами.
///
/// Запуск: ./run-tests.sh res://tests/SuitModelTest.tscn
/// </summary>
public partial class SuitModelTest : Node
{
    private int _pass, _fail;
    private readonly System.Collections.Generic.List<string> _failures = new();

    private const float Eps = 1e-4f;

    public override void _Ready()
    {
        GD.Print("=== SuitModelTest: чистые модели EVA (Блок B) ===");
        RunFuelTests();
        RunBatteryTests();
        RunAttachRechargeTests();
        RunWarningTests();
        RunLoadoutTests();
        RunStepDrainSequenceTests();
        Finish();
    }

    // ==================================================================
    // П.1 — расход реактивной массы по ТЗ: 1 %/с линейная, 0.2 %/с вращение
    // ==================================================================
    private void RunFuelTests()
    {
        GD.Print("[B] п.1: BurnFuel — 1 %/с тяга, 0.2 %/с доворот, 0 в покое");

        SuitState s = SuitState.Fresh();
        Check(s.Fuel == SuitModel.MassFuelTotal && s.Battery == SuitModel.BatteryTotal && s.HeatOn,
            $"п.1: Fresh = масса {s.Fuel} % / батарея {s.Battery} % / обогрев вкл");

        // 1 с линейной тяги (доля осей = 1) ⇒ ровно 1.0 %.
        float b1 = SuitModel.BurnFuel(s, 1f, 0f, SuitModel.SuitMaxAngularSpeed, 1f);
        Check(Mathf.Abs(b1 - 1.0f) < Eps, $"п.1: 1 с тяги => {b1:F6} % (ждём 1.0)");

        // 1 с доворота на ПРЕДЕЛЕ (rotUsedRad = maxAng*dt) ⇒ ровно 0.2 %.
        float b2 = SuitModel.BurnFuel(s, 0f, SuitModel.SuitMaxAngularSpeed * 1f,
            SuitModel.SuitMaxAngularSpeed, 1f);
        Check(Mathf.Abs(b2 - 0.2f) < Eps, $"п.1: 1 с доворота на пределе => {b2:F6} % (ждём 0.2)");

        // Покой: ни тяги, ни доворота ⇒ 0 (вход — факт движения, а не намерение).
        float b3 = SuitModel.BurnFuel(s, 0f, 0f, SuitModel.SuitMaxAngularSpeed, 1f);
        Check(b3 == 0f, $"п.1: покой => {b3} % (ждём 0)");

        // Половина предела ⇒ ровно половина ставки.
        float b4 = SuitModel.BurnFuel(s, 0f, 0.5f * SuitModel.SuitMaxAngularSpeed,
            SuitModel.SuitMaxAngularSpeed, 1f);
        Check(Mathf.Abs(b4 - 0.1f) < Eps, $"п.1: доворот на ½ предела => {b4:F6} % (ждём 0.1)");

        // Оба канала сразу складываются: 1 с (тяга + доворот на пределе) = 1.2 %.
        float b5 = SuitModel.BurnFuel(s, 1f, SuitModel.SuitMaxAngularSpeed,
            SuitModel.SuitMaxAngularSpeed, 1f);
        Check(Mathf.Abs(b5 - 1.2f) < Eps, $"п.1: тяга+доворот => {b5:F6} % (ждём 1.2)");

        // Дробный шаг: 0.1 с тяги = 0.1 %; расход линейно делится на подшаги.
        SuitState a = SuitState.Fresh();
        SuitState c = SuitState.Fresh();
        SuitModel.StepDrain(ref a, 1f, 0f, SuitModel.SuitMaxAngularSpeed, 1f);
        for (int i = 0; i < 10; i++)
            SuitModel.StepDrain(ref c, 1f, 0f, SuitModel.SuitMaxAngularSpeed, 0.1f);
        Check(Mathf.Abs(a.Fuel - c.Fuel) < 1e-3f,
            $"п.1: 1×1 с и 10×0.1 с дают одно и то же ({a.Fuel:F5} == {c.Fuel:F5})");

        // Перелёт за предел (численный) не даёт лишнего расхода: кламп доли <= 1.
        float b6 = SuitModel.BurnFuel(s, 1f, 100f * SuitModel.SuitMaxAngularSpeed,
            SuitModel.SuitMaxAngularSpeed, 1f);
        Check(Mathf.Abs(b6 - 1.2f) < Eps, $"п.1: сверх-доворот клампится в ставку => {b6:F6} % (ждём 1.2)");

        // Пустой бак молчит (в Player это гейт «тяга не проходит», п.1 п.1 Stage7).
        SuitState dry = SuitState.Fresh();
        dry.Fuel = 0f;
        Check(SuitModel.BurnFuel(dry, 1f, SuitModel.SuitMaxAngularSpeed,
            SuitModel.SuitMaxAngularSpeed, 1f) == 0f,
            "п.1: Fuel == 0 => расход 0 (джетпак пуст, молчит)");

        // Ostatok меньше ставки: сжигаем ровно остаток, в минус не уходим.
        SuitState low = SuitState.Fresh();
        low.Fuel = 0.05f;
        SuitModel.StepDrain(ref low, 1f, 0f, SuitModel.SuitMaxAngularSpeed, 5f);
        Check(low.Fuel == 0f, $"п.1: ставка больше остатка => Fuel = {low.Fuel} (не отрицательно)");

        // dt <= 0 и maxAng <= 0 — защиты от деления, расход 0.
        Check(SuitModel.BurnFuel(s, 1f, 1f, SuitModel.SuitMaxAngularSpeed, 0f) == 0f &&
              SuitModel.BurnFuel(s, 1f, 1f, 0f, 1f) == 0f,
              "п.1: dt == 0 или maxAng == 0 => расход 0 (нет деления на ноль)");
    }

    // ==================================================================
    // П.1 (продолжение) — батарея: 4 %/мин обогрев, +фонарь, гашение при 0 %
    // ==================================================================
    private void RunBatteryTests()
    {
        GD.Print("[B] п.1: BurnBattery — 4 %/мин обогрев, свет, гашение при нуле");

        SuitState s = SuitState.Fresh();
        float heatOnly = SuitModel.BurnBattery(s, 60f);
        Check(Mathf.Abs(heatOnly - SuitModel.HeatDrawPerMin) < Eps,
            $"п.1: 1 мин обогрева => {heatOnly:F6} % (ждём {SuitModel.HeatDrawPerMin})");

        s.LightOn = true;
        float withLight = SuitModel.BurnBattery(s, 60f);
        Check(Mathf.Abs(withLight - (SuitModel.HeatDrawPerMin + SuitModel.LightDrawPerMin)) < Eps,
            $"п.1: 1 мин обогрева+света => {withLight:F4} % " +
            $"(ждём {SuitModel.HeatDrawPerMin + SuitModel.LightDrawPerMin})");

        // Свет в 10 раз прожорливее обогрева поour константе (12 против 4):
        // за минуту разница ровно на LightDrawPerMin.
        Check(Mathf.Abs(withLight - heatOnly - SuitModel.LightDrawPerMin) < Eps,
            $"п.1: свет добавляет ровно {SuitModel.LightDrawPerMin} %/мин");

        // Обогрев — «постоянно»: горит и в Seat (флаг В22), т.е. BurnBattery не
        // зависит от режима/тяги: тот же результат при нулевой тяге.
        SuitState seat = SuitState.Fresh();
        Check(Mathf.Abs(SuitModel.BurnBattery(seat, 30f) - SuitModel.HeatDrawPerMin / 2f) < Eps,
            "п.1: обогрев идёт и без тяги/режима (постоянный расход)");

        // Батарея кончилась ⇒ свет и обогрев погашены (ТЗ-оговорка плана), джетпак жив.
        SuitState flat = SuitState.Fresh();
        flat.Battery = 0.1f;
        flat.LightOn = true;
        flat.BootsOn = true;
        flat.Fuel = 42f;
        SuitModel.StepDrain(ref flat, 1f, 0f, SuitModel.SuitMaxAngularSpeed, 0.5f);
        Check(flat.Battery == 0f, $"п.1: батарея обнулилась ({flat.Battery})");
        Check(!flat.LightOn && !flat.HeatOn,
            "п.1: 0 % батареи => LightOn/HeatOn выключены");
        Check(flat.BootsOn && flat.Fuel > 0f,
            $"п.1: ботинки и реактивная масса НЕ связаны с батареей " +
            $"(ТЗ не связывает): Fuel = {flat.Fuel:F3}");

        // Расход батареи не уходит в минус и не превышает остаток.
        SuitState nearly = SuitState.Fresh();
        nearly.Battery = 1f;
        nearly.HeatOn = true;
        SuitModel.StepDrain(ref nearly, 0f, 0f, SuitModel.SuitMaxAngularSpeed, 3600f);
        Check(nearly.Battery == 0f && nearly.Battery >= 0f,
            $"п.1: часовая симуляция с малым зарядом => Battery = {nearly.Battery}");
    }

    // ==================================================================
    // П.8 — заправка: корабль заправляет, станция/палуба — нет
    // ==================================================================
    private void RunAttachRechargeTests()
    {
        GD.Print("[B] п.8: ApplyShipAttach / StepRecharge — только корабль");

        SuitState s = SuitState.Fresh();
        s.Fuel = 30f;
        s.Battery = 50f;
        SuitModel.ApplyShipAttach(ref s);
        Check(s.Fuel == SuitModel.MassFuelTotal,
            $"п.8: посадка в кресло корабля => масса {s.Fuel} % (мгновенно, ТЗ)");
        Check(s.Battery == 50f, $"п.8: ApplyShipAttach батарею не льёт ({s.Battery}) — она заряжается темпом");

        // Зарядка батареи: 20 %/мин ⇒ за 3 мин = +60 % (50 → 100, кламп по потолку).
        SuitModel.StepRecharge(ref s, attachedToShip: true, dt: 180f);
        Check(Mathf.Abs(s.Battery - SuitModel.BatteryTotal) < Eps,
            $"п.8: 3 мин у корабля => батарея {s.Battery:F3} % (полная, кламп)");

        // Станция/свобода не заправляют и не заряжают (ТЗ: заправка — от корабля).
        SuitState station = SuitState.Fresh();
        station.Fuel = 30f;
        station.Battery = 30f;
        SuitModel.StepRecharge(ref station, attachedToShip: false, dt: 600f);
        Check(station.Fuel == 30f && station.Battery == 30f,
            "п.8: крепление НЕ к кораблю (станция/палуба) => ни массы, ни зарядки");

        // Восстановление батареи возвращает обогрев (ТЗ «постоянно»), но не свет.
        SuitState dead = SuitState.Fresh();
        dead.Battery = 0f;
        dead.HeatOn = false;
        dead.LightOn = false;
        SuitModel.StepRecharge(ref dead, attachedToShip: true, dt: 60f);
        Check(dead.HeatOn, "п.8: батарея появилась => обогрев снова включён");
        Check(!dead.LightOn, "п.8: свет остаётся выключенным (фонарь — решение игрока)");
        Check(Mathf.Abs(dead.Battery - SuitModel.RechargePerMin) < Eps,
            $"п.8: 1 мин у корабля => +{dead.Battery:F3} % (ждём {SuitModel.RechargePerMin})");

        // Одновременный drain+recharge (пилот у корабля с включённым светом):
        // 4+12 против 20 ⇒ чистый прирост 4 %/мин.
        SuitState both = SuitState.Fresh();
        both.Battery = 50f;
        both.LightOn = true;
        SuitModel.StepDrain(ref both, 0f, 0f, SuitModel.SuitMaxAngularSpeed, 60f);
        SuitModel.StepRecharge(ref both, attachedToShip: true, dt: 60f);
        Check(Mathf.Abs(both.Battery - 54f) < 1e-3f,
            $"п.8: свет у корабля за 1 мин: 50 -16 +20 = {both.Battery:F3} % (ждём 54)");
    }

    // ==================================================================
    // П.11 — гистерезис HUD-предупреждений
    // ==================================================================
    private void RunWarningTests()
    {
        GD.Print("[B] п.11: гистерезис предупреждений (10 % / 12 %)");

        SuitState s = SuitState.Fresh();
        Check(!s.LowFuelWarn && !s.LowBatteryWarn, "п.11: полный скафандр — предупреждений нет");

        s.Fuel = 10f;
        SuitModel.UpdateWarnings(ref s);
        Check(s.LowFuelWarn, $"п.11: Fuel = 10 => включилось");

        s.Fuel = 9f;
        SuitModel.UpdateWarnings(ref s);
        Check(s.LowFuelWarn, "п.11: 9 % — держится");

        s.Fuel = 12f;
        SuitModel.UpdateWarnings(ref s);
        Check(s.LowFuelWarn, $"п.11: 12 % — ещё держится (гистерезис, не мигает)");

        s.Fuel = 16f;
        SuitModel.UpdateWarnings(ref s);
        Check(!s.LowFuelWarn, "п.11: 16 % => выключилось");

        s.Fuel = 11f;
        SuitModel.UpdateWarnings(ref s);
        Check(!s.LowFuelWarn, "п.11: 11 % — снова не включается (высокий порог повтора)");

        s.Fuel = 10f;
        SuitModel.UpdateWarnings(ref s);
        Check(s.LowFuelWarn, "п.11: 10 % => снова включилось");

        // Батарея — тот же механизм независимо от массы.
        s.Battery = 9f;
        SuitModel.UpdateWarnings(ref s);
        Check(s.LowBatteryWarn && s.LowFuelWarn,
            "п.11: LowBatteryWarn независим от LowFuelWarn");
        s.Battery = 13f;
        SuitModel.UpdateWarnings(ref s);
        Check(!s.LowBatteryWarn, "п.11: батарея above 12 % => предупреждение снято");

        // StepDrain сам обновляет флаги (единая точка изменения состояния).
        SuitState auto = SuitState.Fresh();
        auto.Fuel = 10.5f;
        for (int i = 0; i < 6; i++)
            SuitModel.StepDrain(ref auto, 1f, 0f, SuitModel.SuitMaxAngularSpeed, 0.1f);
        Check(auto.LowFuelWarn,
            $"п.11: StepDrain обновил флаг сам (Fuel = {auto.Fuel:F3} => warn = {auto.LowFuelWarn})");
    }

    // ==================================================================
    // П.Б3 — задел модификаций: SuitLoadout + SuitModuleCatalog
    // ==================================================================
    private void RunLoadoutTests()
    {
        GD.Print("[B] п.Б3: SuitLoadout / SuitModuleCatalog — структура без цен");

        SuitLoadout l = new();
        Check(l.Get(SuitModuleKind.Heat) == "heat_standard" &&
              l.Get(SuitModuleKind.Light) == "light_standard" &&
              l.Get(SuitModuleKind.Jetpack) == "jetpack_mk1" &&
              l.Get(SuitModuleKind.Boots) == "boots_mk1",
            "п.Б3: базовая комплектация заполнена по умолчанию");

        Check(l.MassTotal() == SuitModel.MassFuelTotal,
            $"п.Б3: масса базового джетпака = {l.MassTotal()} % = константе SuitModel");
        Check(l.PowerDrawDeltaPerMin() == 0f,
            "п.Б3: базовая комплектация не меняет расход (надбавка 0) — сегодняшние числа SuitModel不变");

        SuitState st = l.Build();
        Check(st.Fuel == SuitModel.MassFuelTotal && st.Battery == SuitModel.BatteryTotal && st.HeatOn,
            "п.Б3: Build() => полный скафандр, обогрев включён");
        Check(SuitModules.Has(st.ActiveSuitModules, SuitModuleKind.Jetpack) &&
              SuitModules.Has(st.ActiveSuitModules, SuitModuleKind.Heat) &&
              SuitModules.CountActive(st.ActiveSuitModules) == SuitModules.Count,
            $"п.Б3: маска активных модулей = {st.ActiveSuitModules} (все 4 слота заняты)");

        // Слот принимает только свой вид (как ShipLoadout.EquipKind).
        Check(!l.EquipKind(SuitModuleKind.Jetpack, "light_flood_mk2"),
            "п.Б3: модуль света не лезет в слот джетпака");
        Check(!l.EquipKind(SuitModuleKind.Heat, "no_such_module"),
            "п.Б3: неизвестный id => отказ");
        Check(l.EquipKind(SuitModuleKind.Heat, "heat_insulated_mk2"),
            "п.Б3: апгрейд обогрева принимается");
        Check(l.PowerDrawPerMinWithBase() == SuitModel.HeatDrawPerMin - 1f,
            $"п.Б3: Mk II обогрев => расход {l.PowerDrawPerMinWithBase()} %/мин (ждём 3)");
        Check(l.EquipKind(SuitModuleKind.Heat, "heat_standard"),
            "п.Б3: откат на стандартный обогрев");

        // Пустой слот джетпака = нет реактивной массы (сумма по слотам).
        Check(l.EquipKind(SuitModuleKind.Jetpack, null) && l.MassTotal() == 0f,
            "п.Б3: слот джетпака пуст => MassTotal = 0 (массу даёт только джетпак)");
        SuitState dry = l.Build();
        Check(dry.Fuel == 0f && dry.LowFuelWarn,
            "п.Б3: Build() без бака => Fuel = 0 и предупреждение сразу");
        Check(SuitModel.BurnFuel(dry, 1f, 0f, SuitModel.SuitMaxAngularSpeed, 1f) == 0f,
            "п.Б3: пустой комплект не расходует массу (нет источника)");

        // Маска — для сети: съём модуля снимает и бит.
        Check(!SuitModules.Has(l.Build().ActiveSuitModules, SuitModuleKind.Jetpack),
            "п.Б3: снятый модуль исчез из маски (сеть не соврёт о джетпаке)");

        // Equip() сам кладёт модуль в слот своего вида.
        SuitLoadout auto = new();
        Check(auto.Equip("light_flood_mk2") &&
              auto.Get(SuitModuleKind.Light) == "light_flood_mk2" &&
              Mathf.Abs(auto.PowerDrawDeltaPerMin() - SuitModuleCatalog.Find("light_flood_mk2").DrawPerMin) < Eps,
            "п.Б3: Equip() раскладывает по виду слота и учитывает надбавку расхода");
        Check(!SuitModuleCatalog.TryFind("", out _), "п.Б3: пустой id каталогу неизвестен");
    }

    // ==================================================================
    // Сквозная последовательность (порядок «полёт → сесть → улететь»)
    // ==================================================================
    private void RunStepDrainSequenceTests()
    {
        GD.Print("[B] п.сквозь: последовательность шагов симуляции");

        SuitState s = new SuitLoadout().Build();
        // 60 с полёта с непрерывной тягой и доворотом на пределе: 1.2 %/с ⇒ 72 %.
        for (int i = 0; i < 600; i++)
            SuitModel.StepDrain(ref s, 1f, SuitModel.SuitMaxAngularSpeed,
                SuitModel.SuitMaxAngularSpeed, 0.1f);
        Check(Mathf.Abs(s.Fuel - 28f) < 1e-2f,
            $"п.сквозь: 60 с манёвра => масса {s.Fuel:F3} % (ждём 28)");
        // 60 с при постоянном обогреве: 4 %/мин ⇒ 4 %.
        Check(Mathf.Abs(s.Battery - 96f) < 1e-2f,
            $"п.сквозь: 60 с => батарея {s.Battery:F3} % (ждём 96)");
        Check(!s.LowFuelWarn, "п.сквозь: 28 % — предупреждения ещё нет");

        // Посадка в кресло: масса мгновенно полная, батарея дальше заряжается.
        SuitModel.ApplyShipAttach(ref s);
        Check(s.Fuel == SuitModel.MassFuelTotal && !s.LowFuelWarn,
            "п.сквозь: посадка => масса восстановлена, предупреждение снято");
        SuitModel.StepRecharge(ref s, attachedToShip: true, dt: 60f * 12f);
        Check(s.Battery == SuitModel.BatteryTotal,
            $"п.сквозь: 12 мин у корабля => батарея {s.Battery} %");

        // Зарядка в кресле не должна «съедать» массу: кресло её не расходует.
        Check(s.Fuel == SuitModel.MassFuelTotal, "п.сквозь: Seat не тратит реактивную массу");
    }

    // ==================================================================
    // Служебное
    // ==================================================================
    private void Check(bool condition, string label)
    {
        if (condition) { _pass++; GD.Print($"  [PASS] {label}"); }
        else { _fail++; _failures.Add(label); GD.PrintErr($"  [FAIL] {label}"); }
    }

    private void Finish()
    {
        GD.Print($"=== SuitModelTest: {_pass} PASS / {_fail} FAIL ===");
        foreach (string f in _failures) GD.PrintErr($"  провал: {f}");
        GetTree().Quit(_fail == 0 ? 0 : 1);
    }
}
