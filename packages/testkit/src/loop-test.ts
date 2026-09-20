export type LoopTestOptions = {
  readonly iterations?: number;
  readonly seed?: number;
};

/**
 * Run a deterministic test body repeatedly from a fresh-state expectation.
 * Each iteration receives a seed derived from the base seed + iteration index.
 */
export async function loopTest(
  name: string,
  body: (ctx: { readonly iteration: number; readonly seed: number }) => void | Promise<void>,
  options: LoopTestOptions = {},
): Promise<void> {
  const iterations = options.iterations ?? 20;
  const seed = options.seed ?? 1;
  if (iterations < 1) throw new Error(`loopTest(${name}): iterations must be >= 1`);
  for (let iteration = 1; iteration <= iterations; iteration += 1) {
    try {
      await body({ iteration, seed: seed + iteration - 1 });
    } catch (error) {
      const detail = error instanceof Error ? error.message : String(error);
      throw new Error(`loopTest(${name}) failed at iteration ${iteration}/${iterations}: ${detail}`, {
        cause: error,
      });
    }
  }
}
