"""Audio prepare CLI scaffold — WhisperX pipeline lands in commit 4."""

from __future__ import annotations

import argparse


def main() -> None:
    parser = argparse.ArgumentParser(prog="relay-audio-prepare")
    parser.add_argument("input", nargs="?", help="Path to a private recording")
    parser.add_argument("--help-scaffold", action="store_true")
    args = parser.parse_args()
    print("audio:prepare scaffold ready; WhisperX pipeline lands in commit 4.")
    if args.input:
        print(f"would prepare: {args.input}")


if __name__ == "__main__":
    main()
