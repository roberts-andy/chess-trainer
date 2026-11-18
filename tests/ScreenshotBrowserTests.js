(async () => {
    const results = [];
    window.screenshotRegression = { status: "running", checks: results };
    const check = (name, condition) => {
        if (!condition) throw new Error(name);
        results.push(name);
    };
    const rejects = action => {
        try { action(); return false; }
        catch { return true; }
    };
    const until = async predicate => {
        const deadline = performance.now() + 3000;
        while (!predicate()) {
            if (performance.now() > deadline) throw new Error("Timed out waiting for a test gate.");
            await new Promise(resolve => setTimeout(resolve, 5));
        }
    };
    const image = async (color, name) => {
        const canvas = document.createElement("canvas");
        canvas.width = canvas.height = 2;
        canvas.getContext("2d").fillStyle = color;
        canvas.getContext("2d").fillRect(0, 0, 2, 2);
        const blob = await new Promise(resolve => canvas.toBlob(resolve, "image/png"));
        return new File([blob], name, { type: "image/png" });
    };
    const preview = document.getElementById("local-screenshot-preview");
    const originalBitmap = window.createImageBitmap;
    const calls = [];
    let receiverHook;
    let providerCalls = 0;
    try {
        chess.connect({
            invokeMethodAsync(method, ...args) {
                calls.push({ method, args });
                return receiverHook ? receiverHook(method, args) : Promise.resolve();
            }
        });
        const a = await image("red", "synthetic-A.png");
        const b = await image("blue", "synthetic-B.png");
        await chess.readFile({ files: [a] });
        const generationA = chess.getScreenshotGeneration();
        const bytesA = chess.getScreenshot(generationA);
        check("Ready A has a generation and local preview", !!generationA && preview.src.endsWith(bytesA));
        check("Selection callbacks contain metadata only", calls.every(call =>
            call.method === "ScreenshotSelectionChanged" && call.args.length === 3 &&
            call.args.every(value => typeof value !== "string" || (!value.startsWith("data:") && value !== bytesA))));

        const replacing = chess.readFile({ files: [b] });
        check("Replacement immediately invalidates consent to A", rejects(() => chess.getScreenshot(generationA)));
        await replacing;
        const generationB = chess.getScreenshotGeneration();
        const bytesB = chess.getScreenshot(generationB);
        check("B needs a new generation and displays B", !!generationB && generationB !== generationA &&
            bytesA !== bytesB && preview.src.endsWith(bytesB));

        chess.clearScreenshot();
        check("Clear invalidates consent and removes the preview",
            chess.getScreenshotGeneration() == null && rejects(() => chess.getScreenshot(generationB)) &&
            preview.hidden && !preview.hasAttribute("src"));
        await chess.readFile({ files: [a] });
        const beforeFailure = chess.getScreenshotGeneration();
        await chess.readFile({ files: [new File(["broken"], "broken.png", { type: "image/png" })] });
        check("Failed replacement cannot restore previous consent",
            chess.getScreenshotGeneration() == null && rejects(() => chess.getScreenshot(beforeFailure)));

        let resumeDecode;
        let first = true;
        window.createImageBitmap = (...args) => first
            ? (first = false, new Promise((resolve, reject) => {
                resumeDecode = () => originalBitmap(...args).then(resolve, reject);
            }))
            : originalBitmap(...args);
        const oldDecode = chess.readFile({ files: [a] });
        await until(() => !!resumeDecode);
        await chess.readFile({ files: [b] });
        const latest = chess.getScreenshotGeneration();
        resumeDecode();
        await oldDecode;
        check("Older asynchronous decode cannot replace B", !!latest &&
            chess.getScreenshotGeneration() === latest && chess.getScreenshot(latest) === bytesB);
        window.createImageBitmap = originalBitmap;

        let releaseReady;
        let hold = true;
        receiverHook = (_, args) => args[1] === true && hold
            ? (hold = false, new Promise(resolve => { releaseReady = resolve; }))
            : Promise.resolve();
        const oldReady = chess.readFile({ files: [a] });
        await until(() => !!releaseReady);
        const oldConsent = chess.getScreenshotGeneration();
        await chess.readFile({ files: [b] });
        const newReady = chess.getScreenshotGeneration();
        check("A busy callback cannot authorize the replacement", rejects(() => chess.getScreenshot(oldConsent)));
        releaseReady();
        await oldReady;
        check("Late callback completion cannot revive A",
            newReady === chess.getScreenshotGeneration() && chess.getScreenshot(newReady) === bytesB);
        receiverHook = undefined;

        const pendingConsent = newReady;
        const queuedRetrieval = Promise.resolve().then(() => {
            const bytes = chess.getScreenshot(pendingConsent);
            providerCalls++;
            return bytes;
        });
        chess.clearScreenshot();
        let refused = false;
        try { await queuedRetrieval; }
        catch { refused = true; }
        check("Clear before queued retrieval causes zero provider calls", refused && providerCalls === 0);
        await chess.readFile({ files: [b] });
        const reconfirmed = chess.getScreenshotGeneration();
        const authorized = chess.getScreenshot(reconfirmed);
        if (chess.isScreenshotCurrent(reconfirmed)) providerCalls++;
        check("Explicit consent to current B permits exactly B", providerCalls === 1 && authorized === bytesB);
        window.screenshotRegression = { status: "passed", checks: results, providerStubCalls: providerCalls, realProviderCalls: 0 };
    } catch (error) {
        window.screenshotRegression = { status: "failed", checks: results, error: error.message };
    } finally {
        window.createImageBitmap = originalBitmap;
        chess.disconnect();
        document.getElementById("results").textContent = JSON.stringify(window.screenshotRegression, null, 2);
    }
})();
