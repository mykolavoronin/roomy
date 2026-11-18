using System.Globalization;
using System.Windows;
using System.Windows.Interop;
using PurgeWindows.Core;
using PurgeWindows.Services;
using PurgeWindows.Localization;

namespace PurgeWindows.Views;

public partial class SettingsWindow : Window
{
    readonly DesktopController _desktop;
    StartupStatus? _startup;
    DateTimeOffset? _snoozedUntil;
    bool _pauseChanged;
    bool _saving;
    bool _windowsBusy;
    bool? _hibernateOn;
    long _hiberfilSize;

    public SettingsWindow(DesktopController desktop)
    {
        InitializeComponent();
        var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = v is null ? S.AppName : Loc.Format("AppVersion", $"{v.Major}.{v.Minor}.{v.Build}");
        _desktop = desktop;
        var p = desktop.Preferences;
        BackgroundCheck.IsChecked = p.RunInBackground;
        LowSpaceCheck.IsChecked = p.LowSpaceAlerts;
        WeeklyCheck.IsChecked = p.WeeklyChecks;
        RemovalDefaultRadio.IsChecked = p.Removal == RemovalMode.ReviewToBin;
        RemovalBinRadio.IsChecked = p.Removal == RemovalMode.EverythingToBin;
        RemovalPermanentRadio.IsChecked = p.Removal == RemovalMode.EverythingPermanent;
        ThresholdBox.Text = p.LowSpaceGB.ToString(CultureInfo.InvariantCulture);
        IntervalBox.SelectedValue = p.CheckEveryMinutes.ToString(CultureInfo.InvariantCulture);
        _snoozedUntil = p.SnoozedUntil;
        UpdatePause();
        Options.IsEnabled = desktop.CanConfigure;
        SaveButton.IsEnabled = false;
        StatusText.Text = desktop.Status ?? "";
        // The Windows card is live system state, not a saved preference: it stays usable
        // when running as admin (when the saved settings above are locked), but never in
        // demo mode, where nothing on the real PC may change.
        WindowsCard.IsEnabled = !desktop.IsDemo;
        if (desktop.IsDemo) WindowsStatus.Text = S.WindowsSettingsDemoNote;
        ClearDeliveryOptButton.IsEnabled = false;
        Loaded += async (_, _) => await ReadWindowsStateAsync();
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            try { Native.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); } catch { }
        };
        Loaded += async (_, _) => await ReadStartupAsync();
        Activated += async (_, _) => { if (!_saving && IsLoaded) await ReadStartupAsync(); };
        Closing += (_, e) => { if (_saving) e.Cancel = true; };
    }

    async Task ReadStartupAsync()
    {
        try
        {
            _startup = _desktop.IsDemo ? new(false, true) : await StartupRegistration.ReadAsync();
            StartupCheck.IsChecked = _startup.Enabled;
            StartupCheck.IsEnabled = !_startup.Blocked && !_desktop.IsDemo;
            StartupMessage.Text = _startup.Message ?? "";
            StartupMessage.Visibility = string.IsNullOrEmpty(_startup.Message) ? Visibility.Collapsed : Visibility.Visible;
            SaveButton.IsEnabled = _desktop.CanConfigure;
        }
        catch (Exception ex)
        {
            StartupCheck.IsEnabled = false;
            SaveButton.IsEnabled = false;
            StatusText.Text = S.CantReadStartup + ex.Message;
        }
    }

    void Background_Click(object sender, RoutedEventArgs e)
    {
        if (BackgroundCheck.IsChecked == true) return;
        if (_startup is { Enabled: true, Blocked: true })
        {
            BackgroundCheck.IsChecked = true;
            StatusText.Text = S.WindowsManagesStartup;
            return;
        }
        StartupCheck.IsChecked = false;
        LowSpaceCheck.IsChecked = false;
        WeeklyCheck.IsChecked = false;
    }

    void EnableBackground_Click(object sender, RoutedEventArgs e)
    {
        if (StartupCheck.IsChecked == true || LowSpaceCheck.IsChecked == true || WeeklyCheck.IsChecked == true)
            BackgroundCheck.IsChecked = true;
    }

    void Pause_Click(object sender, RoutedEventArgs e)
    {
        _pauseChanged = true;
        _snoozedUntil = DateTimeOffset.UtcNow.AddDays(1);
        UpdatePause();
    }

    void Resume_Click(object sender, RoutedEventArgs e)
    {
        _pauseChanged = true;
        _snoozedUntil = null;
        UpdatePause();
    }

    void UpdatePause() => PauseStatus.Text = _snoozedUntil > DateTimeOffset.UtcNow
        ? Loc.Format("RemindersPausedUntil", _snoozedUntil.Value.LocalDateTime.ToString("g"))
        : S.RemindersFollowWindows;

    async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_startup is null || _saving) return;
        if (!int.TryParse(ThresholdBox.Text, out var threshold) || threshold is < 1 or > 500)
        {
            StatusText.Text = S.ThresholdRange;
            ThresholdBox.Focus();
            return;
        }
        _saving = true;
        SaveButton.IsEnabled = false;
        Options.IsEnabled = false;
        var previousStartup = _startup.Enabled;
        var startupChanged = false;
        try
        {
            var start = StartupCheck.IsChecked == true;
            if (!_desktop.IsDemo && start != previousStartup)
            {
                var result = await StartupRegistration.SetEnabledAsync(start);
                if (result.Enabled != start) throw new InvalidOperationException(result.Message ?? S.WindowsDeniedStartup);
                startupChanged = true;
            }
            var current = _desktop.Preferences;
            _desktop.Apply(current with
            {
                RunInBackground = BackgroundCheck.IsChecked == true || start,
                LowSpaceAlerts = LowSpaceCheck.IsChecked == true,
                WeeklyChecks = WeeklyCheck.IsChecked == true,
                Removal = RemovalBinRadio.IsChecked == true ? RemovalMode.EverythingToBin
                    : RemovalPermanentRadio.IsChecked == true ? RemovalMode.EverythingPermanent
                    : RemovalMode.ReviewToBin,
                LowSpaceGB = threshold,
                CheckEveryMinutes = IntervalBox.SelectedValue as string == "60" ? 60 : 30,
                SnoozedUntil = _pauseChanged ? _snoozedUntil : current.SnoozedUntil,
                LastWeeklyCheck = WeeklyCheck.IsChecked == true && !current.WeeklyChecks ? DateTimeOffset.UtcNow : current.LastWeeklyCheck,
            });
            _saving = false;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = S.CantSaveSettings + ex.Message;
            if (startupChanged)
            {
                try { await StartupRegistration.SetEnabledAsync(previousStartup); }
                catch { StatusText.Text += S.StartupCheckHint; }
            }
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = _desktop.CanConfigure;
            Options.IsEnabled = _desktop.CanConfigure;
        }
    }

    void StartupSettings_Click(object sender, RoutedEventArgs e) => Shell.Open("ms-settings:startupapps");
    void NotificationSettings_Click(object sender, RoutedEventArgs e) => Shell.Open("ms-settings:notifications");
    void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- Windows space settings (live system state, applied immediately) ----------

    async Task ReadWindowsStateAsync()
    {
        if (_desktop.IsDemo) return;
        var (enabled, size) = await Task.Run(() => (WindowsFeatures.HibernationEnabled(), WindowsFeatures.HiberfilSize()));
        _hibernateOn = enabled;
        _hiberfilSize = size;
        ShowHibernateState();
        ClearDeliveryOptButton.IsEnabled = Admin.IsElevated;
        DeliveryOptAdminText.Visibility = Admin.IsElevated ? Visibility.Collapsed : Visibility.Visible;
    }

    void ShowHibernateState()
    {
        HibernateCheck.IsChecked = _hibernateOn ?? false;
        HibernateCheck.IsEnabled = _hibernateOn.HasValue;
        HibernateStateText.Text = _hibernateOn is null ? S.HibernateUnknown
            : (_hibernateOn.Value ? S.HibernateStateOn : S.HibernateStateOff)
              + (_hiberfilSize > 0 ? " " + Loc.Format("HibernateFileSize", Format.Bytes(_hiberfilSize)) : "");
    }

    async void Hibernate_Click(object sender, RoutedEventArgs e)
    {
        if (_windowsBusy || _hibernateOn is null) return;
        bool want = HibernateCheck.IsChecked == true;
        if (want == _hibernateOn.Value) return;
        _windowsBusy = true;
        WindowsCard.IsEnabled = false;
        WindowsStatus.Text = "";
        try
        {
            var before = _hiberfilSize;
            var (result, error) = await WindowsFeatures.SetHibernationAsync(want);
            var (enabled, size) = await Task.Run(() => (WindowsFeatures.HibernationEnabled(), WindowsFeatures.HiberfilSize()));
            _hibernateOn = enabled;
            _hiberfilSize = size;
            ShowHibernateState();
            WindowsStatus.Text = result switch
            {
                HibernationResult.Applied => want
                    ? S.HibernateTurnedOn
                    : Loc.Format("HibernateTurnedOff", Format.Bytes(Math.Max(0, before - size))),
                HibernationResult.Cancelled => S.HibernateCancelled,
                _ => error ?? S.HibernateUnknown,
            };
        }
        finally
        {
            _windowsBusy = false;
            WindowsCard.IsEnabled = !_desktop.IsDemo;
        }
    }

    async void ClearDeliveryOpt_Click(object sender, RoutedEventArgs e)
    {
        if (_windowsBusy) return;
        _windowsBusy = true;
        ClearDeliveryOptButton.IsEnabled = false;
        WindowsStatus.Text = "";
        try
        {
            var r = await Task.Run(() => WindowsFeatures.ClearDeliveryOptimizationCache());
            WindowsStatus.Text = r.Freed > 0
                ? Loc.Format("DeliveryOptCleared", Format.Bytes(r.Freed))
                : S.DeliveryOptEmpty;
        }
        catch (Exception ex)
        {
            WindowsStatus.Text = ex.Message;
        }
        finally
        {
            _windowsBusy = false;
            ClearDeliveryOptButton.IsEnabled = Admin.IsElevated && !_desktop.IsDemo;
        }
    }
}
