#!/usr/bin/env python3
"""
Оценка серверного трафика на клиентов: 4 игрока, 400 ИИ-кораблей на поле боя,
300 кораблей с 1 целью, 50 с 2 целями, 50 с 4 целями (всего 600 ссылок «цель»).

Модель опирается на фактические константы проекта (SpaceCorporation):
  NetworkManager.ReplicationHz = 30, физика 60 Гц, гейт публикации `_netPublishPhase % 2`;
  реплицируемые свойства корабля — scenes/ShipReplication.tres (8 свойств NetState);
  InputFrame.Size = 16 байт, 60 кадров/с на пилота;
  стрельба стадии 3 — hitscan-события (RpcRequestFire / RpcOnHitConfirmed).

Скрипт считает три сценария репликации целей и два уровня стрелкового шума,
печатает байты/с и кбит/с на клиента и на сервер, и пакеты/с (порог плана —
<50 логических пакетов/с на игрока).

Использование: python tools/traffic_estimate.py
"""

from dataclasses import dataclass
import math

# --------------------------------------------------------------- транспорт
ENET_OVERHEAD = 4      # ENetPacketPeer header
UDP_IP_OVERHEAD = 28   # UDP(8) + IPv4(20)
# Godot-обвязка RPC: путь узла (VarInt len + bytes) + номер метода + аргументы.
# Для одного и того же повторяющегося RPC путь кэшируется не движком, поэтому
# считаем путь явно; в плотной пачке он делится между RPC, но мы консервативны.
RPC_PATH = 12
RPC_METHOD = 2
# Сколько логических репликационных обновлений (дельт свойств) влезает в один
# MTU-пакет 1400 байт: зависит от размера дельты.
MTU = 1400

# Сцена-цель расчёта (net-refactor-plan.md §0).
N_CLIENTS_DEFAULT = 4


