import { describe, expect, it } from "vitest";
import { frameFromFinding, HaloDisplayPort } from "./halo-display.js";

describe("HaloDisplayPort", () => {
  it("records brief finding frames and supports clear/reconnect", async () => {
    const port = new HaloDisplayPort();
    await port.show(frameFromFinding("API", "Application Programming Interface"));
    await port.clear();
    expect(port.frames.map((f) => f.kind)).toEqual(["finding", "clear"]);
    port.disconnect();
    await expect(port.show({ kind: "status", title: "x" })).rejects.toThrow("halo_disconnected");
    port.reconnect();
    await port.show({ kind: "listening", title: "Listen", body: "on" });
    expect(port.frames.at(-1)?.kind).toBe("listening");
  });
});
