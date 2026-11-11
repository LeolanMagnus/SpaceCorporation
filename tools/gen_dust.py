#!/usr/bin/env python3
"""Генерация текстуры «космической пыли» — белый мягкий диск с плавным
затуханием альфы к краю. Маленькая (32x32), без сжатия-артефактов: партиклы
рисуются в 1-2 пикселя, поэтому диск должен быть гладким уже в маленьком
размере (mip-levels в Godot сами уменьшат без ступенек).

Запуск: python tools/gen_dust.py
"""
import math
from pathlib import Path

import numpy as np
from PIL import Image

SIZE = 32
OUT = Path(__file__).resolve().parent.parent / "assets" / "vfx" / "dust_dot.png"

def main() -> None:
    c = (SIZE - 1) / 2.0
    y, x = np.mgrid[0:SIZE, 0:SIZE]
    r = np.sqrt((x - c) ** 2 + (y - c) ** 2) / (SIZE / 2.0)  # 0 в центре, 1 на краю
    # Мягкий диск: гладкий спад от 1 в ядре (r<0.35) к 0 на ободе (r>=1).
    core, edge = 0.35, 1.0
    t = np.clip((r - core) / (edge - core), 0.0, 1.0)
    a = (1.0 - t) ** 2  # квадратичный спад: без «кольца» на ободе
    rgba = np.zeros((SIZE, SIZE, 4), dtype=np.uint8)
    rgba[..., 0] = 255  # белый
    rgba[..., 1] = 255
    rgba[..., 2] = 255
    rgba[..., 3] = np.round(a * 255).astype(np.uint8)
    OUT.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(rgba, "RGBA").save(OUT)
    # Самопроверка: центр непрозрачный, угол прозрачный.
    assert rgba[SIZE // 2, SIZE // 2, 3] == 255
    assert rgba[0, 0, 3] == 0
    print(f"OK: {OUT} {SIZE}x{SIZE}, центр alpha={rgba[SIZE//2, SIZE//2, 3]}, угол alpha={rgba[0,0,3]}")

if __name__ == "__main__":
    main()