def packets_per_sec(updates_per_sec, bytes_per_update):
    """Логические обновления -> wire-пакеты с учётом фрагментации/пачинга."""
    per_packet = max(1, MTU // (bytes_per_update + RPC_PATH + RPC_METHOD))
    return updates_per_sec / per_packet


def wire_bytes(payload_bytes_per_sec, packets):
    return payload_bytes_per_sec + packets * (ENET_OVERHEAD + UDP_IP_OVERHEAD)


# ------------------------------------------------------- размер снимка корабля
@dataclass
class ShipSnapshot:
    """Дельта-обновление NetState на одного получателя (байты payload)."""
    pos: int = 6          # Vector3 квантованный (16 бит на ось по 1 см) — оптимизация
    pos_f32: int = 12     # Vector3 как 3×float32 (то, что шлёт Godot сейчас)
    rot: int = 8          # Quaternion: 3 компонента + индекс пропущенного (16 бит)
    rot_f32: int = 16     # Quaternion 4×float32
    vel: int = 6          # NetLinearVelocity квант
    vel_f32: int = 12
    avel: int = 4         # NetAngularVelocity (часто 0, дельта дешёвая)
    tick: int = 3         # SimTick как VarInt-дельта
    netid: int = 3        # ShipNetId (VarInt)
    applied: int = 0      # AppliedTick+AppliedMask: пилоту нужны, наблюдателю — нет

    def bytes(self, quantized: bool, for_pilot: bool) -> int:
        if quantized:
            b = self.pos + self.rot + self.vel + self.avel + self.tick + self.netid
        else:
            b = self.pos_f32 + self.rot_f32 + self.vel_f32 + self.avel + self.tick + self.netid
        if for_pilot:
            b += 6  # AppliedTick + AppliedMask
        return b


# --------------------------------------------------------- цели и события
def target_ref_bytes(id_bits: int) -> int:
    """Ссылка на цель в реплике: VarInt-номер цели + квантованная точка прицела."""
    id_bytes = (id_bits + 7) // 8 if id_bits <= 16 else 3
    aim = 6  # 3 координаты по 16 бит (1 см) — нужно клиенту для предсказания упреждения
    return id_bytes + aim


@dataclass
class Scenario:
    name: str
    targets_replicated: bool      # цели вообще едут в реплике
    ids_only: bool = False        # только ID целей без точки прицела
    aim_point: bool = True
    fire_events_per_sec: float = 0.0
    hit_events_per_sec: float = 0.0


def estimate(sc: Scenario, n_ships: int, n_clients: int, hz: int,
             quantized: bool, visible_frac: float, id_bits: int):
    """Возвращает dict с метриками на одного клиента и суммарно на сервер."""
    ships = int(n_ships * visible_frac)
    # распределение целей: 300×1, 50×2, 50×4 → среднее 1.5 цели на корабль
    avg_targets = (300 * 1 + 50 * 2 + 50 * 4) / 400
    tgt_bytes = target_ref_bytes(id_bits)
    if sc.ids_only:
        tgt_bytes = (id_bits + 7) // 8 if id_bits <= 16 else 3

    payload_per_update = ShipSnapshot().bytes(quantized, for_pilot=False)
    if sc.targets_replicated:
        payload_per_update += avg_targets * tgt_bytes

    updates = ships * hz
    payload = updates * payload_per_update
    pkts = packets_per_sec(updates, payload_per_update)

    # события выстрелов/попаданий: broadcast на всех, кроме отправителя
    ev = 0
    ev_pkts = 0
    if sc.fire_events_per_sec:
        # RpcOnHitConfirmed: targetNetId + damage + hitPoint(3×f32) + shooterNetId + tick
        ev_size = RPC_PATH + RPC_METHOD + 3 + 2 + 12 + 3 + 3
        ev += sc.hit_events_per_sec * ev_size
        ev_pkts += packets_per_sec(sc.hit_events_per_sec, ev_size - RPC_PATH - RPC_METHOD)
    down = wire_bytes(payload, pkts) + ev + ev_pkts * (ENET_OVERHEAD + UDP_IP_OVERHEAD)

    # апстрим: 4 игрока шлют ввод 60/с по 16 байт + запросы выстрелов
    up_payload = 60 * (16 + RPC_PATH + RPC_METHOD)
    up_pkts = packets_per_sec(60, 16)
    if sc.fire_events_per_sec / max(1, n_clients):
        per_player_shots = sc.fire_events_per_sec / n_clients
        up_payload += per_player_shots * (RPC_PATH + RPC_METHOD + 3 + 3 + 12)
        up_pkts += packets_per_sec(per_player_shots, 18)
    up = wire_bytes(up_payload, up_pkts)

    return {
        "ships_visible": ships,
        "payload_per_update": payload_per_update,
        "updates_per_sec": updates,
        "repl_packets_per_sec": pkts,
        "down_bytes_per_sec": down,
        "up_bytes_per_sec": up,
    }


def fmt(bps):
    return f"{bps/1024:7.1f} КБ/с | {bps*8/1000:7.0f} кбит/с"


# --------------------------------------------------- целевая модель (норматив)
# Запись по тирам (§2.1/§2.3 плана net-refactor-plan.md): near/mid — полная
# (30 Б: позиция int16-смещение от якоря тира 6 Б, поворот 3×int16+индекс 8 Б,
# скорости 6+4 Б, netid 2 Б, дельта тика 2 Б — консервативно без плотной
# упаковки), far — только позиция (квант 10 см) + грубый поворот (3×int8+индекс)
# + netid + дельта тика = 2+6+4+2 = 14 Б, без скоростей.
FULL_REC = ShipSnapshot().bytes(True, for_pilot=False)          # 30 Б
FAR_REC = 2 + 6 + 4 + 2                                        # 14 Б
HDR = 8                     # заголовок пакета батча: базовый тик + число записей
# События: hitscan-подтверждение попадания (RpcOnHitConfirmed).
EV_SIZE = RPC_PATH + RPC_METHOD + 3 + 2 + 12 + 3 + 3
EV_PER_SEC = 6              # подтверждений/с на клиента (8 выстрелов/с на игрока)
# Апстрим: 1 пилот × 60 кадров ввода × 16 Б + 8 запросов выстрелов/с.
UP_PAYLOAD = 60 * (16 + RPC_PATH + RPC_METHOD) + 8 * (RPC_PATH + RPC_METHOD + 3 + 3 + 12)
UP_PKTS = packets_per_sec(60, 16) + packets_per_sec(8, 18)

TIERS = {
    "консервативно": [("near", 100, 30, FULL_REC), ("mid", 200, 10, FULL_REC),
                      ("far", 100, 3, FAR_REC)],
    "базово":        [("near", 100, 20, FULL_REC), ("mid", 200, 10, FULL_REC),
                      ("far", 100, 3, FAR_REC)],
    "экономно":      [("near", 60, 20, FULL_REC), ("mid", 140, 5, FULL_REC),
                      ("far", 200, 2, FAR_REC)],
}
# Варианты репликации целей: none / onchange (targetId 2 Б при смене, ~1 смена/с
# на корабль — отдельное событие, а не поле записи) / every (targetId + точка
# прицела 8 Б в каждой записи, ×1.5 цели в среднем).
TARGET_MODES = {
    "без целей": (0.0, 0.0),
    "onchange": (0.0, 400 * 1 * 2),
    "every": (1.5 * target_ref_bytes(10), 0.0),
}


def target_model(tiers, per_rec_extra=0.0, event_extra=0.0, n_clients=N_CLIENTS_DEFAULT):
    """Считает целевую модель: один упакованный батч на репликационный тик.

    Возвращает dict: updates (записей/с на клиента), pkts (wire-пакетов батча/с),
    down (байт/с вниз на клиента), up (байт/с апстрима на клиента).
    Пакеты считаются по календарю тиков: тик t несёт тиры, период которых делит
    max hz без остатка; объём тика режется на MTU-вместимости батча.
    """
    upd = sum(n * hz for _, n, hz, _rec in tiers)
    payload = sum(n * hz * (_rec + per_rec_extra) for _, n, hz, _rec in tiers) + event_extra
    top = max(hz for _, _, hz, _rec in tiers)
    cap = MTU - (ENET_OVERHEAD + UDP_IP_OVERHEAD + RPC_PATH + RPC_METHOD + HDR)
    pkts = 0
    for t in range(top):
        vol = 0
        for _, n, hz, rec in tiers:
            if t % (top // hz) == 0:
                vol += n * (rec + per_rec_extra)
        pkts += max(1, math.ceil(vol / cap))   # ceil
    down = wire_bytes(payload, pkts) + HDR * pkts + EV_PER_SEC * EV_SIZE
    up = wire_bytes(UP_PAYLOAD, UP_PKTS)
    return {"updates": upd, "pkts": pkts, "down": down, "up": up, "n_clients": n_clients}


# Нормативный бюджет (net-refactor-plan.md §4) — пороги на стенде R9
# (4 клиента, 400 кораблей, 600 целей, 32 выстрела/с суммарно).
BUDGET = {
    "down_per_client": 200 * 1024,      # ≤ 200 КБ/с (1.6 Мбит/с)
    "pkts_per_client": 150,             # ≤ 150 репликационных пакетов/с
    "updates_per_client": 5300,         # ≤ 5300 записей состояния/с
    "server_down_total": 800 * 1024,    # ≤ 800 КБ/с суммарно (6.4 Мбит/с)
    "up_per_client": 10 * 1024,         # ≤ 10 КБ/с
}
# Нормативная конфигурация, по которой выставлены пороги: консервативный пресет
# тиров + цели только по смене (targetId). Скрипт возвращает код 0, если она
# проходит бюджет; остальные комбинации печатаются справочно.
NORMATIVE = ("консервативно", "onchange")


def budget_check(name, r):
    """Печатает строку пресета и проверку всех порогов бюджета. Возвращает True
    если ни один порог не превышен."""
    checks = [
        ("вниз/клиент", r["down"], BUDGET["down_per_client"], "Б/с"),
        ("пакетов/с", r["pkts"], BUDGET["pkts_per_client"], ""),
        ("записей/с", r["updates"], BUDGET["updates_per_client"], ""),
        ("сервер вниз", r["down"] * r["n_clients"], BUDGET["server_down_total"], "Б/с"),
        ("апстрим/клиент", r["up"], BUDGET["up_per_client"], "Б/с"),
    ]
    worst = 0.0
    marks = []
    for label, val, lim, unit in checks:
        frac = val / lim
        worst = max(worst, frac)
        marks.append(f"{label} {frac*100:5.1f}%")
    verdict = "PASS" if worst <= 1.0 else "FAIL"
    print(f"  {name:46s} {verdict}  worst {worst*100:5.1f}% | " + ", ".join(marks))
    return worst <= 1.0


def main():
    N_SHIPS, N_CLIENTS, HZ = 400, 4, 30
    print(f"Модель: {N_SHIPS} ИИ-кораблей, {N_CLIENTS} клиента, репликация {HZ} Гц, "
          f"ввод 60 Гц × 16 Б, MTU {MTU} Б\n")

    scenarios = [
        Scenario("A. Цели НЕ реплицируются (клиент сам выбирает по близости)",
                 targets_replicated=False, fire_events_per_sec=0, hit_events_per_sec=0),
        Scenario("B. Цели НЕ реплицируются + стрельба (низкая плотность)",
                 targets_replicated=False, fire_events_per_sec=200, hit_events_per_sec=120),
        Scenario("C. Цели НЕ реплицируются + стрельба (высокая плотность)",
                 targets_replicated=False, fire_events_per_sec=800, hit_events_per_sec=500),
        Scenario("D. Цели реплицируются (ID+точка прицела) + стрельба средняя",
                 targets_replicated=True, fire_events_per_sec=200, hit_events_per_sec=120),
        Scenario("E. Цели реплицируются (только ID) + стрельба средняя",
                 targets_replicated=True, ids_only=True, fire_events_per_sec=200,
                 hit_events_per_sec=120),
    ]

    for quantized in (False, True):
        tag = "квантование (16 бит/ось)" if quantized else "без квантования (float32, как сейчас)"
        print(f"=== {tag} ===")
        for sc in scenarios:
            for vf in (1.0, 0.25):
                r = estimate(sc, N_SHIPS, N_CLIENTS, HZ, quantized, visible_frac=vf, id_bits=10)
                scope = "все 400 в поле зрения" if vf == 1.0 else "интерес: 25% (100 кораблей)"
                print(f"  {sc.name}")
                print(f"    {scope}")
                print(f"      дельта/корабль: {r['payload_per_update']:3.0f} Б, "
                      f"обновлений/с: {r['updates_per_sec']:5d}, "
                      f"репл. пакетов/с: {r['repl_packets_per_sec']:6.0f}")
                print(f"      вниз на клиента: {fmt(r['down_bytes_per_sec'])}; "
                      f"апстрим клиента: {fmt(r['up_bytes_per_sec'])}")
                print(f"      сервер вниз (×{N_CLIENTS}): {fmt(r['down_bytes_per_sec']*N_CLIENTS)}; "
                      f"сервер вверх: {fmt(r['up_bytes_per_sec']*N_CLIENTS)}")
            print()

    # ------------------------- предельная частота безInterest-сценария
    print("=== предельная частота репликации (сценарий C, все 400, без квантования) ===")
    for hz in (30, 20, 10, 5):
        r = estimate(scenarios[2], N_SHIPS, N_CLIENTS, hz, False, 1.0, 10)
        print(f"  {hz:2d} Гц: вниз {fmt(r['down_bytes_per_sec'])}, "
              f"репл. пакетов/с {r['repl_packets_per_sec']:5.0f}")

    # ------------------- ЦЕЛЕВАЯ МОДЕЛЬ: AOI + ступенчатая частота + квантование
    # + один упакованный пакет снапшота на клиента за тик (вместо RPC на объект).
    print("\n=== целевая модель (после рефакторинга) ===")
    for pname, tiers in TIERS.items():
        for tname, (per_rec_extra, event_extra) in TARGET_MODES.items():
            r = target_model(tiers, per_rec_extra, event_extra, N_CLIENTS)
            print(f"  {pname:11s} | {tname:9s}: обновлений/с {r['updates']:5d}, "
                  f"пакетов/с {r['pkts']:4d}, вниз {fmt(r['down'])}, "
                  f"сервер ×{N_CLIENTS} {fmt(r['down']*N_CLIENTS)}")

    # ------------------- НОРМАТИВНЫЙ БЮДЕТ (net-refactor-plan.md §4)
    # Пороги стенда R9; целевая модель обязана им соответствовать, иначе шаг
    # рефакторинга не закрыт. Считается по консервативному пресету тиров +
    # цели по смене (худший из приемлемых вариантов).
    print("\n=== нормативный бюджет (пороги §4 плана, консервативный пресет + цели по смене) ===")
    print(f"  пороги: вниз/клиент {BUDGET['down_per_client']/1024:.0f} КБ/с, "
          f"пакетов/с {BUDGET['pkts_per_client']}, записей/с {BUDGET['updates_per_client']}, "
          f"сервер вниз {BUDGET['server_down_total']/1024:.0f} КБ/с, "
          f"апстрим/клиент {BUDGET['up_per_client']/1024:.0f} КБ/с")
    ok = True
    for pname, tiers in TIERS.items():
        for tname, (per_rec_extra, event_extra) in TARGET_MODES.items():
            r = target_model(tiers, per_rec_extra, event_extra, N_CLIENTS)
            passed = budget_check(f"{pname} + {tname}", r)
            # Норматив — консервативный пресет + цели по смене (именно он принят
            # в §4 как основание порогов). Остальные строки справочные: «every»
            # (цели в каждой записи) превышает бюджет намеренно, он показан
            # только чтобы оценить цену такой схемы.
            if (pname, tname) == NORMATIVE:
                ok = passed
    print(f"  норматив: {' + '.join(NORMATIVE)} — {'PASS' if ok else 'FAIL'}")

    # ------------------- модель «Godot как есть»: 1 пакет на дельту корабля
    # DEVELOPMENT.md §2.6: при 2 клиентах логических снимков 30/с, а wire-пакетов
    # ≈156/с — т.е. пачинга «много кораблей в один MTU» движок не делает, каждый
    # RPC-заход синхронизатора = отдельный пакет. Считаем верхнюю границу.
    print("\n=== верхняя граница: 1 wire-пакет на дельту корабля (Godot как есть) ===")
    for hz in (30, 20, 10, 5):
        for q in (False, True):
            per_upd = ShipSnapshot().bytes(q, for_pilot=False) + 1.5 * target_ref_bytes(10)
            updates = N_SHIPS * hz
            # каждый заход = RPC(path+method) + дельта, поверх ENet+UDP+IP
            per_pkt = per_upd + RPC_PATH + RPC_METHOD + ENET_OVERHEAD + UDP_IP_OVERHEAD
            down = updates * per_pkt
            print(f"  {hz:2d} Гц {'квант' if q else 'f32 '}: пакетов/с {updates:6d}, "
                  f"вниз на клиента {fmt(down)}, сервер ×{N_CLIENTS} {fmt(down*N_CLIENTS)}")
    return ok


if __name__ == "__main__":
    import sys
    sys.exit(0 if main() else 1)
