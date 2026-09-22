import { useEffect, useState } from "react";
import { AccessibilityInfo, Platform } from "react-native";

/**
 * Prefer reduced motion when the OS/user requests it.
 * On web, also listens to prefers-reduced-motion.
 */
export function usePrefersReducedMotion(): boolean {
  const [reduced, setReduced] = useState(false);

  useEffect(() => {
    let cancelled = false;
    void AccessibilityInfo.isReduceMotionEnabled().then((value) => {
      if (!cancelled) setReduced(value);
    });
    const sub = AccessibilityInfo.addEventListener("reduceMotionChanged", setReduced);

    let media: MediaQueryList | undefined;
    const onWeb = (event: MediaQueryListEvent) => {
      if (!cancelled) setReduced(event.matches);
    };
    if (Platform.OS === "web" && typeof globalThis.matchMedia === "function") {
      media = globalThis.matchMedia("(prefers-reduced-motion: reduce)");
      if (media.matches) setReduced(true);
      media.addEventListener?.("change", onWeb);
    }

    return () => {
      cancelled = true;
      sub.remove();
      media?.removeEventListener?.("change", onWeb);
    };
  }, []);

  return reduced;
}
