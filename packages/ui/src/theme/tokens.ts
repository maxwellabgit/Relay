/** 8px spacing grid and four radii — product visual system. */
export const space = {
  xxs: 4,
  xs: 8,
  sm: 12,
  md: 16,
  lg: 24,
  xl: 32,
} as const;

export const radius = {
  sm: 8,
  md: 12,
  lg: 20,
  xl: 28,
} as const;

export const typeScale = {
  xs: 12,
  sm: 14,
  md: 16,
  lg: 20,
} as const;

/** Minimum interactive target size (WCAG-aligned touch target). */
export const touchTarget = 44;
