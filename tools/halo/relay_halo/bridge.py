"""Halo NDJSON bridge scaffold — Stage 2 implements emulator wiring."""

from __future__ import annotations

import argparse
import sys


def main() -> None:
    parser = argparse.ArgumentParser(prog="relay-halo-bridge")
    parser.add_argument("--emulator", action="store_true", default=True)
    args = parser.parse_args()
    print("halo:emulator scaffold ready; Stage 2 wires HaloEmulator.", file=sys.stderr)
    _ = args


if __name__ == "__main__":
    main()
