export class VirtualClock {
  private ms = 0;

  now(): Date {
    return new Date(this.ms);
  }

  nowMs(): number {
    return this.ms;
  }

  advance(ms: number): void {
    this.ms += ms;
  }

  async sleep(ms: number): Promise<void> {
    this.ms += ms;
  }
}

export function createDeterministicIds(seed = "id"): { next(prefix: string): string } {
  let n = 0;
  return {
    next(prefix: string) {
      n += 1;
      return `${seed}_${prefix}_${n}`;
    },
  };
}
