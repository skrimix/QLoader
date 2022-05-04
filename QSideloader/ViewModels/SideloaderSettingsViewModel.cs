﻿using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Timers;
using Avalonia.Threading;
using Newtonsoft.Json;
using QSideloader.Helpers;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using ReactiveUI.Validation.Extensions;
using Serilog;

namespace QSideloader.ViewModels;

[JsonObject(MemberSerialization.OptIn)]
public class SideloaderSettingsViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly string _defaultDownloadsLocation = Path.Combine(Environment.CurrentDirectory, "downloads");
    private readonly string _defaultBackupsLocation = Path.Combine(Environment.CurrentDirectory, "backups");
    
    public SideloaderSettingsViewModel()
    {
        Activator = new ViewModelActivator();
        SaveSettings = ReactiveCommand.CreateFromObservable(SaveSettingsImpl);
        SetDownloadLocation = ReactiveCommand.CreateFromObservable(SetDownloadLocationImpl, this.IsValid());
        SetBackupsLocation = ReactiveCommand.CreateFromObservable(SetBackupsLocationImpl, this.IsValid());
        SetDownloaderBandwidthLimit = ReactiveCommand.CreateFromObservable(SetDownloaderBandwidthLimitImpl, this.IsValid());
        RestoreDefaults = ReactiveCommand.CreateFromObservable(RestoreDefaultsImpl);
        CheckUpdates = ReactiveCommand.CreateFromObservable(CheckUpdatesImpl);
        InitDefaults();
        LoadSettings();
        ValidateSettings();
        PropertyChanged += AutoSave;
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
    }

    [JsonProperty] private byte ConfigVersion { get; } = 1;
    [Reactive] [JsonProperty] public bool CheckUpdatesOnLaunch { get; private set; }
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
    private ReactiveCommand<Unit, Unit> SaveSettings { get; }
    private ReactiveCommand<Unit, Unit> RestoreDefaults { get; }
    public ReactiveCommand<Unit, Unit> SetDownloadLocation { get; }
    public ReactiveCommand<Unit, Unit> SetBackupsLocation { get; }
    public ReactiveCommand<Unit, Unit> SetDownloaderBandwidthLimit { get; }
    public ReactiveCommand<Unit, Unit> CheckUpdates { get; }

    private Timer AutoSaveDelayTimer { get; } = new() {AutoReset = false, Interval = 500};

    public ViewModelActivator Activator { get; }

    private void InitDefaults()
    {
        CheckUpdatesOnLaunch = true;
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
                Log.Information("Loaded settings");
            }
            catch
            {
                Log.Warning("Failed to load settings, using defaults");
                InitDefaults();
            }
        }
        else
        {
            Log.Information("No settings file, using defaults");
            SaveSettings.Execute().Subscribe();
        }
    }

    private IObservable<Unit> SaveSettingsImpl()
    {
        return Observable.Start(() =>
        {
            try
            {
                var json = JsonConvert.SerializeObject(this);
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

                Log.Debug("Settings saved");
            }
            catch (Exception e)
            {
                Log.Error(e, "Failed to save settings");
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
        });
    }
    
    private IObservable<Unit> SetDownloaderBandwidthLimitImpl()
    {
        return Observable.Start(() =>
        {
            if (DownloaderBandwidthLimitTextBoxText == DownloaderBandwidthLimit ||
                !string.IsNullOrEmpty(DownloaderBandwidthLimitTextBoxText) &&
                !int.TryParse(
                    Regex.Match(DownloaderBandwidthLimitTextBoxText, @"^(\d+)[BKMGTP]{0,1}$").Groups[1].ToString(),
                    out _)) return;
            DownloaderBandwidthLimit = DownloaderBandwidthLimitTextBoxText;
            SaveSettings.Execute().Subscribe();
            if (!string.IsNullOrEmpty(DownloaderBandwidthLimitTextBoxText))
                Log.Debug("Set new downloader bandwidth limit: {Limit}",
                    DownloaderBandwidthLimitTextBoxText);
            else
            {
                Log.Debug("Removed downloader bandwidth limit");
            }
        });
    }

    private IObservable<Unit> CheckUpdatesImpl()
    {
        return Observable.Start(() =>
        {
            if (Globals.Updater is null) return;
            Dispatcher.UIThread.InvokeAsync(() => Globals.Updater.CheckForUpdatesAtUserRequest());
        });
    }

    private void AutoSave(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null || typeof(SideloaderSettingsViewModel).GetProperty(e.PropertyName) is { } property && !Attribute.IsDefined(property,
                typeof(JsonPropertyAttribute))) return;
        AutoSaveDelayTimer.Stop();
        AutoSaveDelayTimer.Start();
    }
}