using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.ReactiveUI;
using Avalonia.Styling;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Navigation;
using QSideloader.Utilities;
using QSideloader.ViewModels;
using QSideloader.Views.Pages;
using ReactiveUI;
using Serilog;

namespace QSideloader.Views;

public partial class MainWindow : ReactiveWindow<MainWindowViewModel>, IMainWindow
{
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        this.AttachDevTools();
#endif
        Title = Program.Name;

        var thm = Application.Current?.Styles.OfType<FluentAvaloniaTheme>().FirstOrDefault();
        if (thm is not null)
        {
            thm.PreferSystemTheme = false;
            thm.PreferUserAccentColor = true;
            if (OperatingSystem.IsWindows())
                thm.ForceWin32WindowToTheme(this, ThemeVariant.Dark);
        }

        AddHandler(DragDrop.DragEnterEvent, DragEnter);
        AddHandler(DragDrop.DragLeaveEvent, DragLeave);
        AddHandler(DragDrop.DropEvent, Drop);

        ContentFrame.NavigationFailed += ContentFrame_OnNavigationFailed;

        // Navigate to the first page
        NavigationView.SelectedItem = NavigationView.MenuItems.OfType<NavigationViewItem>().First();

        // Recalculate task list height when windows size changes
        this.GetObservable(ClientSizeProperty).Throttle(TimeSpan.FromMilliseconds(100))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(_ => RecalculateTaskListBoxHeight());
    }

    private INotificationManager? NotificationManager { get; set; }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void NavigationView_OnSelectionChanged(object? sender, NavigationViewSelectionChangedEventArgs e)
    {
        if (e.IsSettingsSelected)
        {
            var pageType = typeof(SettingsView);
            Log.Debug("Navigating to {View}", pageType);
            ContentFrame.BackStack.Clear();
            ContentFrame.Navigate(pageType);
        }
        else
        {
            var selectedItem = (NavigationViewItem) e.SelectedItem;
            var selectedItemTag = (string) selectedItem.Tag!;
            var pageName = "QSideloader.Views.Pages." + selectedItemTag;
            var pageType = Type.GetType(pageName);
            if (pageType is null) return;
            Log.Debug("Navigating to {View}", selectedItemTag);
            ContentFrame.BackStack.Clear();
            ContentFrame.Navigate(pageType);
        }
    }

    private static void ContentFrame_OnNavigationFailed(object? sender, NavigationFailedEventArgs e)
    {
        Log.Error(e.Exception, "Failed to navigate to {View}", e.SourcePageType);
    }

    public void NavigateToGameDonationView()
    {
        NavigationView.SelectedItem = NavigationView.MenuItems
            .OfType<NavigationViewItem>()
            .First(x => (string?) x.Tag == "GameDonationView");
    }

    private void RecalculateTaskListBoxHeight()
    {
        var windowHeight = ClientSize.Height;
        TaskListBox.MaxHeight = (int) windowHeight / (double) 3 / 60 * 60;
        //Log.Debug("Recalculated TaskListBox height to {Height}", taskListBox.MaxHeight);
    }

    private void TaskListBox_OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        var viewModel = (MainWindowViewModel) DataContext!;
        var listBox = (ListBox?) sender;
        var selectedTask = (TaskViewModel?) e.AddedItems[0];
        if (listBox is null || selectedTask is null) return;
        listBox.SelectedItem = null;
        switch (selectedTask.IsFinished)
        {
            case true when viewModel.TaskList.Contains(selectedTask):
                Log.Debug("Dismissed finished task {TaskId} {TaskType} {TaskName}", selectedTask.TaskId,
                    selectedTask.TaskType, selectedTask.TaskName);
                viewModel.TaskList.Remove(selectedTask);
                break;
            case false:
                selectedTask.Cancel();
                break;
        }
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    private void Window_OnOpened(object? sender, EventArgs e)
    {
        // Couldn't set this in styles, so this will have to do
        NavigationView.SettingsItem.Content = Properties.Resources.Settings;

        if (Design.IsDesignMode) return;
        RecalculateTaskListBoxHeight();
    }

    [SuppressMessage("ReSharper", "UnusedParameter.Local")]
    [SuppressMessage("ReSharper", "SuggestBaseTypeForParameter")]
    private async void Window_OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_isClosing || ViewModel!.TaskList.Count == 0 || ViewModel.TaskList.All(x => x.IsFinished))
        {
            KillRclone();
            Log.Information("Closing application");
            await Log.CloseAndFlushAsync();
            return;
        }

        e.Cancel = true;
        Log.Information("Application close requested, cancelling tasks");
        foreach (var task in ViewModel.TaskList)
            task.Cancel();
        // Give tasks time to cancel
        // Check every 100ms for tasks to finish with a timeout of 2s
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(100);
            if (ViewModel.TaskList.All(x => x.IsFinished))
                break;
        }

        _isClosing = true;
        Close();
        return;

        // Kill all dangling rclone processes
        void KillRclone()
        {
            var rclonePath = Path.GetFullPath(PathHelper.RclonePath);
            var rcloneName = Path.GetFileNameWithoutExtension(rclonePath);
            var rcloneProcesses = Process.GetProcessesByName(rcloneName);
            foreach (var process in rcloneProcesses)
            {
                // check full path to make sure it's our rclone
                if (process.MainModule?.FileName != rclonePath) continue;
                Log.Debug("Killing rclone process {ProcessId}", process.Id);
                try
                {
                    process.Kill();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to kill rclone process {ProcessId}", process.Id);
                }
            }
        }
    }

    private void DragEnter(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
        DragDropPanel.IsVisible = true;
        e.Handled = true;
    }

    private void DragLeave(object? sender, RoutedEventArgs e)
    {
        DragDropPanel.IsVisible = false;
        e.Handled = true;
    }

    private async void Drop(object? sender, DragEventArgs e)
    {
        Log.Debug("DragDrop.Drop event");
        if (e.Data.Contains(DataFormats.Files))
        {
            var files = e.Data.GetFiles()?.ToList();
            if (files is null)
            {
                Log.Warning("e.Data.GetFileNames() returned null");
                return;
            }

            var fileNames = files.Select(x => x.Path.LocalPath).ToList();

            Log.Debug("Dropped folders/files: {FilesNames}", fileNames);
            await MainWindowViewModel.HandleDroppedItemsAsync(fileNames);
        }
        else
        {
            Log.Warning("Drop data does not contain file names");
        }

        DragDropPanel.IsVisible = false;
        e.Handled = true;
    }

    // ReSharper disable UnusedParameter.Local
    private void Window_OnLoaded(object? sender, RoutedEventArgs e)
    {
        NotificationManager = new WindowNotificationManager(GetTopLevel(this))
        {
            Position = NotificationPosition.TopRight,
            MaxItems = 3
        };
        if (ViewModel is null) return;
        ViewModel.NotificationManager = NotificationManager;
    }

    private async void Window_OnKeyDown(object? sender, KeyEventArgs e)
    {
        var openFile = e.Key == Key.F2;
        var openFolder = e.Key == Key.F3;
        if (!openFile && !openFolder) return;
        e.Handled = true;
        if (openFile)
        {
            var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Properties.Resources.SelectApkFile,
                AllowMultiple = true,
                FileTypeFilter = new FilePickerFileType[] {new("APK files") {Patterns = new List<string> {"*.apk"}}}
            });
            if (result.Count == 0) return;
            var paths = from file in result
                let localPath = file.TryGetLocalPath()
                where File.Exists(localPath)
                select localPath;
            await MainWindowViewModel.HandleDroppedItemsAsync(paths);
        }
        else if (openFolder)
        {
            var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Properties.Resources.SelectGameFolder,
                AllowMultiple = true
            });
            if (result.Count == 0) return;
            var paths = from folder in result
                let localPath = folder.TryGetLocalPath()
                where Directory.Exists(localPath)
                select localPath;
            await MainWindowViewModel.HandleDroppedItemsAsync(paths);
        }
    }
}