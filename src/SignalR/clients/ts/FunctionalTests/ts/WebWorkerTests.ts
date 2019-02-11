// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

import { ENDPOINT_BASE_URL } from "./Common";

// On slower CI machines, these tests sometimes take longer than 5s
jasmine.DEFAULT_TIMEOUT_INTERVAL = 10 * 1000;

if (typeof window !== "undefined" && (window as any).Worker) {
    describe("WebWorkers", () => {
        it("can use SignalR client", (done) => {
            const worker = new Worker("worker.js");
            const testMessage = "Hello World!";

            worker.postMessage(ENDPOINT_BASE_URL);

            worker.onmessage = (e) => {
                if (e.data === "connected") {
                    worker.postMessage(testMessage);
                } else {
                    expect(e.data).toBe(`Received message: ${testMessage}`);
                    worker.terminate();
                    done();
                }
            };
        });
    });
}
