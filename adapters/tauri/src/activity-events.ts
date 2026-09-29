export async function listenActivityHost(handler: (payload: unknown) => void): Promise<() => void> {
  const { listen } = await import("@tauri-apps/api/event");
  const unlisten = await listen<unknown>("activity-observation", (event) => {
    handler(event.payload);
  });
  return () => {
    unlisten();
  };
}
