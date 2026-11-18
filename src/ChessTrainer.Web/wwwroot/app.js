window.chess = (() => {
    let receiver;
    let currentGeneration;
    let pendingScreenshot;

    function showScreenshot() {
        const preview = document.getElementById("local-screenshot-preview");
        if (!preview) return;
        if (pendingScreenshot) {
            const source = `data:image/png;base64,${pendingScreenshot.png}`;
            if (preview.src !== source) preview.src = source;
            preview.hidden = false;
        } else {
            preview.removeAttribute("src");
            preview.hidden = true;
        }
    }

    async function notifySelection(generation, ready, error = "") {
        if (!receiver) return;
        try { await receiver.invokeMethodAsync("ScreenshotSelectionChanged", generation, ready, error); }
        catch { console.warn("Screenshot selection state could not be synchronized."); }
    }

    function startSelection() {
        const generation = crypto.randomUUID();
        currentGeneration = generation;
        pendingScreenshot = null;
        showScreenshot();
        void notifySelection(generation, false);
        return generation;
    }

    function clearScreenshot() {
        if (currentGeneration === undefined && !pendingScreenshot) {
            showScreenshot();
            return;
        }
        currentGeneration = undefined;
        pendingScreenshot = null;
        showScreenshot();
        void notifySelection(null, false);
    }

    async function normalize(blob, generation = startSelection()) {
        let image;
        try {
            if (!blob) throw new Error("Choose or paste an image before generating FEN.");
            if (blob.size > 10 * 1024 * 1024) throw new Error("Use an image smaller than 10 MB.");
            image = await createImageBitmap(blob);
            if (image.width * image.height > 30000000) throw new Error("Use an image under 30 megapixels.");
            const canvas = document.createElement("canvas");
            canvas.width = image.width;
            canvas.height = image.height;
            canvas.getContext("2d").drawImage(image, 0, 0);
            const png = canvas.toDataURL("image/png").split(",")[1];
            if (png.length > 14 * 1024 * 1024) throw new Error("Use an image smaller than 10 MB after PNG conversion.");
            if (currentGeneration !== generation) return;
            pendingScreenshot = { generation, png };
            showScreenshot();
            await notifySelection(generation, true);
        } catch (error) {
            if (currentGeneration === generation) {
                pendingScreenshot = null;
                showScreenshot();
                await notifySelection(generation, false, error.message || String(error));
            }
        } finally {
            image?.close();
        }
    }

    async function onPaste(event) {
        if (!document.querySelector(".screenshot-preview, input[accept^='image/png']")) return;
        const item = [...(event.clipboardData?.items || [])].find(item => item.type.startsWith("image/"));
        if (item) {
            event.preventDefault();
            const generation = startSelection();
            await normalize(item.getAsFile(), generation);
        }
    }

    return {
        connect(dotnet) {
            receiver = dotnet;
            document.addEventListener("paste", onPaste);
        },
        disconnect() {
            document.removeEventListener("paste", onPaste);
            receiver = null;
            currentGeneration = undefined;
            pendingScreenshot = null;
        },
        showScreenshot,
        clearScreenshot,
        getScreenshotGeneration() {
            return pendingScreenshot?.generation === currentGeneration ? currentGeneration : null;
        },
        isScreenshotCurrent(generation) {
            return currentGeneration === generation && pendingScreenshot?.generation === generation;
        },
        getScreenshot(generation) {
            if (currentGeneration !== generation || pendingScreenshot?.generation !== generation)
                throw new Error("The selected screenshot changed. Confirm the displayed image again before generating FEN.");
            return pendingScreenshot.png;
        },
        async readFile(input) {
            if (!input.files[0]) {
                clearScreenshot();
                return;
            }
            const generation = startSelection();
            await normalize(input.files[0], generation);
        },
        async paste() {
            const generation = startSelection();
            try {
                if (!navigator.clipboard?.read) throw new Error("This browser does not support image clipboard access. Use Choose screenshot instead.");
                const items = await navigator.clipboard.read();
                for (const item of items) {
                    const type = item.types.find(type => type.startsWith("image/"));
                    if (type) {
                        await normalize(await item.getType(type), generation);
                        return;
                    }
                }
                throw new Error("The clipboard has no image. Copy a screenshot, or use Choose screenshot.");
            } catch (error) {
                if (currentGeneration === generation) {
                    await notifySelection(generation, false, error.message || String(error));
                }
            }
        }
    };
})();

if ("serviceWorker" in navigator && window.isSecureContext) {
    navigator.serviceWorker.register("/sw.js").catch(() => console.warn("App installation support unavailable."));
}
