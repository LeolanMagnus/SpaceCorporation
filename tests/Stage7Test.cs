using Godot;

namespace SpaceCorporation.Tests;

/// <summary>
/// ТЕСТ ЭТАПА 7 (EVA) — eva-plan Блок C + Блок G (чек-лист §G п.1–12 без
/// сетевой части п.12 = Блок D). Драйвер фаз; тела проверок — в
/// Stage7Test.Checks.cs, стенд мира и снимки — в Stage7Test.Helpers.cs.
/// Запуск: ./run-tests.sh res://tests/Stage7Test.tscn
///
/// Порядок фаз = порядок блока C в плане (кресло → джетпак → ботинки):
///  P0  — клавиши InputMap (C4: detach/toggle_boots/suit_light, п.7 литеры);
///  P1  — расходники, которыми пользуется EVA (п.1/п.8/п.18; чистые функции
///        подробно покрыты SuitModelTest, здесь — инварианты EVА-путей);
///  P2  — инвариант п.3: EVA и корабль = ОДИН формат кадра (бит ThrustF,
///        одинаковый отклик на одинаковый кадр);
///  P3  — C3 кресло: посадка/выход (п.7), езда вместе с кораблём (п.10),
///        заправка скафандра при посадке (п.8), потолки джетпака (п.2);
///  P4  — C1 джетпак: тяга совпадает с кораблём, 6DoF-маска, расход по факту;
///  P5  — C2 ботинки: прилипание к палубе, перенос с moving-палубой, ходьба
///        в плоскости нормали, нормаль следит за креном палубы (п.9/п.10);
///  P6  — переходы клавиш (п.13–15 плана §8: F/G/N, отказ ботинок без
///        поверхности и в кресле);
///  P7  — SuitState на всех трёх режимах (расход/зарядка/гаснение при 0);
///  P8  — инвариант п.17: иерархию меняет только Attach/Detach (ботинки НЕ
///        дочерят игрока к палубе).
///
/// Тест НЕ эмулирует ОС-ввод (в headless его сгенерировать нельзя — уроки
/// этапа 2): логика меряется на швах `EvaTest*` — тех же путях, что идут от
/// реальных клавиш (`_Input` зовёт те же методы).
/// </summary>
public partial class Stage7Test : Node
{
    private enum Phase
    {
        Build, Key0, Keys,
        Suit0, Suit1, Suit2, Suit3, Suit4,
        Frame0, Frame1, Frame2, Frame3,
        Seat0, Seat1, Seat2, Seat3, Seat4, Seat5,
        Jet0, Jet1, Jet2, Jet3, Jet4,
        Boot0, Boot1, Boot2, Boot3, Boot4, Boot5, Boot6,
        Sw0, Sw1, Sw2, Sw3, Sw4, Sw5, Sw6,
        Suit2m0, Suit2m1, Suit2m2, Suit2m3,
        Hier0, Hier1, Hier2,
        Slope0,
        Hover0, Hover1, Hover2, Hover3, SuitHud0,
            NetSeat0, NetSeat1, NetSeat2,
            EvaNet0,
            Done
    }
    private Phase _phase = Phase.Build;
    private int _frame;
    private int _tickBudget;

    // Сквозные значения между шагами одной фазы (локальные переменные фаз не
    // переживают switch — поля проще, чем корутинины старого файла).
    private Ship _ship2 = null!;
    private AttachPoint _chair = null!;
    private SuitState _suitBefore;
    private Vector3 _p0, _p1, _savedPos;
    private float _fuel0, _battery0;
    private bool _wasChild;

    public override void _Ready()
    {
        GD.Print("=== STAGE 7 TEST (EVA / eva-plan Block C+G) ===");
    }

