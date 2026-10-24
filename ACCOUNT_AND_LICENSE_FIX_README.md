# SubReel Account & Minecraft Java License UX fix

This patch contains:
- `src/SubReel.Launcher.App/Views/AuthWindow.xaml`
- `src/SubReel.Launcher.App/Views/AuthWindow.xaml.cs`
- `src/SubReel.Launcher.Auth/MicrosoftGameAuthService.cs`

## What it changes
- After Supabase sign-in or session restoration, the email/password form and sign-in/sign-up buttons are hidden. The account email and active session state are shown with a sign-out button.
- Signing out returns the SubReel tab to the sign-in form without signing out Minecraft.
- Adds **Проверить лицензию**. It revalidates the active Microsoft session against Minecraft Services. If no Microsoft account is active, it starts Microsoft sign-in, which also validates the Minecraft Java profile.
- Adds **Купить Minecraft Java**, opening the official Minecraft Java & Bedrock Edition PC store page in the default browser.
- Preserves the explicit no-license response from Minecraft Services instead of hiding it inside a generic session error.

## Apply
1. Back up the current solution.
2. Extract this ZIP into the solution root (same folder as `SubReel.Launcher.slnx`), preserving paths and replacing files.
3. Build and check exit code:

```powershell
dotnet build .\SubReel.Launcher.slnx
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
```

## Important
The launcher cannot create or grant a Minecraft license. The purchase must be made through the official Minecraft store and associated with the Microsoft account used in the launcher. The license check only verifies the signed-in account against Minecraft Services. An offline profile is not a licensed profile.
