﻿using Avalonia.ReactiveUI;
using QSideloader.ViewModels;

namespace QSideloader.Views.Pages;

// ReSharper disable once UnusedType.Global
public partial class BackupView : ReactiveUserControl<BackupViewModel>
{
    public BackupView()
    {
        InitializeComponent();
        ViewModel = new BackupViewModel();
        DataContext = ViewModel;
    }
}