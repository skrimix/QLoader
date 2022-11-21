﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using FileHelpers;
using Newtonsoft.Json;

// ReSharper disable MemberCanBePrivate.Global

namespace QSideloader.Models;

[DelimitedRecord(";")]
[IgnoreFirst]
[SuppressMessage("ReSharper", "PropertyCanBeMadeInitOnly.Global")]
public class Game : INotifyPropertyChanged
{
    [FieldHidden] [JsonIgnore] private bool _isInstalled;
    [FieldHidden] [JsonIgnore] private bool _isSelected;

    protected Game()
    {
    }

    public Game(string gameName, string releaseName, int gameSize, string? note)
    {
        GameName = gameName;
        ReleaseName = releaseName;
        GameSize = gameSize;
        if (note is null) return;
        Note = note;
    }

    public Game(string gameName, string releaseName)
    {
        GameName = gameName;
        ReleaseName = releaseName;
    }

    public Game(string gameName, string releaseName, string packageName)
    {
        GameName = gameName;
        ReleaseName = releaseName;
        PackageName = packageName;
    }

    [FieldTrim(TrimMode.Right)] public string? GameName { get; protected set; }
    public string? ReleaseName { get; protected set; }
    public string? PackageName { get; protected set; }
    public int VersionCode { get; protected set; }

    [FieldConverter(ConverterKind.Date, "yyyy-MM-dd HH:mm UTC", "en")]
    public DateTime LastUpdated { get; protected set; }

    public int GameSize { get; protected set; }

    [FieldHidden]
    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    [FieldHidden]
    [JsonIgnore]
    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            _isInstalled = value;
            OnPropertyChanged();
        }
    }

    [FieldHidden] [JsonIgnore] public string? Note { get; set; }

    [FieldHidden]
    [JsonIgnore]
    // ReSharper disable once CollectionNeverQueried.Global
    public Dictionary<string, int?> Popularity { get; } = new() {{"1D", null}, {"7D", null}, {"30D", null}};

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString()
    {
        return ReleaseName ?? "";
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}