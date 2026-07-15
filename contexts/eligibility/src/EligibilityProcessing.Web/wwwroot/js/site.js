// Please see documentation at https://learn.microsoft.com/aspnet/core/client-side/bundling-and-minification
// for details on configuring this project to bundle and minify static web assets.

// Reusable export download helper. Fetches `url` (sends the auth cookie),
// and on success triggers a browser download of the response body, preferring
// the server's Content-Disposition filename and falling back to `fallbackName`.
// Throws on a non-OK response (callers surface the message in their own UI), so
// it can back any "Export" button — not just the audit trail.
window.downloadFile = async function (url, fallbackName) {
    const resp = await fetch(url, { headers: { "X-Requested-With": "XMLHttpRequest" } });
    if (!resp.ok) {
        let msg = "Export failed (" + resp.status + ")";
        try { const j = await resp.json(); if (j && j.error) msg = j.error; } catch (e) { /* non-JSON body */ }
        throw new Error(msg);
    }

    let name = fallbackName || "download";
    const cd = resp.headers.get("Content-Disposition");
    if (cd) {
        const m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(cd);
        if (m && m[1]) {
            try { name = decodeURIComponent(m[1]); } catch (e) { name = m[1]; }
        }
    }

    const blob = await resp.blob();
    const objectUrl = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = objectUrl;
    a.download = name;
    document.body.appendChild(a);
    a.click();
    a.remove();
    URL.revokeObjectURL(objectUrl);
};
