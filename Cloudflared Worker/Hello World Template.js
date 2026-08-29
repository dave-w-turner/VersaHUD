export default {
    async fetch(request, env, ctx) {
        const url = new URL(request.url);

        if (url.pathname === "/api/telemetry" || url.pathname === "/api/command") {
            return env.MY_HARDWARE_WEBSOCKET.fetch(request);
        }

        return new Response("VersaHUD Cloudflare Zero-Trust Node Online.", { status: 200 });
    },
};