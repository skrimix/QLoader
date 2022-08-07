﻿using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using AdvancedSharpAdbClient;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using DynamicData;
using QSideloader.Models;
using QSideloader.Services;
using QSideloader.Utilities;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;

namespace QSideloader.ViewModels;

public class BackupViewModel: ViewModelBase, IActivatableViewModel
{
    private readonly AdbService _adbService;
    private readonly ReadOnlyObservableCollection<Backup> _backups;
    private readonly SourceCache<Backup, DateTime> _backupsSourceCache = new(x => x.Date);
    private readonly ObservableAsPropertyHelper<bool> _isBusy;
    public BackupViewModel()
    {
        Activator = new ViewModelActivator();
        _adbService = AdbService.Instance;
        Refresh = ReactiveCommand.CreateFromObservable<bool,Unit>(RefreshImpl);
        Refresh.IsExecuting.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        Restore = ReactiveCommand.CreateFromObservable(RestoreImpl);
        var cacheListBind = _backupsSourceCache.Connect()
            .RefCount()
            .SortBy(x => x.Date)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Bind(out _backups)
            .DisposeMany();
        this.WhenActivated(disposables =>
        {
            cacheListBind.Subscribe().DisposeWith(disposables);
            _adbService.WhenDeviceStateChanged.Subscribe(OnDeviceStateChanged).DisposeWith(disposables);
            _adbService.WhenBackupListChanged.Subscribe(_ => Refresh.Execute(false).Subscribe())
                .DisposeWith(disposables);
            IsDeviceConnected = _adbService.CheckDeviceConnectionSimple();
            Refresh.Execute(false).Subscribe();
        });
    }

    private ReactiveCommand<bool, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> Restore { get; }
    public ReadOnlyObservableCollection<Backup> Backups => _backups;
    public bool IsBusy => _isBusy.Value;
    [Reactive] public bool IsDeviceConnected { get; private set; }
    
    public ViewModelActivator Activator { get; }
    
    private IObservable<Unit> RefreshImpl(bool rescan)
    {
        return Observable.Start(() =>
        {
            if (rescan)
                _adbService.RefreshBackupList();
            _backupsSourceCache.Edit(innerCache =>
            {
                innerCache.AddOrUpdate(_adbService.BackupList);
                innerCache.Remove(_backups.Except(_adbService.BackupList).ToList());
            });
        });
    }

    private IObservable<Unit> RestoreImpl()
    {
        return Observable.Start(() =>
        {
            if (!_adbService.CheckDeviceConnectionSimple())
            {
                Log.Warning("BackupViewModel.RestoreImpl: no device connection!");
                OnDeviceOffline();
                return;
            }

            var selectedBackups = _backupsSourceCache.Items.Where(backup => backup.IsSelected).ToList();
            if (selectedBackups.Count == 0)
            {
                Log.Information("No backups selected for restore");
                Globals.ShowNotification("Restore", "No backups selected", NotificationType.Information, TimeSpan.FromSeconds(2));
                return;
            }
            foreach (var backup in selectedBackups)
            {
                backup.IsSelected = false;
                Globals.MainWindowViewModel!.AddTask(new TaskOptions {Type = TaskType.Restore, Backup = backup});
                Log.Information("Queued for restore: {BackupName}", backup);
            }
        });
    }
    
    private void OnDeviceStateChanged(DeviceState state)
    {
        switch (state)
        {
            case DeviceState.Online:
                OnDeviceOnline();
                break;
            case DeviceState.Offline:
                OnDeviceOffline();
                break;
        }
    }

    private void OnDeviceOnline()
    {
        IsDeviceConnected = true;
        Refresh.Execute().Subscribe();
    }

    private void OnDeviceOffline()
    {
        IsDeviceConnected = false;
        Dispatcher.UIThread.InvokeAsync(_backupsSourceCache.Clear);
    }
}