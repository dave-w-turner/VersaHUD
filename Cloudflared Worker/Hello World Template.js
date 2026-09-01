const FALLBACK_DEFAULT_JSON = "{\"front_v\":0.00,\"front_p\":0,\"background_v\":0.00,\"back_p\":0,\"charging_f\":false,\"charging_b\":false,\"cross_charging\":false,\"system_logs\":[]}";

export default {
  async fetch(request, env, ctx) {
    const url = new URL(request.url);
    const method = request.method.toUpperCase();

    if (url.pathname === "/api/telemetry") {
      if (method === "POST") {
        try {
          const bodyText = (await request.text()).trim();

          if (bodyText.startsWith("{")) {
            await env.VERSAHUB_KV.put("LATEST_VEHICLE_STATE", bodyText);
            
            let pendingCommand = await env.VERSAHUB_KV.get("PENDING_COMMAND");
            if (!pendingCommand) pendingCommand = "NONE";
            
            if (pendingCommand !== "NONE") {
              await env.VERSAHUB_KV.put("PENDING_COMMAND", "NONE");
            }

            return new Response(pendingCommand, { status: 200 });
          } 
          else {
            let activeLiveStateJson = await env.VERSAHUB_KV.get("LATEST_VEHICLE_STATE");
            if (!activeLiveStateJson) activeLiveStateJson = FALLBACK_DEFAULT_JSON;

            return new Response(activeLiveStateJson, {
              status: 200,
              headers: { "Content-Type": "application/json", "Access-Control-Allow-Origin": "*" }
            });
          }
        } 
        catch (err) {
          return new Response("EDGE_TELEMETRY_EXCEPTION: " + err.message, { status: 500 });
        }
      }
    }

    if (url.pathname === "/api/command") {
      if (method === "POST") {
        try {
          const commandPayloadBody = (await request.text()).trim();

          if (commandPayloadBody.length === 0) {
            return new Response("BAD_REQUEST: Empty payload body", { status: 400 });
          }

          await env.VERSAHUB_KV.put("PENDING_COMMAND", commandPayloadBody);
          await env.VERSAHUB_KV.put("LAST_EXECUTED_COMMAND", `${new Date().toISOString()} | ${commandPayloadBody}`);
          
          return new Response(JSON.stringify({ status: "Success" }), {
            status: 200,
            headers: { "Content-Type": "application/json", "Access-Control-Allow-Origin": "*" }
          });
        } 
        catch (err) {
          return new Response("EDGE_COMMAND_EXCEPTION: " + err.message, { status: 500 });
        }
      }
    }

    if (url.pathname === "/api/admin") {
      if (method === "POST") {
        try {
          const rawConfigJsonText = await request.text();
          const parsedConfigData = JSON.parse(rawConfigJsonText.trim());

          if (parsedConfigData.router_ssid) await env.VERSAHUB_KV.put("ROUTER_BRIDGE_SSID", parsedConfigData.router_ssid.trim());
          if (parsedConfigData.wifi_ap)    await env.VERSAHUB_KV.put("WIFI_AP_NAME", parsedConfigData.wifi_ap.trim());
          if (parsedConfigData.ble_name)   await env.VERSAHUB_KV.put("BLE_BROADCAST_NAME", parsedConfigData.ble_name.trim());

          return new Response("TELEMETRY_VAULTS_HYDRATED_SUCCESSFULLY", { status: 200 });
        } 
        catch (err) {
          return new Response("EDGE_ADMIN_WRITE_EXCEPTION: " + err.message, { status: 500 });
        }
      }
      
      if (method === "GET") {
        try {
          const activeHost = env?.CF_ALLOWED_HOST || url.hostname || "silent-bird-d9c0.taigon1984.workers.dev";
          const activeId   = env?.CF_ALLOWED_ID   || "9b28e96698ee489c6a80c96c4e211317.access";

          let activeRouterSsid = await env.VERSAHUB_KV.get("ROUTER_BRIDGE_SSID");
          if (!activeRouterSsid) activeRouterSsid = "NONE"; 

          let activeWifiAp = await env.VERSAHUB_KV.get("WIFI_AP_NAME");
          if (!activeWifiAp) activeWifiAp = "NONE";

          let activeBleName = await env.VERSAHUB_KV.get("BLE_BROADCAST_NAME");
          if (!activeBleName) activeBleName = "NONE";

          const jsonAdminProfile = { wifi_ap: activeWifiAp, ble_name: activeBleName, router_ssid: activeRouterSsid, cf_host: activeHost, cf_id: activeId };
          return new Response(JSON.stringify(jsonAdminProfile), { status: 200, headers: { "Content-Type": "application/json", "Access-Control-Allow-Origin": "*" } });
        } 
        catch (err) {
          return new Response("EDGE_ADMIN_READ_EXCEPTION: " + err.message, { status: 500 });
        }
      }
    }

    if (url.pathname === "/api/status") {
      return new Response(JSON.stringify({ status: "Ready" }), { status: 200, headers: { "Content-Type": "application/json" } });
    }

    return new Response("VersaHUD Cloud telemetry edge proxy engine live.", { status: 200 });
  }
};
