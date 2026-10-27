# SubReel Launcher — Supabase Auth starter

This patch adds Supabase email/password sign-in, sign-up, encrypted local session persistence using Windows DPAPI, session restoration/refresh, local + remote sign-out, and a dedicated WPF account window opened by clicking the profile button.

## Configuration

In `src/SubReel.Launcher.App/appsettings.json`, set:
- `Platform:SupabaseUrl` to the Supabase project URL (HTTPS, e.g. `https://YOUR_PROJECT.supabase.co`)
- `Platform:SupabasePublishableKey` to the project's publishable/anon key. Never use `service_role` in the launcher.

In Supabase Auth settings, configure email authentication and decide whether email confirmation is required. If confirmation is enabled, sign-up returns an informational message and the user must confirm their email before signing in.

## Build and verify on Windows with .NET 10 SDK

From the solution root (the directory containing `SubReel.Launcher.slnx`):

```powershell
dotnet restore .\SubReel.Launcher.slnx
dotnet build .\SubReel.Launcher.slnx --no-restore
```

Then run the launcher and test: unconfigured Supabase message; valid/invalid login; sign-up with confirmation enabled and disabled; close/reopen to restore session; sign-out; network loss; expired/revoked refresh token.

The current execution environment does not have the `dotnet` CLI installed, so a real compile/build could not be run here. XML parsing and source integration checks passed; please run the commands above on the Windows development machine before treating this patch as build-verified.

## Scope and limitations

- This is Supabase platform-account auth only. It does not replace Microsoft/Xbox/Minecraft Java authentication.
- No new NuGet packages were added; the auth service uses existing `Microsoft.Extensions.Http` and `System.Security.Cryptography.ProtectedData` references.
- The saved session file is stored under the launcher's existing per-user Auth directory and encrypted for the current Windows user via DPAPI.
- Supabase user profiles/RLS tables are not required for basic email/password Auth. Add them only when the platform needs profile data.

## Привязка игрового профиля Minecraft к аккаунту SubReel

Supabase-аккаунт и Minecraft-аккаунт остаются независимыми. Привязка сохраняет только UUID и имя игрового профиля Minecraft; access/refresh-токены Microsoft, Xbox и Minecraft в Supabase не отправляются.

Схема и политики RLS теперь поддерживаются в Platform как `supabase/sql/032_launcher_game_profiles.sql`. Прежде чем применять миграцию, проверь существующие таблицы и историю проекта: таблица могла быть создана ранее вручную по старой версии этой инструкции. Применяй SQL через Supabase SQL Editor только после проверки, чтобы не переписывать удалённую миграционную историю.

### Поведение

- Кнопка профиля открывает единое окно, где отдельно управляются аккаунт SubReel и игровой аккаунт.
- Microsoft-вход продолжает использовать существующий `IGameAuthService`.
- Привязать можно только активный аккаунт типа `Microsoft`; офлайн-профиль не считается подтверждённым лицензионным аккаунтом.
- Выход из SubReel не выполняет выход из Microsoft и не удаляет игровой профиль из локального лаунчера.
- Отвязка удаляет только строку привязки в Supabase; она не удаляет Microsoft-аккаунт из лаунчера.
- SQL использует `auth.uid()` для ограничения каждой операции строкой текущего пользователя. Не добавляй `service_role` в приложение.
