# Party Pulse

Party Pulse is a Dalamud plugin foundation for FFXIV venue visitors, staff, and managers.

The current milestone provides:

- public venue discovery by `PULSE-XXXXXX` code;
- public venue discovery from the player's current world, housing district, ward, and plot;
- `/pulse addvenue PULSE-XXXXXX`;
- locally saved multi-venue profiles and venue selection;
- public venue name/address display for visitors without an account;
- optional staff registration on the same saved venue using an invite code;
- account recovery that revokes all other devices for that venue user;
- refresh-token authentication and explicit rotation confirmation;
- access tokens kept in memory only;
- per-device refresh tokens persisted through Dalamud configuration;
- placeholder tabs for VIP, staff, payout, bar, games, and greeter modules.

## Visitor flow

A visitor can add a venue without authentication:

```text
/pulse addvenue PULSE-XXXXXX
```

The settings window also accepts the public code and, while the player is standing on a residential plot, offers an address-based add button. The API returns public venue details only: internal ID, public code, name, and address.

## Staff flow

A saved visitor venue can be upgraded in place. Enter the owner-issued invite code under that venue in Settings. The plugin sends the venue's public code, current character/home world, device name, and invite code. It immediately persists the returned device ID and refresh token before confirming authentication.

Normal startup authentication uses the public venue code and the stored per-device refresh token. If confirmation is lost, the pending token remains recoverable on the next refresh.

## Development

Open `PartyPulse.sln` and build the `PartyPulse` project. The Dalamud SDK packages the output as `latest.zip`.

- `/pulse` opens the main window.
- `/pulse config` opens settings.
- `/pulse addvenue PULSE-XXXXXX` adds a public venue.

Do not commit live refresh tokens or plugin configuration files.
