﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using Newtonsoft.Json;
using QSideloader.Helpers;
using QSideloader.Models;
using QSideloader.Services;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using ReactiveUI.Validation.Extensions;
using Serilog;
using Timer = System.Timers.Timer;

namespace QSideloader.ViewModels;

[JsonObject(MemberSerialization.OptIn)]
public class SideloaderSettingsViewModel : ViewModelBase
{
    private readonly string _defaultDownloadsLocation = PathHelper.DefaultDownloadsPath;
    private readonly string _defaultBackupsLocation = PathHelper.DefaultBackupsPath;
    private static readonly SemaphoreSlim MirrorSelectionRefreshSemaphoreSlim = new (1, 1);
    private readonly ObservableAsPropertyHelper<bool> _isSwitchingMirror;
    
    public SideloaderSettingsViewModel()
    {
        SaveSettings = ReactiveCommand.CreateFromObservable<bool, Unit>(SaveSettingsImpl);
        BrowseDownloadsDirectory = ReactiveCommand.CreateFromTask(BrowseDownloadsDirectoryImpl);
        SetDownloadLocation = ReactiveCommand.CreateFromObservable(SetDownloadLocationImpl, this.IsValid());
        BrowseBackupsDirectory = ReactiveCommand.CreateFromTask(BrowseBackupsDirectoryImpl);
        SetBackupsLocation = ReactiveCommand.CreateFromObservable(SetBackupsLocationImpl, this.IsValid());
        SetDownloaderBandwidthLimit =
            ReactiveCommand.CreateFromObservable(SetDownloaderBandwidthLimitImpl, this.IsValid());
        RestoreDefaults = ReactiveCommand.CreateFromObservable(RestoreDefaultsImpl);
        CheckUpdates = ReactiveCommand.CreateFromObservable(CheckUpdatesImpl);
        CheckUpdates.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Error checking for updates");
            Globals.ShowErrorNotification(ex, "Error checking for updates");
        });
        SwitchMirror = ReactiveCommand.CreateFromObservable(SwitchMirrorImpl);
        ReloadMirrorList = ReactiveCommand.CreateFromObservable(ReloadMirrorListImpl);
        var isSwitchingMirrorCombined =
            SwitchMirror.IsExecuting.CombineLatest(ReloadMirrorList.IsExecuting, (a, b) => a || b);
        isSwitchingMirrorCombined.ToProperty(this, x => x.IsSwitchingMirror, out _isSwitchingMirror, false,
            RxApp.MainThreadScheduler);
        IsTrailersAddonInstalled = Directory.Exists(PathHelper.TrailersPath);
        InstallTrailersAddon = ReactiveCommand.CreateFromObservable(InstallTrailersAddonImpl);
        CopyInstallationId = ReactiveCommand.CreateFromTask(CopyInstallationIdImpl);
        InitDefaults();
        LoadSettings();
        ValidateSettings();
        PropertyChanged += AutoSave;
        DonatedPackages.CollectionChanged += OnCollectionChanged;
        IgnoredDonationPackages.CollectionChanged += OnCollectionChanged;
        this.ValidationRule(viewModel => viewModel.DownloadsLocationTextBoxText,
            Directory.Exists,
            "Invalid path");
        this.ValidationRule(viewModel => viewModel.BackupsLocationTextBoxText,
            Directory.Exists,
            "Invalid path");
        this.ValidationRule(viewModel => viewModel.DownloaderBandwidthLimitTextBoxText,
            x => string.IsNullOrEmpty(x) ||
                 int.TryParse(Regex.Match(x, @"^(\d+)[BKMGTP]{0,1}$").Groups[1].ToString(), out _),
            "Invalid format. Allowed format: Number in KiB/s, or number with suffix B|K|M|G|T|P (e.g. 1000 or 10M)");
        AutoSaveDelayTimer.Elapsed += (_, _) =>
        {
            ValidateSettings(false);
            SaveSettings.Execute().Subscribe();
        };
        this.WhenAnyValue(x => x.SelectedMirror).Where(s => s is not null)
            .DistinctUntilChanged()
            .Subscribe(_ =>
            {
                SwitchMirror.Execute().Subscribe();
                RefreshMirrorSelection();
            });
    }

    [JsonProperty] private byte ConfigVersion { get; } = 1;
    [Reactive] [JsonProperty] public bool CheckUpdatesAutomatically { get; private set; }
    public string[] ConnectionTypes { get; } = {"USB", "Wireless"};
    [Reactive] [JsonProperty] public string? PreferredConnectionType { get; private set; }
    [Reactive] public string DownloadsLocationTextBoxText { get; private set; } = "";
    [JsonProperty] public string DownloadsLocation { get; private set; } = "";
    [Reactive] public string BackupsLocationTextBoxText { get; private set; } = "";
    [JsonProperty] public string BackupsLocation { get; private set; } = "";
    [Reactive] public string DownloaderBandwidthLimitTextBoxText { get; private set; } = "";
    [JsonProperty] public string DownloaderBandwidthLimit { get; private set; } = "";
    [Reactive] [JsonProperty] public bool DeleteAfterInstall { get; private set; }
    [Reactive] [JsonProperty] public bool EnableDebugConsole { get; private set; }
    [Reactive] [JsonProperty] public string LastWirelessAdbHost { get; set; } = "";
    public string VersionString { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
#if DEBUG
    public bool IsConsoleToggleable { get; }
#else
    public bool IsConsoleToggleable { get; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
#endif
    [Reactive] public List<string> MirrorList { get; private set; } = new();
    [Reactive] public string? SelectedMirror { get; set; }
    public bool IsSwitchingMirror => _isSwitchingMirror.Value;
    public string[] PopularityRanges { get; } = {"30 days", "7 days", "1 day", "None"};
    [Reactive] [JsonProperty] public string? PopularityRange { get; private set; }
    [JsonProperty] public Guid InstallationId { get; private set; } = Guid.NewGuid();
    [JsonProperty] public ObservableCollection<(string packageName, int versionCode)> DonatedPackages { get; } = new();
    [JsonProperty] public ObservableCollection<string> IgnoredDonationPackages { get; private set; } = new();
    [Reactive] public bool IsTrailersAddonInstalled { get; set; }
    [Reactive] [JsonProperty] public bool EnableRemoteLogging { get; private set; }
    private ReactiveCommand<bool, Unit> SaveSettings { get; }
    private ReactiveCommand<Unit, Unit> RestoreDefaults { get; }
    public ReactiveCommand<Unit, Unit> BrowseDownloadsDirectory { get; }
    public ReactiveCommand<Unit, Unit> SetDownloadLocation { get; }
    public ReactiveCommand<Unit, Unit> BrowseBackupsDirectory { get; }
    public ReactiveCommand<Unit, Unit> SetBackupsLocation { get; }
    public ReactiveCommand<Unit, Unit> SetDownloaderBandwidthLimit { get; }
    public ReactiveCommand<Unit, Unit> CheckUpdates { get; }
    public ReactiveCommand<Unit, Unit> SwitchMirror { get; }
    public ReactiveCommand<Unit, Unit> ReloadMirrorList { get; }
    public ReactiveCommand<Unit, Unit> InstallTrailersAddon { get; }
    public ReactiveCommand<Unit, Unit> CopyInstallationId { get; }

    private Timer AutoSaveDelayTimer { get; } = new() {AutoReset = false, Interval = 500};
    
    private void InitDefaults()
    {
        CheckUpdatesAutomatically = true;
        PreferredConnectionType = ConnectionTypes[0];
        DownloadsLocation = _defaultDownloadsLocation;
        DownloadsLocationTextBoxText = DownloadsLocation;
        BackupsLocation = _defaultBackupsLocation;
        BackupsLocationTextBoxText = BackupsLocation;
        DownloaderBandwidthLimit = "";
        DownloaderBandwidthLimitTextBoxText = "";
        DeleteAfterInstall = false;
        LastWirelessAdbHost = "";
        EnableDebugConsole = !IsConsoleToggleable;
        PopularityRange = PopularityRanges[0];
        IgnoredDonationPackages = new ObservableCollection<string>();
        EnableRemoteLogging = false;
    }

    private void ValidateSettings(bool save = true)
    {
        var saveNeeded = false;
        if (!Directory.Exists(DownloadsLocation))
        {
            if (DownloadsLocation == _defaultDownloadsLocation)
            {
                Directory.CreateDirectory(_defaultDownloadsLocation);
            }
            else
            {
                Log.Debug("Downloads location is invalid, resetting to default");
                Directory.CreateDirectory(_defaultDownloadsLocation);
                DownloadsLocation = _defaultDownloadsLocation;
                saveNeeded = true;
            }
        }

        if (!Directory.Exists(BackupsLocation))
        {
            if (BackupsLocation == _defaultBackupsLocation)
            {
                Directory.CreateDirectory(_defaultBackupsLocation);
                BackupsLocationTextBoxText = BackupsLocation;
            }
            else
            {
                Log.Debug("Backups location is invalid, resetting to default");
                Directory.CreateDirectory(_defaultBackupsLocation);
                BackupsLocation = _defaultBackupsLocation;
                saveNeeded = true;
            }
        }

        if (!ConnectionTypes.Contains(PreferredConnectionType))
        {
            Log.Debug("Preferred connection type is invalid, resetting to default");
            PreferredConnectionType = ConnectionTypes[0];
            saveNeeded = true;
        }
        
        if (!PopularityRanges.Contains(PopularityRange))
        {
            Log.Debug("Popularity range is invalid, resetting to default");
            PopularityRange = PopularityRanges[0];
            saveNeeded = true;
        }

        DownloadsLocationTextBoxText = DownloadsLocation;
        BackupsLocationTextBoxText = BackupsLocation;
        DownloaderBandwidthLimitTextBoxText = DownloaderBandwidthLimit;
        if (save && saveNeeded)
            SaveSettings.Execute().Subscribe();
    }

    private void LoadSettings()
    {
        if (File.Exists(PathHelper.SettingsPath))
        {
            try
            {
                var json = File.ReadAllText(PathHelper.SettingsPath);
                JsonConvert.PopulateObject(json, this);
                SaveSettings.Execute(true).Subscribe();
                Log.Information("Loaded settings");
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to load settings, resetting to defaults");
                InitDefaults();
                SaveSettings.Execute().Subscribe();
            }
        }
        else
        {
            Log.Information("No settings file, loading defaults");
            SaveSettings.Execute().Subscribe();
        }
    }

    private IObservable<Unit> SaveSettingsImpl(bool silent = false)
    {
        return Observable.Start(() =>
        {
            try
            {
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    using FileStream stream = AtomicFileStream.Open(PathHelper.SettingsPath, FileMode.Create,
                        FileAccess.Write, FileShare.Read, 4096, FileOptions.None);
                    stream.Write(Encoding.UTF8.GetBytes(json));
                }
                else
                {
                    var tmpPath = PathHelper.SettingsPath + ".tmp";
                    File.WriteAllText(tmpPath, json);
                    File.Move(tmpPath, PathHelper.SettingsPath, true);
                    File.Delete(tmpPath);
                }

                if (!silent)
                    Log.Debug("Settings saved");
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to save settings");
                Globals.ShowErrorNotification(e, "Failed to save settings");
                throw;
            }
        });
    }

    private IObservable<Unit> RestoreDefaultsImpl()
    {
        return Observable.Start(() =>
        {
            Log.Information("Restoring default settings");
            InitDefaults();
            AutoSaveDelayTimer.Stop();
            AutoSaveDelayTimer.Start();
            Globals.ShowNotification("Info", "Restored default settings", NotificationType.Success,
                TimeSpan.FromSeconds(3));
        });
    }

    private IObservable<Unit> SetDownloadLocationImpl()
    {
        return Observable.Start(() =>
        {
            if (!Directory.Exists(DownloadsLocationTextBoxText) ||
                DownloadsLocationTextBoxText == DownloadsLocation) return;
            DownloadsLocation = DownloadsLocationTextBoxText;
            SaveSettings.Execute().Subscribe();
            Log.Debug("Set new downloads location: {Location}",
                DownloadsLocationTextBoxText);
            Globals.ShowNotification("Info", "New downloads location set", NotificationType.Success,
                TimeSpan.FromSeconds(2));
        });
    }

    private IObservable<Unit> SetBackupsLocationImpl()
    {
        return Observable.Start(() =>
        {
            if (!Directory.Exists(BackupsLocationTextBoxText) ||
                BackupsLocationTextBoxText == BackupsLocation) return;
            BackupsLocation = BackupsLocationTextBoxText;
            SaveSettings.Execute().Subscribe();
            Log.Debug("Set new backups location: {Location}",
                BackupsLocationTextBoxText);
            Globals.ShowNotification("Info", "New backups location set", NotificationType.Success,
                TimeSpan.FromSeconds(2));
        });
    }

    private IObservable<Unit> SetDownloaderBandwidthLimitImpl()
    {
        return Observable.Start(() =>
        {
            if (DownloaderBandwidthLimitTextBoxText == DownloaderBandwidthLimit ||
                (!string.IsNullOrEmpty(DownloaderBandwidthLimitTextBoxText) &&
                 !int.TryParse(
                     Regex.Match(DownloaderBandwidthLimitTextBoxText, @"^(\d+)[BKMGTP]{0,1}$").Groups[1].ToString(),
                     out _))) return;
            DownloaderBandwidthLimit = DownloaderBandwidthLimitTextBoxText;
            SaveSettings.Execute().Subscribe();
            if (!string.IsNullOrEmpty(DownloaderBandwidthLimitTextBoxText))
            {
                Log.Debug("Set new downloader bandwidth limit: {Limit}",
                    DownloaderBandwidthLimitTextBoxText);
                Globals.ShowNotification("Info", "New downloader bandwidth limit set",
                    NotificationType.Success, TimeSpan.FromSeconds(2));
            }
            else
            {
                Log.Debug("Removed downloader bandwidth limit");
                Globals.ShowNotification("Info", "Removed downloader bandwidth limit",
                    NotificationType.Success, TimeSpan.FromSeconds(2));
            }
        });
    }

    private IObservable<Unit> CheckUpdatesImpl()
    {
        return Observable.Start(() =>
        {
            if (Globals.Updater is null)
            {
                Log.Error("Requested to check for updates, but updater is not initialized");
                throw new InvalidOperationException("Updater is not initialized");
            }

            Dispatcher.UIThread.InvokeAsync(() => Globals.Updater.CheckForUpdatesAtUserRequest());
        });
    }

    public void RefreshMirrorSelection()
    {
        var downloaderService = DownloaderService.Instance;
        if (MirrorSelectionRefreshSemaphoreSlim.CurrentCount == 0) return;
        MirrorSelectionRefreshSemaphoreSlim.Wait();
        try
        {
            MirrorList = downloaderService.MirrorListReadOnly.ToList();
            SelectedMirror = downloaderService.MirrorName != "" ? downloaderService.MirrorName : null;
        }
        finally
        {
            MirrorSelectionRefreshSemaphoreSlim.Release();
        }
    }

    private IObservable<Unit> SwitchMirrorImpl()
    {
        return Observable.Start(() =>
        {
            if (MirrorSelectionRefreshSemaphoreSlim.CurrentCount == 0) return;
            var downloaderService = DownloaderService.Instance;
            if (SelectedMirror == downloaderService.MirrorName || SelectedMirror is null) return;
            downloaderService.TryManualSwitchMirror(SelectedMirror);
            RefreshMirrorSelection();
        });
    }
    
    private IObservable<Unit> ReloadMirrorListImpl()
    {
        return Observable.Start(() =>
        {
            if (MirrorSelectionRefreshSemaphoreSlim.CurrentCount == 0) return;
            DownloaderService.Instance.ReloadMirrorList();
            RefreshMirrorSelection();
        });
    }
    
    private IObservable<Unit> InstallTrailersAddonImpl()
    {
        return Observable.Start(() =>
        {
            if (Directory.Exists(PathHelper.TrailersPath))
            {
                IsTrailersAddonInstalled = true;
                return;
            }
            Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.DownloadAndInstallTrailersAddon});
        });
    }
    
    private async Task BrowseDownloadsDirectoryImpl()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            var result = await new OpenFolderDialog
            {
                Title = "Select downloads folder",
                Directory = DownloadsLocation
            }.ShowAsync(mainWindow);
            if (result is not null)
            {
                DownloadsLocationTextBoxText = result;
                SetDownloadLocation.Execute().Subscribe();
            }
        }
    }
    
    private async Task BrowseBackupsDirectoryImpl()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;
            var result = await new OpenFolderDialog
            {
                Title = "Select backups folder",
                Directory = BackupsLocation
            }.ShowAsync(mainWindow);
            if (result is not null)
            {
                BackupsLocationTextBoxText = result;
                SetBackupsLocation.Execute().Subscribe();
            }
        }
    }
    
    private async Task CopyInstallationIdImpl(){
        await Application.Current!.Clipboard!.SetTextAsync(InstallationId.ToString());
        Globals.ShowNotification("Info", "Installation ID copied to clipboard", NotificationType.Success, 
            TimeSpan.FromSeconds(2));
    }

    private static void ShowRelaunchNotification(string propertyName)
    {
        if (propertyName != "EnableDebugConsole" && propertyName != "EnableRemoteLogging"
            && propertyName != "CheckUpdatesAutomatically") return;
        Globals.ShowNotification("Settings", "Application restart is needed to apply this change",
            NotificationType.Information, TimeSpan.FromSeconds(2));
    }

    private void AutoSave(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null ||
            (typeof(SideloaderSettingsViewModel).GetProperty(e.PropertyName) is { } property && !Attribute.IsDefined(
                property,
                typeof(JsonPropertyAttribute)))) return;
        ShowRelaunchNotification(e.PropertyName);
        AutoSaveDelayTimer.Stop();
        AutoSaveDelayTimer.Start();
    }
    
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        AutoSaveDelayTimer.Stop();
        AutoSaveDelayTimer.Start();
    }
}