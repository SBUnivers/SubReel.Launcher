# SubReel Account Center UI patch

## Included
- `src/SubReel.Launcher.App/Views/AuthWindow.xaml` — redesigned Account Center with separate tabs for SubReel and Minecraft Java.
- `src/SubReel.Launcher.App/Views/AuthWindow.xaml.cs` — safer linking flow, refresh action, and tracked linked-profile state.

## Apply
1. Back up the current project.
2. Extract this ZIP into the solution root (the folder containing `SubReel.Launcher.slnx`), preserving paths and replacing the two files.
3. Build:

```powershell
dotnet build .\SubReel.Launcher.slnx
```

4. Launch the app and open the profile button.
5. Test: sign in to SubReel, sign in to Microsoft, select the Microsoft game profile, link it, close/reopen Account Center, refresh, then unlink. Verify offline accounts cannot be linked.

## What changed in linking
- The user must already be signed in to SubReel.
- Only the active Microsoft account can be linked; offline profiles are rejected.
- The service revalidates the active Minecraft session before writing metadata.
- If a different Minecraft profile is already linked to this SubReel account, the UI asks before replacing it.
- Unlinking only deletes the SubReel-to-Minecraft metadata relation; it does not remove the Microsoft account from the launcher.
- Only Minecraft UUID and username are persisted in Supabase; never send Microsoft/Minecraft bearer or refresh tokens.

## Configuration reminder
Since environment variables were required to make the current run work, configure them permanently for your launch method or fill `Platform:SupabaseUrl` and `Platform:SupabasePublishableKey` in the app's configuration. For environment variables, names use double underscores:
- `Platform__SupabaseUrl`
- `Platform__SupabasePublishableKey`

Use only the public publishable/anon key in a desktop app. Never put `service_role` or `sb_secret_...` keys into the launcher.
