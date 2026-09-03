using CodeLines.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeLines.App.ViewModels;

public sealed partial class ProjectItemViewModel : ObservableObject
{
    public ProjectItemViewModel(ProjectDefinition model)
    {
        Model = model;
        name = model.Name;
        rootPath = model.RootPath;
        isEnabled = model.IsEnabled;
        exclusions = string.Join(Environment.NewLine, model.ExcludePatterns);
    }

    public ProjectDefinition Model { get; }

    [ObservableProperty] private string name;
    [ObservableProperty] private string rootPath;
    [ObservableProperty] private bool isEnabled;
    [ObservableProperty] private string exclusions;

    public void Apply()
    {
        Model.Name = Name.Trim();
        Model.RootPath = RootPath.Trim();
        Model.IsEnabled = IsEnabled;
        Model.ExcludePatterns = Exclusions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}

