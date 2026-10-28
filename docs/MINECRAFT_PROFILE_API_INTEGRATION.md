# Minecraft profile API integration

The launcher now uses the SubReel website API instead of writing directly to `launcher_game_profiles`.

## API contract

`Platform:ApiBaseUrl` must point to the launcher API prefix, for example `https://www.subreel.ru/api/launcher/`.

- `GET minecraft-profiles` returns `{ "profiles": [{ "minecraftUuid", "minecraftUsername", "linkedAt" }] }`.
- `POST minecraft-profiles` accepts `{ "minecraftAccessToken": "..." }`. The website validates the token against Minecraft Services and stores only the verified UUID/name.
- `DELETE minecraft-profiles/{minecraftUuid}` removes only the selected profile owned by the authenticated SubReel user.

The SubReel access token is sent as a Bearer token to the website API. The Minecraft Services access token is sent only in the POST body to that API for server-side verification; it must not be logged or persisted.

## Local verification

On Windows with the .NET 10 SDK installed:

```powershell
dotnet restore SubReel.Launcher.slnx
dotnet build SubReel.Launcher.slnx --configuration Release --no-restore -m:1
dotnet test SubReel.Launcher.slnx --configuration Release --no-build --no-restore -m:1
```

Then test with a non-production account:
1. Sign in to SubReel in the launcher.
2. Sign in to a licensed Minecraft Java account.
3. Link it and verify it appears in the linked-profile list.
4. Link a second licensed account and verify both remain listed.
5. Select one linked profile and unlink it; verify the other remains linked.
6. Try linking the same Minecraft profile to another SubReel ID and verify the API returns a safe conflict.
7. Sign out of SubReel and verify profile-management controls are unavailable.

Do not apply pending production SQL migrations as part of this client-side change. The website API must be deployed and the required database migrations reviewed/applied by an authorized operator before end-to-end testing can pass.
