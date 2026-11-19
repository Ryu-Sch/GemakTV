using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using AndroidX.Media3.UI;
using GemakTV.Application.Tv;
using GemakTV.Domain;
using GemakTV.Domain.Playback;
using GemakTV.Tv.Playback;
using GemakTV.Tv.Updates;

namespace GemakTV.Tv;

/// <summary>
/// The only screen. The remote does just a few things:
/// ▲/▼ (or CH+/CH−) change channel, OK opens the menu, BACK closes whatever is open, number keys jump to a
/// channel, and volume/power behave as usual. Everything else is ignored, and nothing leaves the app.
/// </summary>
[Activity(
    Name = "io.github.ryusch.gemaktv.MainActivity",
    Label = "GemakTV",
    Exported = true,
    LaunchMode = LaunchMode.SingleTask,
    ClearTaskOnLaunch = true,
    StateNotNeeded = true,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden | ConfigChanges.Navigation | ConfigChanges.UiMode
        | ConfigChanges.Locale | ConfigChanges.FontScale | ConfigChanges.Density)]
[IntentFilter(new[] { Intent.ActionMain }, Categories = new[] { Intent.CategoryLauncher, Intent.CategoryLeanbackLauncher })]
[IntentFilter(new[] { Intent.ActionMain }, Categories = new[] { Intent.CategoryHome, Intent.CategoryDefault })]
public sealed class MainActivity : Activity
{
    private const string Tag = "GemakTV";
    private static readonly TimeSpan BannerTime = TimeSpan.FromSeconds(6);
    private static readonly Keycode[] MaintenanceCode =
        [Keycode.DpadUp, Keycode.DpadUp, Keycode.DpadDown, Keycode.DpadDown, Keycode.DpadLeft, Keycode.DpadRight, Keycode.DpadLeft, Keycode.DpadRight];

    private readonly Handler _handler = new(Looper.MainLooper!);
    private readonly List<Keycode> _recentKeys = [];
    private ChannelNavigator _navigator = new(Lineup.Empty);
    private PlayerController? _player;
    private bool _initialized;
    private bool _busy;
    private int _bannerToken;
    private int _installToken;
    private bool _awaitingInstallConfirmation;
    private string _digits = "";
    private int _digitsToken;
    private View? _focusBeforeDialog;

    private PlayerView _playerView = null!;
    private View _statusCard = null!, _statusSpinner = null!;
    private ImageView _statusIcon = null!;
    private TextView _statusTitle = null!, _statusDetail = null!;
    private View _banner = null!;
    private ImageView _bannerLogo = null!;
    private TextView _bannerLetter = null!, _bannerNumber = null!, _bannerName = null!, _bannerClock = null!;
    private View _menu = null!;
    private ScrollView _channelScroll = null!;
    private LinearLayout _channelList = null!;
    private TextView _menuClock = null!, _menuDate = null!, _versionText = null!;
    private View _checkChannelsButton = null!, _checkUpdatesButton = null!;
    private View _dialog = null!, _dialogSpinner = null!;
    private ImageView _dialogIcon = null!;
    private TextView _dialogTitle = null!, _dialogMessage = null!, _dialogButton = null!;
    private ProgressBar _dialogProgress = null!;
    private View _maintenance = null!;
    private TextView _maintenanceInfo = null!;

