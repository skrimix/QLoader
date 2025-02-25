﻿using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using AdvancedSharpAdbClient.Models;
using Avalonia.Controls.Notifications;
using DynamicData;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Services;
using QSideloader.Utilities;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class AvailableGamesViewModel : ViewModelBase, IActivatableViewModel
{
    private readonly AdbService _adbService;
    private readonly DownloaderService _downloaderService;

    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly SettingsData _sideloaderSettings;
    private readonly ReadOnlyObservableCollection<Game> _availableGames;
    private readonly SourceCache<Game, string> _availableGamesSourceCache = new(x => x.ReleaseName!);
    private readonly ObservableAsPropertyHelper<bool> _isBusy;

    public AvailableGamesViewModel()
    {
        _adbService = AdbService.Instance;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        Activator = new ViewModelActivator();

        // ReSharper disable once MoveLocalFunctionAfterJumpStatement
        Func<Game, bool> GameFilter(string text)
        {
            return game => string.IsNullOrEmpty(text)
                           || text.Split()
                               .All(x => game.ReleaseName!.Contains(x, StringComparison.OrdinalIgnoreCase));
        }

        var filterPredicate = this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .DistinctUntilChanged()
            .Select(GameFilter);
        _availableGamesSourceCache.Connect()
            .RefCount()
            .ObserveOn(RxApp.TaskpoolScheduler)
            .Filter(filterPredicate)
            .SortBy(x => x.ReleaseName!)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _availableGames)
            .DisposeMany().Subscribe();
        Refresh = ReactiveCommand.CreateFromObservable<bool, Unit>(RefreshImpl);
        Refresh.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Error loading available games list");
            Globals.ShowErrorNotification(ex, Resources.ErrorLoadingAvailableGames);
        });
        Refresh.IsExecuting.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Install = ReactiveCommand.CreateFromObservable(InstallImpl);
        InstallSingle = ReactiveCommand.CreateFromObservable<Game, Unit>(InstallSingleImpl);
        Download = ReactiveCommand.CreateFromObservable(DownloadImpl);
        DownloadSingle = ReactiveCommand.CreateFromObservable<Game, Unit>(DownloadSingleImpl);
        this.WhenActivated(disposables =>
        {
            _adbService.WhenDeviceStateChanged.Subscribe(OnDeviceStateChanged).DisposeWith(disposables);
            _adbService.WhenPackageListChanged.Subscribe(_ => Task.Run(RefreshInstalledAsync)).DisposeWith(disposables);
            ShowPopularity30Days = _sideloaderSettings.PopularityRange == "30 days";
            ShowPopularity7Days = _sideloaderSettings.PopularityRange == "7 days";
            ShowPopularity1Day = _sideloaderSettings.PopularityRange == "1 day";
            try
            {
                Refresh.Execute().Subscribe();
            }
            catch
            {
                // ignored
            }
        });
    }

    public ReactiveCommand<bool, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> Install { get; }
    public ReactiveCommand<Game, Unit> InstallSingle { get; }
    public ReactiveCommand<Unit, Unit> Download { get; }
    public ReactiveCommand<Game, Unit> DownloadSingle { get; }
    public ReadOnlyObservableCollection<Game> AvailableGames => _availableGames;

    public bool IsBusy => _isBusy.Value;

    //[Reactive] public bool MultiSelectEnabled { get; set; } = true;
    [Reactive] public string SearchText { get; set; } = "";
    [Reactive] public bool IsDeviceConnected { get; set; }
    [Reactive] public bool ShowPopularity1Day { get; set; }
    [Reactive] public bool ShowPopularity7Days { get; set; }
    [Reactive] public bool ShowPopularity30Days { get; set; }
    public ViewModelActivator Activator { get; }

    private IObservable<Unit> RefreshImpl(bool force = false)
    {
        return Observable.StartAsync(async () =>
        {
            await _downloaderService.TryLoadMetadataAsync(force);
            IsDeviceConnected = await  _adbService.CheckDeviceConnectionAsync();
            PopulateAvailableGames();
            await RefreshInstalledAsync();
        });
    }

    private IObservable<Unit> InstallImpl()
    {
        return Observable.Start(() =>
        {
            var selectedGames = _availableGamesSourceCache.Items.Where(game => game.IsSelected).ToList();
            if (selectedGames.Count == 0)
            {
                Log.Information("No games selected for download and install");
                Globals.ShowNotification(Resources.DownloadAndInstallButton, Resources.NoGamesSelected,
                    NotificationType.Information, TimeSpan.FromSeconds(2));
                return;
            }

            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                game.Install();
            }
        });
    }

    private IObservable<Unit> InstallSingleImpl(Game game)
    {
        return Observable.Start(() =>
        {
            if (!_adbService.IsDeviceConnected)
            {
                Log.Warning("AvailableGamesViewModel.InstallImpl: no device connection!");
                IsDeviceConnected = false;
                return;
            }

            game.Install();
        });
    }

    private IObservable<Unit> DownloadImpl()
    {
        return Observable.Start(() =>
        {
            if (IsBusy)
                return;
            var selectedGames = _availableGamesSourceCache.Items.Where(game => game.IsSelected).ToList();
            if (selectedGames.Count == 0)
            {
                Log.Information("No games selected for download");
                Globals.ShowNotification(Resources.DownloadButton, Resources.NoGamesSelected,
                    NotificationType.Information, TimeSpan.FromSeconds(2));
                return;
            }

            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                game.Download();
            }
        });
    }

    private IObservable<Unit> DownloadSingleImpl(Game game)
    {
        return Observable.Start(() =>
        {
            if (IsBusy)
                return;
            game.Download();
        });
    }

    private void OnDeviceStateChanged(DeviceState state)
    {
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (state)
        {
            case DeviceState.Online:
                IsDeviceConnected = true;
                Task.Run(RefreshInstalledAsync);
                break;
            case DeviceState.Offline:
                IsDeviceConnected = false;
                Task.Run(RefreshInstalledAsync);
                break;
        }
    }

    private void PopulateAvailableGames()
    {
        if (_downloaderService.AvailableGames is null)
        {
            Log.Warning("PopulateAvailableGames: DownloaderService.AvailableGames is not initialized!");
            return;
        }

        var toRemove = _availableGamesSourceCache.Items
            .Where(game => _downloaderService.AvailableGames.All(game2 => game.ReleaseName != game2.ReleaseName))
            .ToList();
        _availableGamesSourceCache.Edit(innerCache =>
        {
            innerCache.AddOrUpdate(_downloaderService.AvailableGames);
            innerCache.Remove(toRemove);
        });
    }

    private async Task RefreshInstalledAsync()
    {
        var games = _availableGamesSourceCache.Items.ToList();
        if (_adbService.Device is null)
        {
            foreach (var game in games)
                game.IsInstalled = false;
            return;
        }

        while (_adbService.Device.IsRefreshingInstalledGames)
            await Task.Delay(100);
        if (_adbService.Device is null)
        {
            foreach (var game in games)
                game.IsInstalled = false;
            return;
        }

        var installedPackages = _adbService.Device.InstalledPackages.ToList();
        foreach (var game in games.Where(game => game.PackageName is not null))
            game.IsInstalled = installedPackages.Any(p => p.packageName == game.PackageName!);
    }
}