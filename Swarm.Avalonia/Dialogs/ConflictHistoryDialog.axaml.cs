using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Swarm.Core.Helpers;
using Swarm.Core.Services;
using Swarm.Core.ViewModels;

namespace Swarm.Avalonia.Dialogs;

public partial class ConflictHistoryDialog : Window
{
    public ConflictHistoryDialog()
    {
        InitializeComponent();
    }

    public ConflictHistoryDialog(ConflictResolutionService conflictService)
    {
        InitializeComponent();
        DataContext = new ConflictHistoryViewModel(conflictService);
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}

public class ConflictHistoryViewModel : ViewModels.ViewModelBase, IDisposable
{
    private readonly ConflictResolutionService _conflictService;
    private ObservableCollection<ConflictHistoryItemViewModel> _conflicts = new();
    private string _summaryText = "No conflicts";

    public ConflictHistoryViewModel(ConflictResolutionService conflictService)
    {
        _conflictService = conflictService;
        _conflictService.HistoryChanged += OnHistoryChanged;
        
        ClearHistoryCommand = new RelayCommand(ClearHistory);
        
        RefreshConflicts();
    }

    public ObservableCollection<ConflictHistoryItemViewModel> Conflicts
    {
        get => _conflicts;
        set => SetProperty(ref _conflicts, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        set => SetProperty(ref _summaryText, value);
    }

    public bool HasConflicts => Conflicts.Count > 0;

    public ICommand ClearHistoryCommand { get; }

    private void OnHistoryChanged()
    {
        Dispatcher.UIThread.Post(RefreshConflicts);
    }

    private void RefreshConflicts()
    {
        var history = _conflictService.ConflictHistory;
        
        Conflicts.Clear();
        foreach (var entry in history)
        {
            Conflicts.Add(new ConflictHistoryItemViewModel(entry));
        }

        // Update summary
        var count = Conflicts.Count;
        if (count == 0)
        {
            SummaryText = "No conflicts detected";
        }
        else
        {
            var keepLocal = history.Count(h => h.Resolution == ConflictChoice.KeepLocal);
            var keepRemote = history.Count(h => h.Resolution == ConflictChoice.KeepRemote);
            var keepBoth = history.Count(h => h.Resolution == ConflictChoice.KeepBoth);
            SummaryText = $"{count} conflicts resolved • Local: {keepLocal} • Remote: {keepRemote} • Both: {keepBoth}";
        }

        OnPropertyChanged(nameof(HasConflicts));
    }

    private void ClearHistory()
    {
        _conflictService.ClearHistory();
    }

    public void Dispose()
    {
        _conflictService.HistoryChanged -= OnHistoryChanged;
    }
}

public class ConflictHistoryItemViewModel
{
    public string FileName { get; }
    public string FullPath { get; }
    public string SourcePeerName { get; }
    public string ResolutionDisplay { get; }
    public string ResolutionMethod { get; }
    public string LocalModified { get; }
    public string RemoteModified { get; }
    public string LocalSize { get; }
    public string RemoteSize { get; }
    public string TimeAgo { get; }
    public ConflictChoice Resolution { get; }

    public ConflictHistoryItemViewModel(ConflictHistoryEntry entry)
    {
        FileName = System.IO.Path.GetFileName(entry.RelativePath);
        FullPath = entry.RelativePath;
        SourcePeerName = entry.SourcePeerName;
        Resolution = entry.Resolution;
        ResolutionDisplay = entry.Resolution switch
        {
            ConflictChoice.KeepLocal => "✓ Local",
            ConflictChoice.KeepRemote => "↓ Remote",
            ConflictChoice.KeepBoth => "⇄ Both",
            ConflictChoice.Skip => "⊘ Skipped",
            _ => entry.Resolution.ToString()
        };
        ResolutionMethod = entry.ResolutionMethod;
        LocalModified = entry.LocalModified.ToString("g");
        RemoteModified = entry.RemoteModified.ToString("g");
        LocalSize = FileHelpers.FormatBytes(entry.LocalSize);
        RemoteSize = FileHelpers.FormatBytes(entry.RemoteSize);
        TimeAgo = FormatTimeAgo(entry.ResolvedAt);
    }

    private static string FormatTimeAgo(DateTime time)
    {
        var span = DateTime.Now - time;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }
}
