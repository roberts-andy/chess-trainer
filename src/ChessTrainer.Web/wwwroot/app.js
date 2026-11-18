window.chess = (() => {
    let receiver;
    let reading = false;
    let pendingScreenshot;
    function showScreenshot() {
        const preview = document.getElementById("local-screenshot-preview");
        if (!preview) return;
        if (pendingScreenshot) {
            const source = `data:image/png;base64,${pendingScreenshot}`;
            if (preview.src !== source) preview.src = source;
            preview.hidden = false;
        } else {
            preview.removeAttribute("src");
            preview.hidden = true;
        }
    }
    async function report(error) {
        if (receiver) await receiver.invokeMethodAsync("ImageError", error.message || String(error));
    }
    async function normalize(blob) {
        if (reading || !receiver) return;
        reading = true;
        try {
            if (blob.size > 10 * 1024 * 1024) throw new Error("Use an image smaller than 10 MB.");
            const image = await createImageBitmap(blob);
            try {
                if (image.width * image.height > 30000000) throw new Error("Use an image under 30 megapixels.");
                const canvas = document.createElement("canvas");
                canvas.width = image.width; canvas.height = image.height;
                canvas.getContext("2d").drawImage(image, 0, 0);
                const png = canvas.toDataURL("image/png").split(",")[1];
                if (png.length > 14 * 1024 * 1024) throw new Error("Use an image smaller than 10 MB after PNG conversion.");
                pendingScreenshot = png;
                await receiver.invokeMethodAsync("ScreenshotReady");
            } finally { image.close(); }
        } catch (error) { await report(error); }
        finally { reading = false; }
    }
    async function onPaste(event) {
        if (!document.querySelector(".screenshot-preview, input[accept^='image/png']")) return;
        const item = [...(event.clipboardData?.items || [])].find(item => item.type.startsWith("image/"));
        if (item) { event.preventDefault(); await normalize(item.getAsFile()); }
    }
    return {
        connect(dotnet) { receiver = dotnet; document.addEventListener("paste", onPaste); },
        disconnect() { document.removeEventListener("paste", onPaste); receiver = null; pendingScreenshot = null; },
        showScreenshot,
        clearScreenshot() { pendingScreenshot = null; showScreenshot(); },
        getScreenshot() {
            if (!pendingScreenshot) throw new Error("Choose or paste the screenshot again before generating FEN.");
            return pendingScreenshot;
        },
        async readFile(input) { if (input.files[0]) await normalize(input.files[0]); },
        async paste() {
            try {
                if (!navigator.clipboard?.read) throw new Error("This browser does not support image clipboard access. Use Choose screenshot instead.");
                const items = await navigator.clipboard.read();
                for (const item of items) {
                    const type = item.types.find(type => type.startsWith("image/"));
                    if (type) { await normalize(await item.getType(type)); return; }
                }
                throw new Error("The clipboard has no image. Copy a screenshot, or use Choose screenshot.");
            } catch (error) { await report(error); }
        }
    };
})();
if ("serviceWorker" in navigator && window.isSecureContext) {
    navigator.serviceWorker.register("/sw.js").catch(error => console.warn("App installation support unavailable.", error));
}
