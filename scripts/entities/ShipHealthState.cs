using Godot;

namespace SpaceCorporation;

/// <summary>
/// Буфер здоровья корабля (Этап 4, шаг 4.5) — «правда сервера» о HP. По образцу
/// <see cref="TargetState"/>: дочерний узел у корня корабля (`Ship.tscn`),
/// `MultiplayerSynchronizer` c `RootPath = ".."` и
/// `scenes/ShipHealthReplication.tres` адресуют свойства от RootPath
/// (`ShipHealthState:Hp`).
///
/// СВОЙСТВА — «истина сервера»: пишет их ТОЛЬКО авторитет (интегратор корабля,
/// шаг 4.7). Писатель-не-авторитет — ошибка кода: сеттер печатает PrintErr и
/// всё равно записывает значение в ЛОКАЛЬНУЮ копию, но реплика (delta_owner,
/// В11 п.2) у пиров значение не меняет и не сотрёт её навсегда — поэтому
/// критерий (в) «клиент не может отменить урон» держится тем, что сервер при
/// каждом изменении HP отправляет дельту (OnChange), и она затирает локальную
/// запись (п.27 проверяет: локальное Hp = 100 после серверного 44 → после
/// реплики снова 44).
///
/// GodMode — тоже серверное поле (шаг 4.9): кнопка есть только у серверного
/// процесса, RPC нет вовсе; пир видит `god=on|off` строкой панели репликой.
///
/// Транспорт: только delta_owner через синхронизатор (OnChange + spawn=true);
/// в батч Record эти поля НЕ входят — метрика rec/s от HP не растёт (шаг 4.4/п.7).
///
/// Свойства ОБЯЗАНЫ быть `[Export] public ... { get; set; }` — иначе реплика
/// молча не доезжает (§3.11 п.2).
/// </summary>
public partial class ShipHealthState : Node
{
    [Export] public int MaxHp { get; set; } = 100;

    private int _hp = 100;
    /// <summary>Текущее здоровье. Пишет только авторитет (интегратор, шаг 4.7);
    /// запись с не-авторитета — PrintErr и ПЕЧАТЬ НАЗВАНИЯ: сигнал того, что код
    /// клиента полез в «правду сервера» (ловушка из прошлого: молча проходящая
    /// ложная проверка).</summary>
    [Export] public int Hp
    {
        get => _hp;
        set
        {
            GuardWrite(nameof(Hp), value.ToString());
            _hp = value;
        }
    }

    private bool _godMode;
    /// <summary>God Mode: урон не считается вовсе (шаг 4.6/4.9). Включает только
    /// сервер (кнопка/`--god`), RPC нет — с клиента запрос не принимается.</summary>
    [Export] public bool GodMode
    {
        get => _godMode;
        set
        {
            GuardWrite(nameof(GodMode), value.ToString());
            _godMode = value;
        }
    }

    private int _damageTakenCount;
    /// <summary>Сколько раз корабль получал урон (для детерминированных проверок
    /// п.17-п.20 и критерия (а): DamageTakenCount × урон должны совпадать).</summary>
    [Export] public int DamageTakenCount
    {
        get => _damageTakenCount;
        set
        {
            GuardWrite(nameof(DamageTakenCount), value.ToString());
            _damageTakenCount = value;
        }
    }

    private float _lastDamage;
    /// <summary>Последний нанесённый урон (для отладочной строки и проверок).</summary>
    [Export] public float LastDamage
    {
        get => _lastDamage;
        set
        {
            GuardWrite(nameof(LastDamage), value.ToString());
            _lastDamage = value;
        }
    }

    private Ship? _owner;

    /// <summary>Корабль-владелец (ближайший предок). У Health нет своего адреса —
    /// он идентифицируется через ShipNetId владельца.</summary>
    public Ship? Owner => _owner ??= GetParent() as Ship;

    /// <summary>Писатель обязан быть авторитетом. Проверка идёт по
    /// <see cref="Ship.IsServerAuthority"/> (флаг ApplyAuthority, Ship.cs:617):
    /// не-авторитет (клиент-кортекс корабля у пира) печатает PrintErr — сигнал,
    /// что код клиента полез в «правду сервера». Offline-роль — авторитет
    /// (ApplyAuthority поднимает флаг в offline тоже), поэтому однопользовательский
    /// урон работает (п.4). Запись при этом ВСЕГДА доходит до бэкфилда: жёсткий
    /// отказ сломал бы репликацию — сам синхронизатор вызывает сеттер на реплике
    /// (п.22 «Hp==0 доехал», п.24 «GodMode==true после Pump»), а движок в
    /// OnChange-режиме не имеет пути записать поле мимо сеттера. Значит защита от
    /// «клиент отменил урон» (п.27) держится не сеттером, а тем, что сервер при
    /// каждом изменении HP шлёт дельту (OnChange), которая затирает локальную
    /// запись. П.11 проверяет не «Hp не изменился», а «страховка сработала» —
    /// предикат <see cref="IsNonAuthorityWrite"/> перед такой записью истинен.</summary>
    private void GuardWrite(string what, string value)
    {
        var owner = Owner;
        if (owner != null && !owner.IsServerAuthority)
            GD.PrintErr($"ShipHealthState: запись {what} = {value} на НЕ-авторитете " +
                        $"(корабль {OwnerNetIdOf()}) — это «правда сервера», " +
                        "клиент её не пишет (Этап 4, шаг 4.5).");
    }

    /// <summary>Тот же предикат, что в GuardWrite, но как чистая функция — чтобы
    /// headless-тест (п.11) мог детерминированно проверить «запись с не-авторитета
    /// будет помечена как чужая», не перехватывая stderr. true — это не-авторитет.</summary>
    public bool IsNonAuthorityWrite()
    {
        var owner = Owner;
        return owner != null && !owner.IsServerAuthority;
    }

    /// <summary>Серверная команда включить/выключить god mode (шаг 4.9):
    /// единственный путь записи (кнопка серверного процесса / `--god`).
    /// RPC-обёртки НЕТ намеренно: с клиента запрос не принимается вовсе.</summary>
    public void SetGodModeServerOnly(bool enabled)
    {
        if (Owner != null && !Owner.IsServerAuthority)
        {
            GD.PrintErr("ShipHealthState: SetGodModeServerOnly вызван на не-авторитете — " +
                        "метод серверный, вызывается только из серверного процесса (шаг 4.9).");
            return;
        }
        GodMode = enabled;
    }

    private int OwnerNetIdOf()
    {
        var net = Owner?.GetNodeOrNull<NetState>("NetState");
        return net?.ShipNetId ?? 0;
    }
}
