import { describe, expect, it } from "vitest";
import {
  formatModelDeliveryIdentity,
  formatModelDeliveryProgress,
  type ModelPin,
} from "./model-delivery.js";

const pin: ModelPin = {
  id: "demo",
  version: "1.0.0",
  license: "apache-2.0",
  byteSize: 600_000_000,
  sha256: "abc123",
};

describe("model delivery copy", () => {
  it("shows size and a Wi-Fi hint without raw byte counts", () => {
    expect(
      formatModelDeliveryProgress({ bytesReceived: 1_048_576, pin, wifiRecommended: true }),
    ).toBe("1.0 MB / 572.2 MB · Use Wi-Fi");
  });

  it("shows version, license, and the content hash", () => {
    expect(formatModelDeliveryIdentity(pin)).toBe("1.0.0 · apache-2.0 · abc123");
  });
});
