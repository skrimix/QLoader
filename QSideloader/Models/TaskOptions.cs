﻿namespace QSideloader.Models;

public class TaskOptions
{
    public TaskType Type { get; set; }
    public Game? Game { get; set; }
    public string? Path { get; set; }
    public InstalledApp? App { get; set; }
    public BackupOptions? BackupOptions { get; set; }
    public Backup? Backup { get; set; }
}