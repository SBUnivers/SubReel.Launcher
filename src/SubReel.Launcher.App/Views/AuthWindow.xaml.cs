using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;
using SubReel.Launcher.Abstractions.Game;
using SubReel.Launcher.Auth.Supabase;

namespace SubReel.Launcher.App.Views;

public partial class AuthWindow : Window
{
    private readonly ISubReelAuthService _subReelAuth;
    private readonly IGameAuthService _gameAuth;
    private readonly ISupabaseGameProfileService _gameProfileService;
    private readonly ObservableCollection<LauncherAccount> _accounts = [];
    private bool _busy;
    private bool _changingSelection;
    private bool _hasLinkedProfile;
    private bool _platformSignedIn;
    private LicenseUiState _licenseUiState = LicenseUiState.NotChecked;

    private enum LicenseUiState
    {
        NotChecked,
        Checking,
        Verified,
        NotFound,
        OfflineProfile,
        SessionExpired,
        ConnectionProblem,
        Error
    }

    public AuthWindow(ISubReelAuthService subReelAuth, IGameAuthService gameAuth, ISupabaseGameProfileService gameProfileService)
    {
        InitializeComponent();
        _subReelAuth = subReelAuth;
        _gameAuth = gameAuth;
        _gameProfileService = gameProfileService;
        GameAccountBox.ItemsSource = _accounts;
        if (!_subReelAuth.IsConfigured)
            StatusText.Text = "Supabase не настроен. Заполни Platform:SupabaseUrl и Platform:SupabasePublishableKey в appsettings.json.";
        else if (_subReelAuth.CurrentSession is { } session)
            SetPlatformSignedIn(session);
        UpdatePlatformView();
        RefreshGameAccounts();
        SetLicenseState(LicenseUiState.NotChecked);
        Loaded += AuthWindow_Loaded;
    }