    private static AppServices Services => TvApplication.Services;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);
        BindViews();
        HideSystemUi();

        InstallResultReceiver.Finished += OnInstallFinished;
        Services.Kiosk.ApplyPolicies();
        Task.Run(() =>
        {
            CleanOldDownloads();
            var trimmed = Services.Kiosk.TrimBackgroundApps();
            Log.Info(Tag, $"Asked {trimmed} other apps to stop in the background");
        });
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            var lineup = await Services.LoadLineupAsync();
            _navigator = new ChannelNavigator(lineup, Services.Prefs.LastChannelId);
            Services.Logos.Prefetch(lineup);
            _initialized = true;
            if (_player is not null) PlayCurrent(showBanner: true);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"Could not load the channel list: {ex}");
        }
    }

    protected override void OnStart()
    {
        base.OnStart();
        _player = new PlayerController(this, _playerView, Services.Prefs);
        _player.StatusChanged += OnPlaybackStatus;
        if (_initialized) PlayCurrent(showBanner: true);
    }

    protected override void OnResume()
    {
        base.OnResume();
        HideSystemUi();
        Services.Kiosk.EnterLockTask(this);
        if (_awaitingInstallConfirmation)
        {
            // Back from Android's install screen without the app being replaced: it was cancelled.
            // (Android 6 sends no result in that case.)
            _awaitingInstallConfirmation = false;
            _installToken++;
            ShowDialog(DialogKind.Error, GetString(Resource.String.update_failed), GetString(Resource.String.update_cancelled_detail));
        }
    }

    protected override void OnStop()
    {
        // Free the decoder and buffers whenever the TV is not showing (screen off, settings open).
        if (_player is not null)
        {
            _player.StatusChanged -= OnPlaybackStatus;
            _player.Dispose();
            _player = null;
        }
        base.OnStop();
    }

    protected override void OnDestroy()
    {
        InstallResultReceiver.Finished -= OnInstallFinished;
        _handler.RemoveCallbacksAndMessages(null);
        base.OnDestroy();
    }

    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus) HideSystemUi();
    }

#pragma warning disable CS0672, CA1422 // never leave the app
    public override void OnBackPressed() { }
