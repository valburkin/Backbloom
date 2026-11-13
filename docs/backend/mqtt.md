# MQTT integration

JSON-RPC over MQTT, an availability topic and Home Assistant discovery. This is a port of HyperHDR's `sources/mqtt` on
top of the shared `MqttConnection` (MQTTnet), in `Backbloom.Server/Mqtt`. The same connection serves the Zigbee2MQTT
LED driver.

| Part | Behavior |
|---|---|
| Connection | The `mqtt` section (Settings → Integrations): `enable`, `host`, `port`, `username`, `password`, `is_ssl`, `ignore_ssl_errors`, `maxRetry` (5 s apart, then it gives up; HyperHDR: 10 s). Reconnects when the section changes; the first attempt runs in the background so an unreachable broker doesn't delay the start. |
| JSON-RPC | Requests on `<topic>/JsonAPI` (`custom_topic`, default `Backbloom`; HyperHDR used `HyperHDR`): one request object or an array of them, run with the rights of a local client (HyperHDR's `localConnection`; configuration access excluded, see [Access](access-and-first-run.md#access-this-computer-and-other-devices)). Every message the session produced is published as one JSON array on `<topic>/JsonAPI/response` (QoS 2). Unreadable JSON gets `{ success: false, error }`. `disableApiAccess` doesn't subscribe at all (as in HyperHDR). |
| Availability (Backbloom addition) | `<topic>/status`: `online` (retained) after every connect, `offline` as the connection's last will and published before a clean disconnect. |
| Home Assistant (Backbloom addition) | With `homeAssistantDiscovery` (a Backbloom setting, default on): a retained discovery config on `homeassistant/light/backbloom_<id>/light/config` (JSON schema, RGB, brightness, effect list "Screen sync" plus every effect and music effect, device = `general.name`), commands on `<topic>/light/set` (`OFF` -> `ALL` off; `ON` -> on; a color -> static color at priority 1 scaled by the brightness; effect "Screen sync" -> colors cleared, capture on; any other effect -> runs at priority 1), and the retained state on `<topic>/light/state`, published on every change from any client. Switching discovery off removes the entity (empty retained config). |
