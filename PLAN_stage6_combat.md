# План: баги выделения/прицела + класс оружия (лазерная турель)

## Найденные причины (изучение кода)

1. **Выделение в лок-он в режиме пилота.** `TargetOverlay._UnhandledInput` гейтит
   выбор цели признаком `Input.MouseMode == Visible`. Но в пилотировании 2.0
   курсор тоже виден (прицел = свободный курсор, `Ship.PilotCam.cs`
   `EnsurePilotInput` ставит `MouseMode.Visible`; `SetInteractionMode` мышиный
   режим не меняет). Признак курсора больше НЕ эквивален режиму — нужен явный
   гейт `ship.IsInteractionMode` (как в `HudLayer` п.260: `myShip.IsLocalPilot
   && !myShip.IsInteractionMode`).
2. **Попадание ниже перекрестия.** Перекрестие (`AimReticle`) = свободный курсор,
   а выстрел идёт СТРОГО по носу (`-Basis.Z` и на клиенте `AimPointWorld()`, и
   на сервере в `ProcessPendingFires`). Нос догоняет курсор по τ (PilotAim) —
   при быстром прицеле/зажатии нос отстаёт, след попадания ниже перекрестия.
3. **Лазерная линия уже вдвое короче** (`ShowAimingBeam`:
   `AimingBeamLifetime * 0.5`, коммит `9dec6eb`) — проверить только, что
   `_aimBeam` живёт `AimingBeamLifetime` (=1.0) → 0.5 с. Да. Правка не нужна,
   добавить assert в тест.
4. **Класса оружия нет.** Жёсткие экспорт-константы `FireCooldownSec=0.2`,
   `FireDamage=25`, `LaserRange=400` в `Ship.Combat.cs`.

## Шаги

1. `scripts/entities/WeaponType.cs` — enum `WeaponKind { LaserTurret }`;
   класс `WeaponType`: Kind, Hitscan, Range=400, ShieldDamage=10,
   HullDamage=20, EnergyCost=10, FireRate=2 (выстр/с, кулдаун=1/FireRate=0.5);
   статичный `WeaponType.LaserTurret`. В `ShipStatCatalog` (Courier) — поле
   `WeaponType Weapon`; корабль в `ApplyStatsFromCatalog` берёт `Weapon`.
2. Энергия: `ShipHealthState` — `Energy`/`MaxEnergy=100`/реген (сек), поля в
   `ShipHealthReplication.tres`. Выстрел требует `Energy >= EnergyCost`
   (клиент-предсказание + сервер), сервер списывает в `AcceptFireFrom`/
   `TryFire`(offline), сервер же регенит в `_PhysicsProcess`.
3. Прицел по курсору/цели: `AimPointWorld()` — если цель выбрана
   (`TargetingService.TargetNode` в режиме пилота) → мировая позиция цели,
   иначе луч камеры через курсор на `Weapon.Range`; нос-гейт сервера
   (`AimToleranceM`) остаётся (античит), хитскан сервера идёт ОТ ствола В
   точку `aimPoint` (не строго по носу) → попадание в перекрестие/цель.
4. Автоогонь зажатой ЛКМ: `ShipMouseCatcher` — Pressed/Released держат флаг
   `FireHeld`; `_PhysicsProcess` корабля при зажатой ЛКМ стреляет с кулдауном
   `1/Weapon.FireRate` (0.5 с, вместо прежних 0.2 — темп ведёт карточка оружия).
5. HUD: `WeaponReticle.cs` — маркер точки прицела орудия (по одному на ствол,
   группа `WeaponMuzzle`): точка на дистанции оружия вдоль фактического
   прицела, пересчёт в экранные координаты (`Camera3D.UnprojectPosition`),
   не рисуется за камерой; при выделенной цели маркер на цели.
6. Баг 1: гейт выбора `TargetOverlay._UnhandledInput` → локальный корабль в
   `IsInteractionMode` (ссылку на Ship HudLayer передаёт оверлею при
   `AssignShip`; offline без корабля — как раньше).
7. Тесты: Stage3 (п.18-п.25) — перевести числа урона на карточку оружия
   (щит 10/корпус 20, темп 0.5 с), новый п. «хитскан по aimPoint вне носа
   в допуске — след в aimPoint», «выстрел без энергии отклонён», «удержание
   ЛКМ = темп 2/с», «выделение цели в режиме пилота не работает, в
   интеракции — работает» (Stage4/Stage5 где есть selection-проверки).
   TimeLimit поднят (регрессия по темпу).
8. Прогнать все сьюты; записи в `dev-journal.md`; найденное — в `bugs.md`.

## Что уже сделано (не переделывать)
- `AimingBeamLifetime * 0.5` в `ShowAimingBeam` (9dec6eb).
