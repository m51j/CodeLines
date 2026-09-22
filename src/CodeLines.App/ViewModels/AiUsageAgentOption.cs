using CommunityToolkit.Mvvm.ComponentModel;

namespace CodeLines.App.ViewModels;

/// <summary>One AI agent in Settings: whether it is read, and what the last refresh found.</summary>
public sealed partial class AiUsageAgentOption(string id, string label, bool isEnabled) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    [ObservableProperty] private bool isEnabled = isEnabled;
    [ObservableProperty] private string status = "";
}