    private async void AuthWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_subReelAuth.IsConfigured && _subReelAuth.CurrentSession is null)
        {
            SetBusy(true);
            try
            {
                var result = await _subReelAuth.RestoreSessionAsync();
                if (result.Success && result.Session is { } session) SetPlatformSignedIn(session);
                else if (!string.IsNullOrWhiteSpace(result.Message)) StatusText.Text = result.Message;
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            finally { SetBusy(false); }
        }
        await RefreshLinkedProfileAsync(showStatus: false);
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e) => await RunAuthAsync(() => _subReelAuth.SignInAsync(EmailBox.Text, PasswordBox.Password));
    private async void SignUp_Click(object sender, RoutedEventArgs e) => await RunAuthAsync(() => _subReelAuth.SignUpAsync(EmailBox.Text, PasswordBox.Password));

    private async Task RunAuthAsync(Func<Task<SubReelAuthResult>> operation)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            var result = await operation();
            StatusText.Text = result.Message;
            StatusText.Foreground = result.Success ? System.Windows.Media.Brushes.LightGreen : System.Windows.Media.Brushes.Salmon;
            if (result.Success && result.Session is { } session) SetPlatformSignedIn(session);
            await RefreshLinkedProfileAsync(showStatus: false);
        }
        catch (OperationCanceledException) { StatusText.Text = "Операция отменена."; }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private async void SignOut_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            await _subReelAuth.SignOutAsync();
            StatusText.Text = "Выход из SubReel выполнен. Игровой аккаунт Minecraft не изменён.";
            _platformSignedIn = false;
            UpdatePlatformView();
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            SignOutButton.IsEnabled = false;
            LinkButton.IsEnabled = false;
            _hasLinkedProfile = false;
            UnlinkButton.IsEnabled = false;
            GameProfileText.Text = "Игровой аккаунт не изменён. Войди в SubReel, чтобы управлять привязкой.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; StatusText.Foreground = System.Windows.Media.Brushes.Salmon; }
        finally { SetBusy(false); UpdateAuthButtons(); }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            if (_subReelAuth.CurrentSession is null && _subReelAuth.IsConfigured)
            {
                var restored = await _subReelAuth.RestoreSessionAsync();
                if (restored.Success && restored.Session is { } session) SetPlatformSignedIn(session);
            }
            RefreshGameAccounts();
            await RefreshLinkedProfileAsync(showStatus: true);
            if (string.IsNullOrWhiteSpace(StatusText.Text)) StatusText.Text = "Данные аккаунта обновлены.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; StatusText.Foreground = System.Windows.Media.Brushes.Salmon; }
        finally { SetBusy(false); UpdateAuthButtons(); }
    }

    private async void Microsoft_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            SetLicenseState(LicenseUiState.Checking, "Выполняем вход Microsoft и проверяем профиль Minecraft Java.");
            var session = await _gameAuth.SignInAsync();
            RefreshGameAccounts();
            GameProfileText.Text = $"Текущий профиль: {session.Username}\nUUID: {session.Uuid}";
            SetLicenseState(LicenseUiState.Verified, $"Профиль {session.Username} подтверждён через Minecraft Services.");
            StatusText.Text = "Microsoft-вход выполнен, профиль Java Edition найден. Теперь можно отдельно привязать его к SubReel.";
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            if (ex.Message.Contains("лицензия Minecraft Java", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("не найдена", StringComparison.OrdinalIgnoreCase))
                SetLicenseState(LicenseUiState.NotFound, "На этом Microsoft-аккаунте не обнаружен профиль Java Edition. Проверь, на тот ли аккаунт оформлена покупка.");
            else if (ex is System.Net.Http.HttpRequestException or TimeoutException || ex.InnerException is System.Net.Http.HttpRequestException)
                SetLicenseState(LicenseUiState.ConnectionProblem, "Проблема сети не означает отсутствие лицензии. Проверь соединение и повтори попытку.");
            else
                SetLicenseState(LicenseUiState.Error, "Microsoft-вход или проверка профиля не завершились. Попробуй ещё раз.");
        }
        finally { SetBusy(false); }
    }

    private async void LicenseCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            SetLicenseState(LicenseUiState.Checking);
            if (_gameAuth.ActiveAccount?.Type == LauncherAccountType.Microsoft)
            {
                // Revalidate the active Microsoft token against Minecraft Services.
                var session = await _gameAuth.GetActiveSessionAsync();
                if (session is null) throw new InvalidOperationException("Не удалось получить игровую сессию. Войди через Microsoft заново.");
                RefreshGameAccounts();
                GameProfileText.Text = $"Текущий профиль: {session.Username}\nUUID: {session.Uuid}";
                SetLicenseState(LicenseUiState.Verified, $"Профиль {session.Username} подтверждён через Minecraft Services.");
                StatusText.Text = "Minecraft Services подтвердил доступ к профилю Java Edition.";
                StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            }
            else
            {
                // Interactive Microsoft sign-in also validates the Minecraft Java profile.
                var session = await _gameAuth.SignInAsync();
                RefreshGameAccounts();
                GameProfileText.Text = $"Текущий профиль: {session.Username}\nUUID: {session.Uuid}";
                SetLicenseState(LicenseUiState.Verified, $"Профиль {session.Username} подтверждён через Minecraft Services.");
                StatusText.Text = "Microsoft-вход выполнен, профиль Java Edition найден.";
                StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            }
            await RefreshLinkedProfileAsync(showStatus: false);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            StatusText.Foreground = System.Windows.Media.Brushes.Salmon;
            if (ex.Message.Contains("лицензия Minecraft Java", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("не найдена", StringComparison.OrdinalIgnoreCase))
            {
                SetLicenseState(LicenseUiState.NotFound, "На этом Microsoft-аккаунте не обнаружен профиль Java Edition. Проверь, на тот ли аккаунт оформлена покупка.");
                GameProfileText.Text = "Профиль Java Edition не подтверждён. Офлайн-режим не является лицензией.";
            }
            else if (ex.Message.Contains("сессия Minecraft истекла", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("Войди через Microsoft", StringComparison.OrdinalIgnoreCase))
            {
                SetLicenseState(LicenseUiState.SessionExpired, "Сессия не подтверждена. Войди через Microsoft заново и повтори проверку.");
            }
            else if (ex is System.Net.Http.HttpRequestException or TimeoutException || ex.InnerException is System.Net.Http.HttpRequestException)
            {
                SetLicenseState(LicenseUiState.ConnectionProblem, "Не удалось связаться с Minecraft Services. Это не означает, что лицензия отсутствует.");
            }
            else
            {
                SetLicenseState(LicenseUiState.Error, "Проверка не завершилась. Исправь проблему и попробуй ещё раз.");
            }
        }
        finally { SetBusy(false); }
    }

    private void BuyMinecraft_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.minecraft.net/store/minecraft-java-bedrock-edition-pc") { UseShellExecute = true });
            StatusText.Text = "Открыл официальную страницу Minecraft Java & Bedrock Edition. После покупки войди в тот же Microsoft-аккаунт и проверь лицензию.";
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
        }
        catch (Exception ex) { StatusText.Text = $"Не удалось открыть страницу покупки: {ex.Message}"; }
    }

    private async void Offline_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var username = Microsoft.VisualBasic.Interaction.InputBox("Ник: 3–16 символов, латинские буквы, цифры и _.", "Добавить офлайн-профиль", "Player");
        if (string.IsNullOrWhiteSpace(username)) return;
        SetBusy(true);
        try
        {
            await _gameAuth.AddOfflineAccountAsync(username);
            RefreshGameAccounts();
            StatusText.Text = "Офлайн-профиль добавлен. Он не подтверждает лицензию Java Edition и не может быть привязан как лицензионный профиль.";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private async void GameAccountBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection || GameAccountBox.SelectedItem is not LauncherAccount account || account.Id == _gameAuth.ActiveAccount?.Id) return;
        try
        {
            await _gameAuth.ActivateAccountAsync(account.Id);
            RefreshGameAccounts();
            SetLicenseState(account.Type == LauncherAccountType.Offline ? LicenseUiState.OfflineProfile : LicenseUiState.NotChecked,
                account.Type == LauncherAccountType.Offline
                    ? "Выбран офлайн-профиль. Он не подтверждает лицензию Minecraft Java Edition."
                    : "Аккаунт выбран, но лицензию нужно проверить для этого профиля.");
            await RefreshLinkedProfileAsync(showStatus: false);
        }
        catch (Exception ex) { StatusText.Text = ex.Message; RefreshGameAccounts(); }
    }

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_subReelAuth.CurrentSession is null)
        {
            StatusText.Text = "Сначала войди в аккаунт SubReel на вкладке «Профиль SubReel».";
            StatusText.Foreground = System.Windows.Media.Brushes.Salmon;
            return;
        }
        if (_gameAuth.ActiveAccount?.Type != LauncherAccountType.Microsoft)
        {
            StatusText.Text = "Выбери подтверждённый Microsoft-аккаунт Minecraft Java. Офлайн-профиль привязать нельзя.";
            StatusText.Foreground = System.Windows.Media.Brushes.Salmon;
            return;
        }

        SetBusy(true);
        try
        {
            var existing = await _gameProfileService.GetLinkedProfileAsync();
            var current = _gameAuth.ActiveAccount;
            if (existing is not null && current is not null &&
                !string.Equals(existing.MinecraftUuid, current.Uuid, StringComparison.OrdinalIgnoreCase))
            {
                var replace = MessageBox.Show(
                    $"К этому аккаунту SubReel уже привязан профиль {existing.MinecraftUsername}.\n\nЗаменить его на {current.Username}?",
                    "Заменить привязанный профиль", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (replace != MessageBoxResult.Yes)
                {
                    StatusText.Text = "Привязка не изменена.";
                    return;
                }
            }

            var linked = await _gameProfileService.LinkCurrentMicrosoftProfileAsync();
            GameProfileText.Text = $"Привязан: {linked.MinecraftUsername}\nUUID: {linked.MinecraftUuid}\nСтатус: Microsoft / Minecraft Java";
            StatusText.Text = "Игровой профиль привязан к текущему аккаунту SubReel. Игровые токены не передавались в Supabase.";
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            _hasLinkedProfile = true;
            UnlinkButton.IsEnabled = true;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; StatusText.Foreground = System.Windows.Media.Brushes.Salmon; }
        finally { SetBusy(false); UpdateAuthButtons(); }
    }

    private async void Unlink_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var answer = MessageBox.Show("Отвязать игровой профиль от аккаунта SubReel? Игровой аккаунт Minecraft останется в лаунчере.", "Отвязать профиль", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            await _gameProfileService.UnlinkProfileAsync();
            _hasLinkedProfile = false;
            GameProfileText.Text = "Игровой профиль не привязан.";
            StatusText.Text = "Привязка удалена. Игровой аккаунт Minecraft не изменён.";
            StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
            UnlinkButton.IsEnabled = false;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private async Task RefreshLinkedProfileAsync(bool showStatus)
    {
        if (_subReelAuth.CurrentSession is null)
        {
            _hasLinkedProfile = false;
            LinkButton.IsEnabled = false;
            UnlinkButton.IsEnabled = false;
            return;
        }
        try
        {
            var profile = await _gameProfileService.GetLinkedProfileAsync();
            if (profile is null)
            {
                _hasLinkedProfile = false;
                GameProfileText.Text = "Игровой профиль не привязан к этому аккаунту SubReel.";
                UnlinkButton.IsEnabled = false;
            }
            else
            {
                _hasLinkedProfile = true;
                GameProfileText.Text = $"Привязан: {profile.MinecraftUsername} ({profile.MinecraftUuid})";
                UnlinkButton.IsEnabled = true;
            }
            LinkButton.IsEnabled = _gameAuth.ActiveAccount?.Type == LauncherAccountType.Microsoft;
        }
        catch (Exception ex)
        {
            if (showStatus) StatusText.Text = ex.Message;
            else if (ex.Message.Contains("launcher_game_profiles", StringComparison.OrdinalIgnoreCase))
                StatusText.Text = "Для привязки профиля нужно выполнить SQL из SUPABASE_AUTH_SETUP.md.";
            LinkButton.IsEnabled = _gameAuth.ActiveAccount?.Type == LauncherAccountType.Microsoft;
        }
    }

    private void RefreshGameAccounts()
    {
        _changingSelection = true;
        try
        {
            _accounts.Clear();
            foreach (var account in _gameAuth.Accounts) _accounts.Add(account);
            GameAccountBox.SelectedItem = _accounts.FirstOrDefault(a => a.Id == _gameAuth.ActiveAccount?.Id);
            var active = _gameAuth.ActiveAccount;
            GameProfileText.Text = active is null ? "Игровой профиль не выбран." : active.Type == LauncherAccountType.Microsoft
                ? $"Текущий профиль: {active.Username} · Microsoft" : $"Текущий профиль: {active.Username} · офлайн · без подтверждённой лицензии";
            if (active?.Type == LauncherAccountType.Offline)
                SetLicenseState(LicenseUiState.OfflineProfile, "Выбран офлайн-профиль. Он не подтверждает наличие лицензии Minecraft Java Edition.");
            else if (active is null && _licenseUiState != LicenseUiState.NotChecked)
                SetLicenseState(LicenseUiState.NotChecked);
            LinkButton.IsEnabled = _subReelAuth.CurrentSession is not null && active?.Type == LauncherAccountType.Microsoft;
        }
        finally { _changingSelection = false; }
    }

    private void SetPlatformSignedIn(SubReelAuthSession session)
    {
        _platformSignedIn = true;
        PlatformStatusTitle.Text = "АККАУНТ SUBREEL ПОДКЛЮЧЁН";
        PlatformStatusDetails.Text = $"Вход выполнен\n{session.Email ?? session.UserId}\nСессия активна. Игровой аккаунт Minecraft управляется отдельно.";
        StatusText.Text = $"Выполнен вход в SubReel: {session.Email ?? session.UserId}.";
        StatusText.Foreground = System.Windows.Media.Brushes.LightGreen;
        UpdatePlatformView();
        UpdateAuthButtons();
    }

    private void UpdatePlatformView()
    {
        AuthCredentialsGrid.Visibility = _platformSignedIn ? Visibility.Collapsed : Visibility.Visible;
        AuthHintText.Visibility = _platformSignedIn ? Visibility.Collapsed : Visibility.Visible;
        SignInButton.Visibility = _platformSignedIn ? Visibility.Collapsed : Visibility.Visible;
        SignUpButton.Visibility = _platformSignedIn ? Visibility.Collapsed : Visibility.Visible;
        SignOutButton.Visibility = _platformSignedIn ? Visibility.Visible : Visibility.Collapsed;
        if (!_platformSignedIn)
        {
            PlatformStatusTitle.Text = "СОСТОЯНИЕ АККАУНТА";
            PlatformStatusDetails.Text = "Выполни вход, чтобы привязать игровой профиль.";
        }
    }

    private void UpdateAuthButtons()
    {
        var signedIn = _subReelAuth.CurrentSession is not null;
        _platformSignedIn = signedIn;
        UpdatePlatformView();
        SignOutButton.IsEnabled = !_busy && signedIn;
        LinkButton.IsEnabled = !_busy && signedIn && _gameAuth.ActiveAccount?.Type == LauncherAccountType.Microsoft;
        MicrosoftButton.IsEnabled = !_busy && _gameAuth.IsConfigured;
        LicenseCheckButton.IsEnabled = !_busy && _gameAuth.IsConfigured;
        BuyMinecraftButton.IsEnabled = !_busy;
        OfflineButton.IsEnabled = !_busy;
        UnlinkButton.IsEnabled = !_busy && signedIn && _hasLinkedProfile;
    }


    private void SetLicenseState(LicenseUiState state, string? details = null)
    {
        _licenseUiState = state;
        var (background, border, iconBackground, iconForeground, icon, title, defaultDetails) = state switch
        {
            LicenseUiState.Checking => ("#29263A", "#66568C", "#39304F", "#C7B8FF", "◌", "Проверяем лицензию…", "Запрашиваем Minecraft Services. Это может занять несколько секунд."),
            LicenseUiState.Verified => ("#1D302A", "#397A5D", "#29483B", "#83E5B4", "✓", "Лицензия подтверждена", "Доступ к профилю Minecraft Java Edition подтверждён."),
            LicenseUiState.NotFound => ("#352923", "#936342", "#4A3327", "#FFC18A", "!", "Лицензия не обнаружена", "На этом аккаунте не обнаружен профиль Java Edition."),
            LicenseUiState.OfflineProfile => ("#292A32", "#555866", "#383A45", "#C3C5D0", "○", "Офлайн-профиль", "Офлайн-режим не подтверждает лицензию Minecraft Java Edition."),
            LicenseUiState.SessionExpired => ("#352923", "#936342", "#4A3327", "#FFC18A", "↻", "Нужно войти снова", "Сессия Microsoft не подтверждена."),
            LicenseUiState.ConnectionProblem => ("#292A32", "#666B7C", "#383A45", "#C3C5D0", "⌁", "Нет соединения с сервисом", "Проблема сети не означает отсутствие лицензии."),
            LicenseUiState.Error => ("#302631", "#79536F", "#443142", "#F0B9DE", "!", "Не удалось проверить", "Результат проверки неизвестен."),
            _ => ("#222331", "#48495B", "#343544", "#C5C5D2", "?", "Ещё не проверена", "Войди через Microsoft и проверь доступ к Minecraft Services.")
        };

        LicenseStatusBorder.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(background));
        LicenseStatusBorder.BorderBrush = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(border));
        LicenseStatusIconBackground.Background = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(iconBackground));
        LicenseStatusIcon.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(iconForeground));
        LicenseStatusIcon.Text = icon;
        LicenseStatusTitle.Text = title;
        LicenseStatusTitle.Foreground = new System.Windows.Media.SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(iconForeground));
        LicenseStatusDetails.Text = details ?? defaultDetails;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SignInButton.IsEnabled = !busy;
        SignUpButton.IsEnabled = !busy;
        SignOutButton.IsEnabled = !busy && _subReelAuth.CurrentSession is not null;
        MicrosoftButton.IsEnabled = !busy && _gameAuth.IsConfigured;
        LicenseCheckButton.IsEnabled = !busy && _gameAuth.IsConfigured;
        BuyMinecraftButton.IsEnabled = !busy;
        OfflineButton.IsEnabled = !busy;
        GameAccountBox.IsEnabled = !busy;
        LinkButton.IsEnabled = !busy && _subReelAuth.CurrentSession is not null && _gameAuth.ActiveAccount?.Type == LauncherAccountType.Microsoft;
        UnlinkButton.IsEnabled = !busy && _subReelAuth.CurrentSession is not null && UnlinkButton.IsEnabled;
        EmailBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
        if (!busy) UpdateAuthButtons();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
