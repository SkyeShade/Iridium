# Node media deployment (LiveKit SFU)

Production Iridium media uses a self-hosted LiveKit SFU. SignalR remains the authenticated control plane for calls, voice-channel membership, mute/deafen metadata, speaking state, and screen publication/watch intent. Audio and video travel only over WebRTC between each browser and LiveKit; neither SignalR nor ASP.NET request bodies carry media.

The former direct-call and Community peer-mesh implementations remain source-only Development rollout aids. Production dependency injection registers the LiveKit adapters and rejects direct peer signaling; there is no silent P2P fallback.

## Iridium.Server configuration

Keep these values in external production configuration (for example `/opt/iridium/config/appsettings.Production.json` or environment variables), never in source control:

```json
"Media": {
  "Provider": "LiveKit",
  "PublicUrl": "wss://media.example.net",
  "ApiKey": "replace-with-livekit-api-key",
  "ApiSecret": "replace-with-livekit-api-secret",
  "JoinTokenLifetimeSeconds": 300,
  "Voice": {
    "Bitrate": 96000
  },
  "RingTimeoutSeconds": 30,
  "SignalingLossTimeoutSeconds": 45
}
```

Equivalent environment variables are `Media__Provider`, `Media__PublicUrl`, `Media__ApiKey`, `Media__ApiSecret`, `Media__JoinTokenLifetimeSeconds`, and `Media__Voice__Bitrate`. Voice bitrate defaults to 96000 bps and is clamped to 64000–128000 bps. The API key and secret must match LiveKit's `keys` configuration. `ApiSecret` stays server-side. Iridium issues short-lived HS256 participant JWTs whose subject is the stable AccountId and whose video grant fixes the room name plus publish/subscribe permissions.

Set `Provider` to `Disabled` to run a text-only Node. `/api/server-info` then reports voice and screen sharing disabled, and authenticated media requests fail cleanly. Invalid LiveKit configuration is logged clearly without printing secrets, and does not prevent text chat from starting.

## Rooms and tracks

- DM call room: `iridium-direct-{CallId}`. Media access is issued only once the call is accepted and only to the connection selected for that participant.
- Server voice room: `iridium-community-{CommunityId}-voice-{ChannelId}`. Iridium's existing channel permission and joined-room checks run before a token is issued.
- Participant identity: AccountId (`N` format), never DisplayName.
- Microphones subscribe automatically. Screen video/audio are publications with an Iridium stream key and subscribe only while the existing Watch intent is active.

LiveKit handles reconnect and the browser-to-SFU ICE transport. Iridium continues to enforce one active DM/Server voice session, and its normal disconnect cleanup removes call/voice/stream control state. Per-user 10–300% volume, local mute, and deafen still use the browser Web Audio gain graph.

## VPS deployment

Use a separate hostname such as `media.example.net`; no Iridium domain is hardcoded. The templates in `deploy/livekit` run LiveKit beside Iridium with host networking and read `/opt/iridium/config/livekit.yaml` read-only. Generate long random API key/secret values and put the same pair in the external Iridium config.

1. Create the `media` DNS A/AAAA records for the VPS.
2. Prefer LiveKit's official `livekit/generate` VM configuration (it includes Caddy and TURN/TLS). For the smaller repository template, copy `deploy/livekit/livekit.example.yaml` to `/opt/iridium/config/livekit.yaml`, replace its example key/secret/domain, place the hostname's trusted `fullchain.pem` and `privkey.pem` under `/opt/iridium/config/livekit-certs`, and restrict permissions.
3. Copy the Compose template to `/opt/iridium/livekit/docker-compose.yml`; run `docker compose up -d`. Docker's `restart: unless-stopped` supplies restart-on-failure behavior; use `docker compose logs -f livekit` for logs.
4. Terminate TLS for LiveKit's WebSocket/API endpoint (port 7880 internally) at `media.example.net`, and configure `Media:PublicUrl` as `wss://media.example.net`.
5. Open the network paths recommended by LiveKit: TCP 7881 for ICE/TCP; UDP 50000–60000 for WebRTC media (or configure LiveKit's UDP mux port instead); and, when embedded TURN is enabled, UDP 3478 plus TURN/TLS 5349. Also allow the normal HTTPS/WSS listener. Keep authorization requirements intact at the proxy.
6. Verify first with two genuinely remote networks, then exercise DM audio, Server voice, screen publish/watch/stop, reconnect, account switching, and cleanup.

Actual media bytes are carried and relayed by LiveKit. LiveKit should be capacity-monitored and upgraded independently from Iridium.Server. The pinned browser SDK and server image should be upgraded together after staging verification.

## Safe diagnostics

Development builds log provider/room-kind and LiveKit connection/reconnect state without logging join tokens, API secrets, SDP, candidate addresses, or authentication tokens. The detailed candidate-type/selected-pair diagnostics remain only in the legacy Development P2P adapters; LiveKit owns its internal peer connection in production and its service/browser logs are the authoritative transport diagnostics.

After live deployment succeeds, the removable Phase 2 code is `WebRtcCallMediaService`, `BrowserCommunityVoiceMediaClient`, `voiceCall.js`, `communityVoiceMedia.js`, the peer SDP/ICE hub methods/contracts, and coturn REST configuration used only by direct P2P. Do that cleanup separately after rollout so it cannot obscure transport migration issues.