    public override void _PhysicsProcess(double delta)
    {
        _frame++;
        switch (_phase)
        {
            // ── мир и клавиши ────────────────────────────────────────────────
            case Phase.Build:
                BuildEvaWorld();
                Next(Phase.Key0);
                break;
            case Phase.Key0:
                CheckKeysInputMap();          // п.7/п.11(C4)
                Next(Phase.Suit0);
                break;

            // ── п.1: расходники в EVA-путях ─────────────────────────────────
            case Phase.Suit0:
                Suit0_Begin();
                Next(Phase.Suit1);
                break;
            case Phase.Suit1:
                Suit1_AttachRefuel();         // п.8: посадка заправляет мгновенно
                Next(Phase.Suit2);
                break;
            case Phase.Suit2:
                Suit2_WarnHysteresis();       // п.18: 10 включился / 12 не выкл / 16 выкл
                Next(Phase.Suit3);
                break;
            case Phase.Suit3:
                Suit3_BatteryZero();          // батарея 0 ⇒ свет/обогрев погашены
                Next(Phase.Suit4);
                break;
            case Phase.Suit4:
                Suit4_DeckNoRefuel();         // п.8: палуба/станция НЕ заправляют
                Next(Phase.Frame0);
                break;

            // ── п.3: кадр EVA == кадр корабля (формат) ──────────────────────
            case Phase.Frame0:
                Frame0_Begin();
                Next(Phase.Frame1);
                break;
            case Phase.Frame1:
                Frame1_SameBit();
                Next(Phase.Frame2);
                break;
            case Phase.Frame2:
                Frame2_SameDeltaV();
                Next(Phase.Frame3);
                break;
            case Phase.Frame3:
                Frame3_End();
                Next(Phase.Seat0);
                break;

            // ── п.7/п.10: кресло ────────────────────────────────────────────
            case Phase.Seat0:
                Seat0_Begin();
                Next(Phase.Seat1);
                break;
            case Phase.Seat1:
                Seat1_EnterExit();            // вход/выход через кресло под курсором
                Next(Phase.Seat2);
                break;
            case Phase.Seat2:
                Seat2_MovingShip();
                Next(Phase.Seat3);
                break;
            case Phase.Seat3:
                Seat3_MovingShip2();          // проверка «уехал вместе, без разгона»
                Next(Phase.Seat4);
                break;
            case Phase.Seat4:
                Seat4_Occupied();             // п.6/п.7: занятое кресло — отказ
                Next(Phase.Seat5);
                break;
            case Phase.Seat5:
                Seat5_End();
                Next(Phase.Jet0);
                break;

            // ── п.2/п.11: джетпак ───────────────────────────────────────────
            case Phase.Jet0:
                Jet0_Begin();
                Next(Phase.Jet1);
                break;
            case Phase.Jet1:
                Jet1_SpeedCap();              // 3 с тяги ⇒ |Vel| <= 10 (п.2)
                Next(Phase.Jet2);
                break;
            case Phase.Jet2:
                Jet2_SpeedCap2();             // снятие замера
                Next(Phase.Jet3);
                break;
            case Phase.Jet3:
                Jet3_SixDoF();                // маска + оси того же формата
                Next(Phase.Jet4);
                break;
            case Phase.Jet4:
                Jet4_FuelDrain();             // расход по факту движения
                Next(Phase.Boot0);
                break;

            // ── п.9/п.10: ботинки ───────────────────────────────────────────
            case Phase.Boot0:
                Boot0_Begin();
                Next(Phase.Boot1);
                break;
            case Phase.Boot1:
                Boot1_Stick();                // прилип: UpDirection == n, Basis.Y ~ n
                Next(Phase.Boot2);
                break;
            case Phase.Boot2:
                Boot2_RideDeck();
                Next(Phase.Boot3);
                break;
            case Phase.Boot3:
                Boot3_RideDeck2();            // палуба уехала — игрок уехал следом
                Next(Phase.Boot4);
                break;
            case Phase.Boot4:
                Boot4_Walk();
                Next(Phase.Boot5);
                break;
            case Phase.Boot5:
                Boot5_Walk2();                // ходьба в плоскости нормали
                Next(Phase.Boot6);
                break;
            case Phase.Boot6:
                Boot6_TiltedDeck();           // крен палубы ⇒ нормаль следует за телом
                Next(Phase.Sw0);
                break;

            // ── п.13–15: переходы клавиш ────────────────────────────────────
            case Phase.Sw0:
                Sw0_Begin();
                Next(Phase.Sw1);
                break;
            case Phase.Sw1:
                Sw1_SeatToJetpack();          // F из кресла = отстёжка (кейс 16)
                Next(Phase.Sw2);
                break;
            case Phase.Sw2:
                Sw2_JetpackToBoots();         // G на палубе ⇒ boots
                Next(Phase.Sw3);
                break;
            case Phase.Sw3:
                Sw3_BootsToJetpack();         // G повторно ⇒ jetpack
                Next(Phase.Sw4);
                break;
            case Phase.Sw4:
                Sw4_BootsNoSurface();         // G без поверхности ⇒ отказ + текст
                Next(Phase.Sw5);
                break;
            case Phase.Sw5:
                Sw5_Light();                  // N: фонарь туда-обратно + расход
                Next(Phase.Sw6);
                break;
            case Phase.Sw6:
                Sw6_BootsToSeat();            // п.14: ботинки ⇒ F ⇒ кресло (разрешено)
                Next(Phase.Suit2m0);
                break;

            // ──SuitState на трёх режимах ────────────────────────────────────
            case Phase.Suit2m0:
                Suit2m_JetpackDrain();        // джетпак: обогрев точится всегда
                Next(Phase.Suit2m1);
                break;
            case Phase.Suit2m1:
                Suit2m_BootsDrain();          // ботинки: тот же расход
                Next(Phase.Suit2m2);
                break;
            case Phase.Suit2m2:
                Suit2m_SeatRecharge();        // кресло: батарея растёт, джетпак-масса ок
                Next(Phase.Suit2m3);
                break;
            case Phase.Suit2m3:
                Suit2m_End();
                Next(Phase.Hier0);
                break;

            // ── п.17: иерархия ──────────────────────────────────────────────
            case Phase.Hier0:
                Hier0_Jetpack();              // джетпак: отец — world, не корабль
                Next(Phase.Hier1);
                break;
            case Phase.Hier1:
                Hier1_Boots();                // ботинки: отец — world (не палуба!)
                Next(Phase.Hier2);
                break;
            case Phase.Hier2:
                Hier2_Seat();                 // кресло: отец — кресло (Reparent)
                Next(Phase.Slope0);
                break;
            case Phase.Slope0:
                Slope0_Gate();                // п.24: SuitModel.CanStepOn 59/60/61°
                Next(Phase.Hover0);
                break;
            case Phase.Hover0:
                Hover0_FreePanel();           // Блок E п.4: луч находит панель, хит есть
                Next(Phase.Hover1);
                break;
            case Phase.Hover1:
                Hover1_Occluded();            // п.4: палуба перекрывает ⇒ хита нет
                Next(Phase.Hover2);
                break;
            case Phase.Hover2:
                Hover2_Busy();                // п.5: занятое кресло ⇒ IsBusy, активация отказ
                Next(Phase.Hover3);
                break;
            case Phase.Hover3:
                Hover3_Activate();            // п.6: свободная активация ⇒ кресло + пилот
                Next(Phase.SuitHud0);
                break;
            case Phase.SuitHud0:
                SuitHud0_Source();            // п.32/Е3: HUD-источник «Suit» в шине
                Next(Phase.NetSeat0);
                break;

            // ── п.12 (Блок D, Д3): серверная занятость кресла (BUG-009) ─────
            case Phase.NetSeat0:
                NetSeat0_Begin();             // re-id валидация: ship в реестре
                Next(Phase.NetSeat1);
                break;
            case Phase.NetSeat1:
                NetSeat1_DenyForeign();       // DENY: PilotPeerId = чужой пир
                Next(Phase.NetSeat2);
                break;
            case Phase.NetSeat2:
                NetSeat2_AllowFreeAndSelf();  // ALLOW: PilotPeerId = free / self
                Next(Phase.EvaNet0);
                break;
            case Phase.EvaNet0:
                EvaNet0_PoseReplica();        // Д1: поза-реплика apply на не-владельце
                Next(Phase.Done);
                break;

            case Phase.Done:
                Finish();
                break;
            default:
                break;
        }

        if (_tickBudget > 0 && --_tickBudget == 0)
        {
            GD.Print($"FAIL: фаза {_phase} не дошла до конца (бюджет кадров)");
            Finish();
        }
    }

    /// <summary>Переход к фазе + бюджет кадров на случай зависания (защита от
    /// вечного ожидания — иначе headless-прогон висит до внешнего таймаута).</summary>
    private void Next(Phase p, int budget = 30)
    {
        _phase = p;
        _tickBudget = budget;
    }
}
