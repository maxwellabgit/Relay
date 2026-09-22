/**
 * Web alias for the explicit in-memory demo client.
 * Requires EXPO_PUBLIC_RELAY_ALLOW_DEMO=1 via createAppClient / with-demo-flag.
 */
export {
  createBrowserDemoClient as createWebClient,
  type BrowserDemoHandle as WebClientHandle,
  type BrowserDemoOptions as WebClientOptions,
} from "./createBrowserDemoClient";
