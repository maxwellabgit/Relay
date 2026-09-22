/** Provider state for one ambient excerpt. Neighboring text is not included. */
export function ambientProviderState(excerpt: string): { readonly origin: "observed"; readonly excerpt: string } {
  return { origin: "observed", excerpt };
}
