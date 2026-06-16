# Party Pulse

Party Pulse is a Dalamud plugin foundation for FFXIV venue guests, staff, and managers.

The current milestone provides:

- multi-venue connection profiles;
- venue selection in the main window;
- character and home-world discovery through Dalamud;
- first-device registration with a one-time invite code;
- account recovery that revokes all other devices for that venue user;
- refresh-token authentication against the deployed PartyPulse API;
- explicit authentication confirmation after the refresh token is safely persisted;
- access tokens kept in memory only;
- per-device refresh tokens persisted through Dalamud configuration;
- serialized authentication requests per venue/device;
- scalable API, authentication, model, service, and feature-tab structure;
- placeholder tabs for VIP, staff, payout, bar, games, and greeter modules.

## Authentication flow

A new venue profile starts with a venue ID and device name. The user enters either:

- an invite code for first-time registration; or
- a recovery code to replace all prior devices.

The plugin reads the current character and home world from the game, sends the code to the API, immediately persists the returned device ID and refresh token, then confirms receipt with the short-lived access token. Normal startup authentication uses:

```text
POST /api/v1/auth/refresh
```

The plugin immediately saves each newly returned refresh token before confirming its rotation. If the confirmation request is lost, the pending token remains recoverable on the next refresh.

## Development

Open `PartyPulse.sln` and build the `PartyPulse` project. The Dalamud SDK packages the output as `latest.zip`.

Use `/pulse` to open the main window and `/pulse config` to open connection settings.

Do not commit live refresh tokens or plugin configuration files.
