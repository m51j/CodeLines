using CodeLines.Core.AiUsage;
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
    [ObservableProperty] private double aiTokens;
    [ObservableProperty] private double aiTokens30Days;
    [ObservableProperty] private string aiCost = "—";
    [ObservableProperty] private int aiSessions;

    public void SetAiUsage(AiProjectUsage allTime, AiProjectUsage last30Days)
    {
        AiTokens = allTime.Tokens;
        AiTokens30Days = last30Days.Tokens;
        AiCost = allTime.CostText;
        AiSessions = allTime.Sessions;
    }

    public void Apply()
    {
        Model.Name = Name.Trim();
        Model.RootPath = RootPath.Trim();
        Model.IsEnabled = IsEnabled;
        Model.ExcludePatterns = Exclusions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}