#pragma warning restore CS0672, CA1422

    // --- Remote control ---------------------------------------------------------------------------------

    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        if (e is null) return base.DispatchKeyEvent(e);
        var key = e.KeyCode;
        var down = e.Action == KeyEventActions.Down;
        var up = e.Action == KeyEventActions.Up;

        // Volume, mute and power always work as normal.
        if (key is Keycode.VolumeUp or Keycode.VolumeDown or Keycode.VolumeMute or Keycode.Mute or Keycode.Power or Keycode.Sleep or Keycode.Wakeup)
            return base.DispatchKeyEvent(e);

        var isBack = key is Keycode.Back or Keycode.Escape;
        var isNavigation = key is Keycode.DpadUp or Keycode.DpadDown or Keycode.DpadLeft or Keycode.DpadRight
            or Keycode.DpadCenter or Keycode.Enter or Keycode.NumpadEnter or Keycode.ButtonA;

        if (IsVisible(_maintenance) || IsVisible(_dialog) || IsVisible(_menu))
        {
            if (isBack || (key == Keycode.Menu && IsVisible(_menu)))
            {
                if (up) CloseTopOverlay();
                return true;
            }
            var menuOnly = IsVisible(_menu) && !IsVisible(_dialog) && !IsVisible(_maintenance);
            if (down && e.RepeatCount == 0 && menuOnly) TrackSecretCode(key);
            if (IsVisible(_dialog) && _dialogButton.Visibility != ViewStates.Visible) return true; // busy: wait
            // ◀ ▶ always jump between the channel column and the button column, wherever focus is.
            if (menuOnly && key is Keycode.DpadRight or Keycode.DpadLeft)
            {
                if (down) SwitchMenuColumn(toButtons: key == Keycode.DpadRight);
                return true;
            }
            return isNavigation ? base.DispatchKeyEvent(e) : true;
        }

        // Watching TV.
        switch (key)
        {
            case Keycode.DpadUp or Keycode.ChannelUp or Keycode.PageUp:
                if (down && e.RepeatCount % 3 == 0) Zap(+1);
                return true;
            case Keycode.DpadDown or Keycode.ChannelDown or Keycode.PageDown:
                if (down && e.RepeatCount % 3 == 0) Zap(-1);
                return true;
            case Keycode.DpadCenter or Keycode.Enter or Keycode.NumpadEnter or Keycode.ButtonA or Keycode.Menu:
                if (up) ShowMenu();
                return true;
            case >= Keycode.Num0 and <= Keycode.Num9:
                if (down) EnterDigit(key - Keycode.Num0);
                return true;
            case >= Keycode.Numpad0 and <= Keycode.Numpad9:
                if (down) EnterDigit(key - Keycode.Numpad0);
                return true;
            default:
                // BACK, INFO, left/right and anything else: just show what is on.
                if (up && (isBack || key is Keycode.Info or Keycode.Guide or Keycode.DpadLeft or Keycode.DpadRight)) ShowBanner();
                return true;
        }
    }

    private void Zap(int direction)
    {
        var channel = direction > 0 ? _navigator.Next() : _navigator.Previous();
        if (channel is not null) PlayCurrent(showBanner: true);
    }

    private void EnterDigit(int digit)
    {
        _digits += digit;
        var token = ++_digitsToken;
        var number = int.Parse(_digits);
        // Commit at once when no longer number could exist, otherwise wait briefly for a second digit.
        if (_digits.Length >= 2 || number * 10 > _navigator.Lineup.Count) CommitDigits();
        else _handler.PostDelayed(() => { if (token == _digitsToken) CommitDigits(); }, 1500);
    }

    private void CommitDigits()
    {
        var number = int.TryParse(_digits, out var n) ? n : 0;
        _digits = "";
        if (_navigator.GoTo(number) is not null) PlayCurrent(showBanner: true);
        else ShowBanner();
    }

    private void TrackSecretCode(Keycode key)
    {
        _recentKeys.Add(key);
        if (_recentKeys.Count > MaintenanceCode.Length) _recentKeys.RemoveAt(0);
        if (_recentKeys.SequenceEqual(MaintenanceCode))
        {
            _recentKeys.Clear();
            ShowMaintenance();
        }
    }

    // --- Playback ---------------------------------------------------------------------------------------

    private void PlayCurrent(bool showBanner)
    {
        if (_player is null) return;
        var channel = _navigator.Current;
        if (channel is null)
        {
            _player.Stop();
            ShowStatus(Resource.Drawable.ic_tv, GetString(Resource.String.no_channels_title), GetString(Resource.String.no_channels_detail));
            return;
        }
        Services.Prefs.LastChannelId = channel.Id;
        _player.Play(channel);
        if (showBanner) ShowBanner();
    }

    private void OnPlaybackStatus(PlaybackStatus status)
    {
        RunOnUiThread(() =>
        {
            if (IsVisible(_menu)) return; // the menu covers it; HideMenu re-applies the latest status
            var name = status.Channel?.Name ?? "";
            switch (status.Phase)
            {
                case PlaybackPhase.Tuning:
                    ShowStatus(null, GetString(Resource.String.tuning, name),
                        status.Attempt > 1 ? GetString(Resource.String.tuning_alternative, status.Attempt, status.Total) : "");
                    break;
                case PlaybackPhase.Playing:
                case PlaybackPhase.Idle:
                    _statusCard.Visibility = ViewStates.Gone;
                    break;
                case PlaybackPhase.Unavailable:
                    ShowStatus(Resource.Drawable.ic_error, GetString(Resource.String.unavailable_title, name), GetString(Resource.String.unavailable_detail));
                    break;
                case PlaybackPhase.NotSetUp:
                    ShowStatus(Resource.Drawable.ic_tv, GetString(Resource.String.not_set_up_title, name), GetString(Resource.String.not_set_up_detail));
                    break;
                case PlaybackPhase.NoInternet:
                    ShowStatus(Resource.Drawable.ic_wifi_off, GetString(Resource.String.no_internet_title), GetString(Resource.String.no_internet_detail));
                    break;
            }
        });
    }

    private void ShowStatus(int? icon, string title, string detail)
    {
        _statusSpinner.Visibility = icon is null ? ViewStates.Visible : ViewStates.Gone;
        _statusIcon.Visibility = icon is null ? ViewStates.Gone : ViewStates.Visible;
        if (icon is { } id) _statusIcon.SetImageResource(id);
        _statusTitle.Text = title;
        _statusDetail.Text = detail;
        _statusDetail.Visibility = string.IsNullOrEmpty(detail) ? ViewStates.Gone : ViewStates.Visible;
        _statusCard.Visibility = ViewStates.Visible;
    }

    // --- Channel banner -------------------------------------------------------------------------------

    private void ShowBanner()
    {
        if (_navigator.Current is not { } channel || IsVisible(_menu)) return;
        _bannerNumber.Text = GetString(Resource.String.channel_number, _navigator.CurrentNumber);
        _bannerName.Text = channel.Name;
        _bannerClock.Text = DateTime.Now.ToString("HH:mm");
        Services.Logos.Bind(_bannerLogo, _bannerLetter, channel);
        if (!IsVisible(_banner))
        {
            _banner.Alpha = 0f;
            _banner.Visibility = ViewStates.Visible;
            _banner.Animate()!.Alpha(1f).SetDuration(200);
        }
        var token = ++_bannerToken;
        _handler.PostDelayed(() =>
        {
            if (token != _bannerToken) return;
            _banner.Animate()!.Alpha(0f).SetDuration(400).WithEndAction(new Java.Lang.Runnable(() =>
            {
                if (token == _bannerToken) _banner.Visibility = ViewStates.Gone;
            }));
        }, (long)BannerTime.TotalMilliseconds);
    }

    // --- Menu ---------------------------------------------------------------------------------------------

    private void ShowMenu()
    {
        _bannerToken++;
        _banner.Visibility = ViewStates.Gone;
        _statusCard.Visibility = ViewStates.Gone; // shown again when the menu closes
        var now = DateTime.Now;
        _menuClock.Text = now.ToString("HH:mm");
        _menuDate.Text = now.ToString("dddd d MMMM");
        _versionText.Text = $"GemakTV {Services.VersionName}";
        var current = RebuildChannelList();
        _menu.Visibility = ViewStates.Visible;
        _recentKeys.Clear();
        (current ?? _checkChannelsButton).RequestFocus();
    }

    private View? RebuildChannelList()
    {
        _channelList.RemoveAllViews();
        View? currentCard = null;
        var channels = _navigator.Lineup.Channels;
        for (var i = 0; i < channels.Count; i++)
        {
            var channel = channels[i];
            var isCurrent = i == _navigator.CurrentIndex;
            var card = LayoutInflater.Inflate(Resource.Layout.item_channel, _channelList, false)!;
            card.FindViewById<TextView>(Resource.Id.channel_number)!.Text = (i + 1).ToString();
            card.FindViewById<TextView>(Resource.Id.channel_name)!.Text = channel.Name;
            card.FindViewById<TextView>(Resource.Id.channel_status)!.Text = !channel.IsPlayable
                ? GetString(Resource.String.channel_not_set_up)
                : isCurrent ? "" : GetString(Resource.String.channel_ready);
            card.FindViewById(Resource.Id.channel_live)!.Visibility = isCurrent ? ViewStates.Visible : ViewStates.Gone;
            Services.Logos.Bind(card.FindViewById<ImageView>(Resource.Id.channel_logo)!, card.FindViewById<TextView>(Resource.Id.channel_letter)!, channel);
            var id = channel.Id;
            card.Click += (_, _) =>
            {
                _navigator.GoTo(id);
                HideMenu();
                PlayCurrent(showBanner: true);
            };
            AddFocusZoom(card);
            _channelList.AddView(card);
            if (isCurrent) currentCard = card;
        }
        return currentCard;
    }

    private void SwitchMenuColumn(bool toButtons)
    {
        if (toButtons)
        {
            if (CurrentFocus != _checkUpdatesButton) _checkChannelsButton.RequestFocus();
            return;
        }
        var index = Math.Max(0, _navigator.CurrentIndex);
        if (index < _channelList.ChildCount) _channelList.GetChildAt(index)!.RequestFocus();
    }

    private void HideMenu()
    {
        _menu.Visibility = ViewStates.Gone;
        if (_player is not null) OnPlaybackStatus(_player.Status);
        ShowBanner();
    }

    private void CloseTopOverlay()
    {
        if (IsVisible(_maintenance)) SetModal(_maintenance, false);
        else if (IsVisible(_dialog)) CloseDialog();
        else if (IsVisible(_menu)) HideMenu();
    }

    // --- Check For Updated Channel List ---------------------------------------------------------------

    private async Task CheckChannelsAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            ShowDialog(DialogKind.Busy, GetString(Resource.String.checking_channels), "");
            var playingBefore = _navigator.Current;
            var result = await Services.ChannelSync.CheckAsync(_navigator.Lineup);
            switch (result.Outcome)
            {
                case ChannelSyncOutcome.Updated:
                    _navigator.ReplaceLineup(result.Lineup);
                    Services.Logos.Prefetch(result.Lineup);
                    RebuildChannelList();
                    ShowDialog(DialogKind.Success, GetString(Resource.String.channels_updated),
                        result.Diff!.Describe() + "\n" + GetString(Resource.String.channels_now, result.Lineup.Count));
                    if (!Equals(playingBefore, _navigator.Current)) PlayCurrent(showBanner: false);
                    break;
                case ChannelSyncOutcome.AlreadyUpToDate:
                case ChannelSyncOutcome.NothingPublished:
                    ShowDialog(DialogKind.Success, GetString(Resource.String.channels_same), GetString(Resource.String.channels_same_detail));
                    break;
                default:
                    Log.Warn(Tag, $"Channel check failed: {result.Error}");
                    ShowDialog(DialogKind.Error, GetString(Resource.String.check_failed), GetString(Resource.String.check_failed_detail));
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"Channel check crashed: {ex}");
            ShowDialog(DialogKind.Error, GetString(Resource.String.check_failed), GetString(Resource.String.check_failed_detail));
        }
        finally
        {
            _busy = false;
        }
    }

    // --- Check For Updates ------------------------------------------------------------------------------

    private async Task CheckUpdatesAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            ShowDialog(DialogKind.Busy, GetString(Resource.String.checking_updates), "");
            var check = await Services.Updater.CheckAsync();
            if (check.Failed)
            {
                Log.Warn(Tag, $"Update check failed: {check.Error}");
                ShowDialog(DialogKind.Error, GetString(Resource.String.check_failed), GetString(Resource.String.check_failed_detail));
                return;
            }
            if (check.Status != AppUpdateStatus.UpdateAvailable)
            {
                ShowDialog(DialogKind.Success, GetString(Resource.String.app_up_to_date), GetString(Resource.String.app_up_to_date_detail, Services.VersionName));
                return;
            }

            var release = check.Release!;
            if (!SelfInstaller.CanInstall(this, Services.Kiosk.IsDeviceOwner))
            {
                // Without device owner Android 6 refuses with a confusing "Install blocked" screen; explain instead.
                ShowDialog(DialogKind.Error, GetString(Resource.String.updates_blocked), GetString(Resource.String.updates_blocked_detail));
                return;
            }
            ShowDialog(DialogKind.Progress, GetString(Resource.String.downloading_update), GetString(Resource.String.downloading_detail, release.VersionName));
            var path = Path.Combine(CacheDir!.AbsolutePath, "updates", $"GemakTV-{release.VersionCode}.apk");
            var progress = new Progress<double>(p => _dialogProgress.Progress = (int)(p * 1000));
            await Services.Updater.DownloadAsync(release, path, progress);

            if (await Task.Run(() => SelfInstaller.Verify(this, path, release.VersionCode)) is { } problem)
            {
                Log.Warn(Tag, $"Update rejected: {problem}");
                File.Delete(path);
                ShowDialog(DialogKind.Error, GetString(Resource.String.update_failed), problem);
                return;
            }

            ShowDialog(DialogKind.Busy, GetString(Resource.String.installing_update), GetString(Resource.String.installing_detail));
            await Task.Run(() => SelfInstaller.Install(this, path));
            // InstallResultReceiver reports back; on success Android replaces and restarts the app. If nothing
            // happens at all, do not leave the viewer looking at "Installing…" forever.
            var token = ++_installToken;
            _handler.PostDelayed(() =>
            {
                if (token == _installToken && IsVisible(_dialog) && _dialogButton.Visibility != ViewStates.Visible)
                    ShowDialog(DialogKind.Error, GetString(Resource.String.update_failed), GetString(Resource.String.update_failed_detail));
            }, (long)TimeSpan.FromMinutes(3).TotalMilliseconds);
        }
        catch (Exception ex)
        {
            Log.Error(Tag, $"Update failed: {ex}");
            ShowDialog(DialogKind.Error, GetString(Resource.String.update_failed), GetString(Resource.String.update_failed_detail));
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnInstallFinished(InstallOutcome outcome, string? message) => RunOnUiThread(() =>
    {
        switch (outcome)
        {
            case InstallOutcome.WaitingForUser:
                _awaitingInstallConfirmation = true;
                ShowDialog(DialogKind.Busy, GetString(Resource.String.installing_update), GetString(Resource.String.confirm_install_detail));
                break;
            case InstallOutcome.Failed:
                _installToken++;
                _awaitingInstallConfirmation = false;
                ShowDialog(DialogKind.Error, GetString(Resource.String.update_failed), GetString(Resource.String.update_failed_detail) + (message is null ? "" : $"\n({message})"));
                break;
        }
    });

    private void CleanOldDownloads()
    {
        var folder = Path.Combine(CacheDir!.AbsolutePath, "updates");
        if (Directory.Exists(folder))
            foreach (var file in Directory.GetFiles(folder))
                File.Delete(file);
    }

    // --- Dialog -----------------------------------------------------------------------------------------

    private enum DialogKind { Busy, Progress, Success, Error }

    private void ShowDialog(DialogKind kind, string title, string message)
    {
        if (!IsVisible(_dialog)) _focusBeforeDialog = CurrentFocus;
        _dialogSpinner.Visibility = kind == DialogKind.Busy ? ViewStates.Visible : ViewStates.Gone;
        _dialogIcon.Visibility = kind is DialogKind.Success or DialogKind.Error ? ViewStates.Visible : ViewStates.Gone;
        _dialogIcon.SetImageResource(kind == DialogKind.Error ? Resource.Drawable.ic_error : Resource.Drawable.ic_ok);
        _dialogProgress.Visibility = kind == DialogKind.Progress ? ViewStates.Visible : ViewStates.Gone;
        if (kind == DialogKind.Progress) _dialogProgress.Progress = 0;
        _dialogTitle.Text = title;
        _dialogMessage.Text = message;
        _dialogMessage.Visibility = string.IsNullOrEmpty(message) ? ViewStates.Gone : ViewStates.Visible;
        var canClose = kind is DialogKind.Success or DialogKind.Error;
        _dialogButton.Visibility = canClose ? ViewStates.Visible : ViewStates.Gone;
        SetModal(_dialog, true);
        if (canClose) _dialogButton.RequestFocus();
        else _dialog.RequestFocus();
    }

    private void CloseDialog()
    {
        if (_dialogButton.Visibility != ViewStates.Visible) return; // still working
        SetModal(_dialog, false);
        if (IsVisible(_menu)) (_focusBeforeDialog ?? _checkChannelsButton).RequestFocus();
    }

    // --- Maintenance (hidden) -----------------------------------------------------------------------

    private void ShowMaintenance()
    {
        _maintenanceInfo.Text = $"GemakTV {Services.VersionName} (code {Services.VersionCode}) · {Services.Kiosk.Describe()}\n" +
            $"Config key {Services.Key.Fingerprint} · {Services.Location}";
        SetModal(_maintenance, true);
        FindViewById(Resource.Id.maint_close)!.RequestFocus();
    }

    private void OpenSettings(string action)
    {
        SetModal(_maintenance, false);
        Services.Kiosk.LeaveLockTaskBriefly(this);
        try
        {
            StartActivity(new Intent(action).AddFlags(ActivityFlags.NewTask));
        }
        catch (ActivityNotFoundException)
        {
            StartActivity(new Intent(Android.Provider.Settings.ActionSettings).AddFlags(ActivityFlags.NewTask));
        }
    }

    // --- Setup --------------------------------------------------------------------------------------

    private void BindViews()
    {
        _playerView = FindViewById<PlayerView>(Resource.Id.player_view)!;
        _statusCard = FindViewById(Resource.Id.status_card)!;
        _statusSpinner = FindViewById(Resource.Id.status_spinner)!;
        _statusIcon = FindViewById<ImageView>(Resource.Id.status_icon)!;
        _statusTitle = FindViewById<TextView>(Resource.Id.status_title)!;
        _statusDetail = FindViewById<TextView>(Resource.Id.status_detail)!;
        _banner = FindViewById(Resource.Id.banner)!;
        _bannerLogo = FindViewById<ImageView>(Resource.Id.banner_logo)!;
        _bannerLetter = FindViewById<TextView>(Resource.Id.banner_letter)!;
        _bannerNumber = FindViewById<TextView>(Resource.Id.banner_number)!;
        _bannerName = FindViewById<TextView>(Resource.Id.banner_name)!;
        _bannerClock = FindViewById<TextView>(Resource.Id.banner_clock)!;
        _menu = FindViewById(Resource.Id.menu)!;
        _channelScroll = FindViewById<ScrollView>(Resource.Id.channel_scroll)!;
        _channelList = FindViewById<LinearLayout>(Resource.Id.channel_list)!;
        _menuClock = FindViewById<TextView>(Resource.Id.menu_clock)!;
        _menuDate = FindViewById<TextView>(Resource.Id.menu_date)!;
        _versionText = FindViewById<TextView>(Resource.Id.version_text)!;
        _checkChannelsButton = FindViewById(Resource.Id.btn_check_channels)!;
        _checkUpdatesButton = FindViewById(Resource.Id.btn_check_updates)!;
        _dialog = FindViewById(Resource.Id.dialog)!;
        _dialogSpinner = FindViewById(Resource.Id.dialog_spinner)!;
        _dialogIcon = FindViewById<ImageView>(Resource.Id.dialog_icon)!;
        _dialogTitle = FindViewById<TextView>(Resource.Id.dialog_title)!;
        _dialogMessage = FindViewById<TextView>(Resource.Id.dialog_message)!;
        _dialogButton = FindViewById<TextView>(Resource.Id.dialog_button)!;
        _dialogProgress = FindViewById<ProgressBar>(Resource.Id.dialog_progress)!;
        _maintenance = FindViewById(Resource.Id.maintenance)!;
        _maintenanceInfo = FindViewById<TextView>(Resource.Id.maintenance_info)!;

        _checkChannelsButton.Click += async (_, _) => await CheckChannelsAsync();
        _checkUpdatesButton.Click += async (_, _) => await CheckUpdatesAsync();
        _dialogButton.Click += (_, _) => CloseDialog();
        FindViewById(Resource.Id.maint_wifi)!.Click += (_, _) => OpenSettings(Android.Provider.Settings.ActionWifiSettings);
        FindViewById(Resource.Id.maint_settings)!.Click += (_, _) => OpenSettings(Android.Provider.Settings.ActionSettings);
        FindViewById(Resource.Id.maint_exit)!.Click += (_, _) =>
        {
            Services.Kiosk.Suspend(TimeSpan.FromMinutes(30), "turned off from the maintenance panel");
            Services.Kiosk.LeaveLockTaskBriefly(this);
            ShowMaintenance();
        };
        FindViewById(Resource.Id.maint_close)!.Click += (_, _) => SetModal(_maintenance, false);

        foreach (var id in new[] { Resource.Id.btn_check_channels, Resource.Id.btn_check_updates, Resource.Id.dialog_button,
                     Resource.Id.maint_wifi, Resource.Id.maint_settings, Resource.Id.maint_exit, Resource.Id.maint_close })
            AddFocusZoom(FindViewById(id)!);
    }

    private static void AddFocusZoom(View view) =>
        view.FocusChange += (_, e) => view.Animate()!.ScaleX(e.HasFocus ? 1.03f : 1f).ScaleY(e.HasFocus ? 1.03f : 1f).SetDuration(120);

    private void HideSystemUi()
    {
        // "Lean back" full screen (no IMMERSIVE flag): meant for video, and unlike immersive mode it never shows
        // Android's "Viewing full screen - swipe down to exit / GOT IT" prompt, which a viewer would have to dismiss.
#pragma warning disable CA1422, CS0618 // system UI flags are the Android 6 API
        Window!.DecorView.SystemUiVisibility = (StatusBarVisibility)(SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation
            | SystemUiFlags.LayoutStable | SystemUiFlags.LayoutFullscreen | SystemUiFlags.LayoutHideNavigation);
#pragma warning restore CA1422, CS0618
    }

    private static bool IsVisible(View view) => view.Visibility == ViewStates.Visible;

    /// <summary>Shows or hides a modal overlay; while one is up, the menu behind it cannot take focus.</summary>
    private void SetModal(View overlay, bool visible)
    {
        overlay.Visibility = visible ? ViewStates.Visible : ViewStates.Gone;
        var blocked = IsVisible(_dialog) || IsVisible(_maintenance);
        ((ViewGroup)_menu).DescendantFocusability = blocked ? DescendantFocusability.BlockDescendants : DescendantFocusability.AfterDescendants;
    }
}
