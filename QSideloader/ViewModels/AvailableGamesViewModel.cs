﻿using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient;
using Avalonia.Controls.Notifications;
using DynamicData;
using QSideloader.Models;
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
    private readonly SideloaderSettingsViewModel _sideloaderSettings;
    private readonly ReadOnlyObservableCollection<Game> _availableGames;
    private readonly SourceCache<Game, string> _availableGamesSourceCache = new(x => x.ReleaseName!);
    private readonly ObservableAsPropertyHelper<bool> _isBusy;

    public AvailableGamesViewModel()
    {
        _adbService = AdbService.Instance;
        _downloaderService = DownloaderService.Instance;
        _sideloaderSettings = Globals.SideloaderSettings;
        Activator = new ViewModelActivator();

        Func<Game, bool> GameFilter(string text)
        {
            return game => string.IsNullOrEmpty(text)
                           || text.Split()
                               .All(x => game.ReleaseName!.ToLower()
                                   .Contains(x.ToLower()));
        }

        var filterPredicate = this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(250))
            .DistinctUntilChanged()
            .Select(GameFilter);
        var cacheListBind = _availableGamesSourceCache.Connect()
            .RefCount()
            .ObserveOn(RxApp.TaskpoolScheduler)
            .Filter(filterPredicate)
            .SortBy(x => x.ReleaseName!)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _availableGames)
            .DisposeMany();
        Refresh = ReactiveCommand.CreateFromObservable(() => RefreshImpl());
        Refresh.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Error refreshing available games");
            Globals.ShowNotification("Error", "Error refreshing available games", NotificationType.Error);
        });
        ManualRefresh = ReactiveCommand.CreateFromObservable(() => RefreshImpl(true));
        ManualRefresh.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Error refreshing available games");
            Globals.ShowNotification("Error", "Error refreshing available games", NotificationType.Error);
        });
        var isExecutingCombined = Refresh.IsExecuting
            .CombineLatest(ManualRefresh.IsExecuting, (x, y) => x || y);
        isExecutingCombined.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Install = ReactiveCommand.CreateFromObservable(InstallImpl);
        Download = ReactiveCommand.CreateFromObservable(DownloadImpl);
        this.WhenActivated(disposables =>
        {
            cacheListBind.Subscribe().DisposeWith(disposables);
            _adbService.WhenDeviceStateChanged.Subscribe(OnDeviceStateChanged).DisposeWith(disposables);
            _adbService.WhenPackageListChanged.Subscribe(_ => RefreshInstalled()).DisposeWith(disposables);
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

    public ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> ManualRefresh { get; }
    public ReactiveCommand<Unit, Unit> Install { get; }
    public ReactiveCommand<Unit, Unit> Download { get; }
    public ReadOnlyObservableCollection<Game> AvailableGames => _availableGames;
    public bool IsBusy => _isBusy.Value;
    [Reactive] public bool MultiSelectEnabled { get; set; } = true;
    [Reactive] public string SearchText { get; set; } = "";
    [Reactive] public bool IsDeviceConnected { get; set; }
    [Reactive] public bool ShowPopularity1Day { get; set; }
    [Reactive] public bool ShowPopularity7Days { get; set; }
    [Reactive] public bool ShowPopularity30Days { get; set; }
    public ViewModelActivator Activator { get; }

    private IObservable<Unit> RefreshImpl(bool force = false)
    {
        return Observable.FromAsync(async () =>
        {
            await _downloaderService.EnsureMetadataAvailableAsync(force);
            IsDeviceConnected = _adbService.CheckDeviceConnectionSimple();
            PopulateAvailableGames();
            RefreshInstalled();
        });
    }

    private IObservable<Unit> InstallImpl()
    {
        return Observable.Start(() =>
        {
            if (IsBusy)
                return;
            if (!_adbService.CheckDeviceConnectionSimple())
            {
                Log.Warning("AvailableGamesViewModel.InstallImpl: no device connection!");
                IsDeviceConnected = false;
                return;
            }

            var selectedGames = _availableGamesSourceCache.Items.Where(game => game.IsSelected).ToList();
            if (selectedGames.Count == 0)
            {
                Log.Information("No games selected for download and install");
                Globals.ShowNotification("Download & Install", "No games selected", NotificationType.Information,
                    TimeSpan.FromSeconds(2));
                return;
            }
            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.DownloadAndInstall, Game = game});
            }
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
                Globals.ShowNotification("Download", "No games selected", NotificationType.Information,
                    TimeSpan.FromSeconds(2));
                return;
            }
            foreach (var game in selectedGames)
            {
                game.IsSelected = false;
                Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.DownloadOnly, Game = game});
            }
        });
    }

    private void OnDeviceStateChanged(DeviceState state)
    {
        switch (state)
        {
            case DeviceState.Online:
                IsDeviceConnected = true;
                Task.Run(RefreshInstalled);
                break;
            case DeviceState.Offline:
                IsDeviceConnected = false;
                Task.Run(RefreshInstalled);
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
            .Where(game => _downloaderService.AvailableGames.All(game2 => game.ReleaseName != game2.ReleaseName)).ToList();
        _availableGamesSourceCache.Edit(innerCache =>
        {
            innerCache.AddOrUpdate(_downloaderService.AvailableGames);
            innerCache.Remove(toRemove);
        });
    }

    private void RefreshInstalled()
    {
        var games = _availableGamesSourceCache.Items.ToList();
        if (_adbService.Device is null)
            foreach (var game in games)
                game.IsInstalled = false;
        else
        {
            while (_adbService.Device.IsRefreshingInstalledGames)
                Thread.Sleep(100);
            var installedPackages = _adbService.Device.InstalledPackages.ToList();
            foreach (var game in games.Where(game => game.PackageName is not null))
                game.IsInstalled = installedPackages.Any(p => p.packageName == game.PackageName!);
        }
    }
}