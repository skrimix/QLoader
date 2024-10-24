﻿using System;
using System.Linq;
using System.Text.RegularExpressions;
using QSideloader.ViewModels;
using Serilog.Core;
using Serilog.Events;

namespace QSideloader.Utilities;

public class PathsMaskingEnricher : ILogEventEnricher
{
    private static SettingsData? _sideloaderSettings;

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        _sideloaderSettings ??= Globals.SideloaderSettings;
        var downloadsPathRegex = new Regex(Regex.Escape(_sideloaderSettings.DownloadsLocation));
        var backupsPathRegex = new Regex(Regex.Escape(_sideloaderSettings.BackupsLocation));
        var userDirectoryRegex = new Regex(Regex.Escape(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        foreach (var property in logEvent.Properties.ToList())
            if (property.Value is ScalarValue {Value: string stringValue})
                switch (stringValue)
                {
                    case not null when downloadsPathRegex.IsMatch(stringValue):
                        logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key,
                            new ScalarValue(downloadsPathRegex.Replace(stringValue, "_DownloadsLocation_", 1))));
                        break;
                    case not null when backupsPathRegex.IsMatch(stringValue):
                        logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key,
                            new ScalarValue(backupsPathRegex.Replace(stringValue, "_BackupsLocation_", 1))));
                        break;
                    case not null when userDirectoryRegex.IsMatch(stringValue):
                        logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key,
                            new ScalarValue(userDirectoryRegex.Replace(stringValue, "_UserDirectory_", 1))));
                        break;
                }
    }
}