using System;
using System.Collections.ObjectModel;
using System.Windows.Input;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Swarm.Core.ViewModels;
using Swarm.Core.Helpers;

namespace Swarm.Avalonia.ViewModels;

/// <summary>
/// ViewModel for displaying side-by-side text diff using DiffPlex.
/// </summary>
public class DiffCompareViewModel : ViewModelBase
{
    private string _leftTitle = "Old Version";
    private string _rightTitle = "Current Version";
    private string _leftText = "";
    private string _rightText = "";
    private ObservableCollection<DiffLineViewModel> _leftLines = new();
    private ObservableCollection<DiffLineViewModel> _rightLines = new();

    public DiffCompareViewModel(string leftText, string rightText, string leftTitle, string rightTitle)
    {
        _leftText = leftText;
        _rightText = rightText;
        _leftTitle = leftTitle;
        _rightTitle = rightTitle;

        CloseCommand = new RelayCommand<object>(Close);
        
        BuildDiff();
    }

    public string LeftTitle
    {
        get => _leftTitle;
        set => SetProperty(ref _leftTitle, value);
    }

    public string RightTitle
    {
        get => _rightTitle;
        set => SetProperty(ref _rightTitle, value);
    }

    public ObservableCollection<DiffLineViewModel> LeftLines
    {
        get => _leftLines;
        set => SetProperty(ref _leftLines, value);
    }

    public ObservableCollection<DiffLineViewModel> RightLines
    {
        get => _rightLines;
        set => SetProperty(ref _rightLines, value);
    }

    public ICommand CloseCommand { get; }
    public Action<bool>? CloseAction { get; set; }

    private void BuildDiff()
    {
        var diffBuilder = new SideBySideDiffBuilder(new Differ());
        var diffResult = diffBuilder.BuildDiffModel(_leftText, _rightText);

        LeftLines.Clear();
        RightLines.Clear();

        foreach (var line in diffResult.OldText.Lines)
        {
            LeftLines.Add(new DiffLineViewModel
            {
                LineNumber = line.Position,
                Text = line.Text ?? "",
                ChangeType = MapChangeType(line.Type)
            });
        }

        foreach (var line in diffResult.NewText.Lines)
        {
            RightLines.Add(new DiffLineViewModel
            {
                LineNumber = line.Position,
                Text = line.Text ?? "",
                ChangeType = MapChangeType(line.Type)
            });
        }
    }

    private static DiffChangeType MapChangeType(ChangeType type) => type switch
    {
        ChangeType.Inserted => DiffChangeType.Added,
        ChangeType.Deleted => DiffChangeType.Removed,
        ChangeType.Modified => DiffChangeType.Modified,
        ChangeType.Imaginary => DiffChangeType.Imaginary,
        _ => DiffChangeType.Unchanged
    };

    private void Close(object? parameter)
    {
        CloseAction?.Invoke(false);
    }
}

public class DiffLineViewModel
{
    public int? LineNumber { get; set; }
    public string Text { get; set; } = "";
    public DiffChangeType ChangeType { get; set; }
    
    public string LineNumberDisplay => LineNumber?.ToString() ?? "";
    
    /// <summary>
    /// Returns a background color based on the change type.
    /// </summary>
    public string BackgroundColor => ChangeType switch
    {
        DiffChangeType.Added => "#20008800",
        DiffChangeType.Removed => "#20880000",
        DiffChangeType.Modified => "#20886600",
        DiffChangeType.Imaginary => "#10808080",
        _ => "Transparent"
    };
}

public enum DiffChangeType
{
    Unchanged,
    Added,
    Removed,
    Modified,
    Imaginary
}
