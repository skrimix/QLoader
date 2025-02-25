﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdvancedSharpAdbClient.Models;
using QSideloader.Models;
using QSideloader.Properties;
using QSideloader.Services;
using QSideloader.Utilities;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Serilog;
using Timer = System.Timers.Timer;

namespace QSideloader.ViewModels;

public class DeviceInfoViewModel : ViewModelBase, IActivatableViewModel
{
    private static readonly SemaphoreSlim DeviceListSemaphoreSlim = new(1, 1);
    private static readonly SemaphoreSlim RefreshSemaphoreSlim = new(1, 1);
    private readonly AdbService _adbService;
    private readonly ObservableAsPropertyHelper<bool> _isBusy;
    private Timer? _refreshTimer;

    public DeviceInfoViewModel()
    {
        _adbService = AdbService.Instance;
        Activator = new ViewModelActivator();
        Refresh = ReactiveCommand.CreateFromObservable(RefreshImpl);
        Refresh.IsExecuting.ToProperty(this, x => x.IsBusy, out _isBusy, false, RxApp.MainThreadScheduler);
        EnableWirelessAdb = ReactiveCommand.CreateFromTask(EnableWirelessAdbImpl);
        Refresh.ThrownExceptions.Subscribe(ex =>
        {
            Log.Error(ex, "Error refreshing device info");
            Globals.ShowErrorNotification(ex, Resources.ErrorGettingDeviceInfo);
        });
        Refresh.Execute().Subscribe();
        this.WhenActivated(disposables =>
        {
            _adbService.WhenDeviceStateChanged.Subscribe(OnDeviceStateChanged).DisposeWith(disposables);
            _adbService.WhenPackageListChanged.Subscribe(_ => OnPackageListChanged()).DisposeWith(disposables);
            _adbService.WhenDeviceListChanged.Subscribe(OnDeviceListChanged).DisposeWith(disposables);
            this.WhenAnyValue(x => x.CurrentDevice).Where(x => x is not null && x.Serial != _adbService.Device?.Serial)
                .DistinctUntilChanged()
                .Subscribe(x =>
                {
                    Task.Run(async () =>
                    {
                        await _adbService.TrySwitchDeviceAsync(x!);
                        RefreshDeviceSelection();
                    });
                }).DisposeWith(disposables);
        });
    }

    private ReactiveCommand<Unit, Unit> Refresh { get; }
    public ReactiveCommand<Unit, Unit> EnableWirelessAdb { get; }

    public bool IsBusy => _isBusy.Value;

    [Reactive] public string? FriendlyName { get; private set; }
    [Reactive] public bool IsQuest1 { get; private set; }
    [Reactive] public bool IsQuest2 { get; private set; }
    [Reactive] public float SpaceUsed { get; private set; }
    [Reactive] public float SpaceFree { get; private set; }
    [Reactive] public float BatteryLevel { get; private set; }
    [Reactive] public bool IsDeviceConnected { get; set; }
    [Reactive] public bool IsDeviceWireless { get; private set; }
    [Reactive] public AdbService.AdbDevice? CurrentDevice { get; set; }
    [Reactive] public string? TrueSerial { get; set; }
    [Reactive] public ObservableCollection<AdbService.AdbDevice> DeviceList { get; set; } = [];
    [Reactive] public bool IsDeviceSwitchEnabled { get; set; } = true;
    public ViewModelActivator Activator { get; }

    private void OnDeviceStateChanged(DeviceState state)
    {
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
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
        SetRefreshTimerState(true);
    }

    private void OnDeviceOffline()
    {
        IsDeviceConnected = false;
        CurrentDevice = null;
        TrueSerial = null;
        SetRefreshTimerState(false);
    }

    private void OnPackageListChanged()
    {
        Refresh.Execute().Subscribe();
    }

    private IObservable<Unit> RefreshImpl()
    {
        return Observable.StartAsync(async () =>
        {
            // Check whether refresh is already in running
            if (RefreshSemaphoreSlim.CurrentCount == 0) return;
            await RefreshSemaphoreSlim.WaitAsync();
            try
            {
                await RefreshDeviceInfoAsync();
                RefreshProps();
            }
            finally
            {
                RefreshSemaphoreSlim.Release();
            }
        });
    }

    private async Task EnableWirelessAdbImpl()
    {
        if (!await _adbService.CheckDeviceConnectionAsync())
        {
            Log.Warning("DeviceInfoViewModel.EnableWirelessAdbImpl: no device connection!");
            OnDeviceOffline();
            return;
        }

        IsDeviceSwitchEnabled = false;
        await _adbService.EnableWirelessAdbAsync(_adbService.Device!);
        Observable.Timer(TimeSpan.FromSeconds(5)).Subscribe(_ => IsDeviceSwitchEnabled = true);
    }

    private async Task RefreshDeviceInfoAsync()
    {
        if (!await _adbService.CheckDeviceConnectionAsync())
        {
            Log.Warning("DeviceInfoViewModel.RefreshDeviceInfo: no device connection!");
            OnDeviceOffline();
            return;
        }

        IsDeviceConnected = true;
        SetRefreshTimerState(true);
        IsDeviceWireless = _adbService.Device!.IsWireless;
        await _adbService.Device.RefreshInfoAsync();
    }

    private void RefreshProps()
    {
        var device = _adbService.Device;
        OnDeviceListChanged(_adbService.DeviceList);
        RefreshDeviceSelection();
        if (device is null) return;
        SpaceUsed = device.SpaceUsed;
        SpaceFree = device.SpaceFree;
        BatteryLevel = device.BatteryLevel;
        FriendlyName = device.FriendlyName;
        IsQuest1 = device.HeadsetEnum == OculusHeadsetEnum.Quest1;
        IsQuest2 = device.HeadsetEnum == OculusHeadsetEnum.Quest2;
    }

    private void OnDeviceListChanged(IReadOnlyList<AdbService.AdbDevice> deviceList)
    {
        if (DeviceListSemaphoreSlim.CurrentCount == 0) return;
        DeviceListSemaphoreSlim.Wait();
        try
        {
            var toAdd = deviceList.Where(device => DeviceList.All(x => x.Serial != device.Serial)).ToList();
            var toRemove = DeviceList.Where(device => deviceList.All(x => x.Serial != device.Serial)).ToList();
            foreach (var device in toAdd)
                DeviceList.Add(device);
            // Workaround to avoid crash with ArgumentOutOfRangeException
            if (DeviceList.Count == toRemove.Count)
                DeviceList = [];
            else
                foreach (var device in toRemove)
                    DeviceList.Remove(device);
        }
        finally
        {
            DeviceListSemaphoreSlim.Release();
        }
    }

    private void RefreshDeviceSelection()
    {
        CurrentDevice = DeviceList.FirstOrDefault(x => _adbService.Device?.Equals(x) ?? false);
        TrueSerial = CurrentDevice?.TrueSerial;
    }

    private void SetRefreshTimerState(bool enabled)
    {
        if (enabled)
        {
            if (_refreshTimer is null)
            {
                _refreshTimer = new Timer(180000);
                _refreshTimer.Elapsed += (_, _) => Refresh.Execute().Subscribe();
                _refreshTimer.AutoReset = true;
            }

            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer?.Stop();
        }
    }
}