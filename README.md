# Party Pulse

Party Pulse is a Dalamud plugin foundation for FFXIV venue guests, staff, and managers.

The current milestone provides:

- multi-venue connection profiles;
- venue selection in the main window;
- character and home-world discovery through Dalamud;
- refresh-token authentication against the deployed PartyPulse API;
- access tokens kept in memory only;
- per-device refresh tokens persisted through Dalamud configuration;
- serialized refresh requests per venue/device;
- scalable API, authentication, model, service, and feature-tab structure;
- placeholder tabs for VIP, staff, payout, bar, games, and greeter modules.

## Current API contract

The plugin authenticates through:

```text
POST /api/v1/auth/refresh
```

It sends the configured venue ID, device ID, persisted refresh token, and the current character name/home world read from the game. On success it immediately saves the newly returned refresh token and keeps the short-lived JWT access token in memory.

The API currently carries `refresh_rotation_id` in the JWT, but the API/SQL confirmation endpoint is not implemented yet. The plugin therefore does not invent a confirmation call. Future authenticated feature requests can use `AuthenticationManager.EnsureAccessTokenAsync` and `PartyPulseApiClient.SendAuthorizedAsync` once the corresponding API endpoints exist.

## Development

Open `PartyPulse.sln` and build the `PartyPulse` project. The Dalamud SDK packages the output as `latest.zip`.

Use `/pulse` to open the main window and `/pulse config` to open connection settings.

Do not commit live refresh tokens or plugin configuration files.
