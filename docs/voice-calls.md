# Voice call deployment

Iridium's first voice-call transport is direct, one-to-one WebRTC. `Iridium.Server` authenticates participants, owns transient call lifecycle, and forwards SDP and ICE signaling; it never receives, records, or stores microphone media. The normal deployment remains the single `Iridium.Server` process.

## Network requirements

- Serve the web client over HTTPS in production. Browsers require a secure context for microphone capture (localhost is the development exception).
- Clients need HTTPS/WebSocket access to the Iridium Node for call control and signaling.
- Clients need outbound access to configured STUN/TURN services and to the peer-selected WebRTC UDP/TCP candidates.
- Direct WebRTC does not require an Iridium media port on the Node. STUN alone will not connect every NAT/firewall combination, so production operators should configure a TURN relay for reliable calls.
- A self-hosted TURN service needs its public listening port and configured relay UDP range allowed by the VPS firewall/security group. Its advertised public address must be reachable by both clients. Those ports belong to the TURN deployment, not `Iridium.Server`.
- No consumer-router port-forward or UPnP automation is included.

Configure provider-neutral ICE entries under `Media:IceServers`. An entry supports `Urls`, `Username`, and `Credential`; `Urls` may contain STUN and/or TURN URLs. Configured credentials are returned only to an authenticated participant in a live call. Do not place unrelated secrets in this section. Prefer short-lived TURN credentials when a future credential issuer is integrated.

`Media:Mode` is currently `DirectWebRtc`. `NodeSfu` is reserved for a future media service; selecting it now is rejected cleanly by the client. An SFU deployment will additionally need published media listener/relay ports, TLS, capacity planning, and a service-to-Node authorization scheme.

Ringing expiry is controlled by `Media:RingTimeoutSeconds`. Active clients send lightweight signaling heartbeats; `Media:SignalingLossTimeoutSeconds` controls how long server call state survives signaling loss while already-established peer audio is allowed to continue.
