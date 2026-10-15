using CommunityToolkit.Mvvm.ComponentModel;
using WpfApp1.Models;

namespace WpfApp1.ViewModels;

public sealed partial class MotorPresetViewModel : ObservableObject
{
    private int totalTravelSteps;

    public MotorPresetViewModel(string id, string displayName, int positionSteps, int totalTravelSteps)
    {
        Id = id;
        DisplayName = displayName;
        this.totalTravelSteps = totalTravelSteps;
        this.positionSteps = Math.Clamp(positionSteps, 0, totalTravelSteps);
    }

    public string Id { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int positionSteps;

    public string Summary => $"已保存：{PositionSteps} step";

    public void SetTotalTravelSteps(int value)
    {
        totalTravelSteps = value;
        PositionSteps = Math.Clamp(PositionSteps, 0, totalTravelSteps);
        OnPropertyChanged(nameof(Summary));
    }

    public MotorPresetConfigurationItem ToConfigurationItem()
    {
        return new MotorPresetConfigurationItem
        {
            Id = Id,
            PositionSteps = PositionSteps
        };
    }

    partial void OnPositionStepsChanged(int value)
    {
        var normalizedValue = Math.Clamp(value, 0, totalTravelSteps);
        if (normalizedValue != value)
        {
            PositionSteps = normalizedValue;
        }
    }
}
