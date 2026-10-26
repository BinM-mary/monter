using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfApp1.Models;

namespace WpfApp1.ViewModels;

public sealed partial class MotorPresetViewModel : ObservableObject
{
    private readonly int maxPositionSteps;

    /// <param name="maxPositionSteps">
    /// 位置上限，取 0x15 目标位置的 16 bit 协议上限（65535）而不是设备回报的行程：
    /// 行程读数可能偏小或异常，用行程钳制会把已保存的预设值改坏。
    /// </param>
    public MotorPresetViewModel(string id, string displayName, int positionSteps, int maxPositionSteps)
    {
        Id = id;
        DisplayName = displayName;
        this.maxPositionSteps = Math.Max(1, maxPositionSteps);
        this.positionSteps = Math.Clamp(positionSteps, 0, this.maxPositionSteps);
        positionText = this.positionSteps.ToString(CultureInfo.InvariantCulture);
    }

    public string Id { get; }

    public string DisplayName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int positionSteps;

    /// <summary>
    /// 输入框的原始文本。超上限（&gt; 65535）或非数字的输入原样保留，
    /// 由保存时统一提示，避免绑定层把用户输入静默改成 65535。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionInputError))]
    private string positionText = string.Empty;

    public string Summary => $"已保存：{PositionSteps} step";

    /// <summary>输入合法时返回 <see langword="null"/>，否则返回可直接展示的原因。</summary>
    public string? PositionInputError => DescribePositionInputError(PositionText);

    public MotorPresetConfigurationItem ToConfigurationItem()
    {
        return new MotorPresetConfigurationItem
        {
            Id = Id,
            PositionSteps = PositionSteps
        };
    }

    partial void OnPositionTextChanged(string value)
    {
        // 合法输入实时同步到已保存值；超限/非法文本保留原样，等保存时提示。
        if (TryParsePositionText(value, out var position))
        {
            PositionSteps = position;
        }
    }

    partial void OnPositionStepsChanged(int value)
    {
        var normalizedValue = Math.Clamp(value, 0, maxPositionSteps);
        if (normalizedValue != value)
        {
            PositionSteps = normalizedValue;
            return;
        }

        // 位置被程序改写（读入当前位置、重新加载配置）时同步文本；用户输入不改写。
        if (!TryParsePositionText(PositionText, out var textValue) || textValue != value)
        {
            PositionText = value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private bool TryParsePositionText(string? text, out int position)
    {
        position = 0;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || !long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            || parsed < 0
            || parsed > maxPositionSteps)
        {
            return false;
        }

        position = (int)parsed;
        return true;
    }

    private string? DescribePositionInputError(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "位置为空，请输入 0～65535 的整数。";
        }

        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            // 位数多到 long 都装不下时同样是"太大"，不用报成无法识别。
            return trimmed.All(char.IsDigit)
                ? $"位置 {trimmed} 超过 16 bit 上限 {maxPositionSteps} step。"
                : $"位置需为 0～65535 的整数，当前“{trimmed}”无法识别。";
        }

        if (parsed < 0)
        {
            return $"位置不能为负数（当前 {parsed}）。";
        }

        return parsed > maxPositionSteps
            ? $"位置 {parsed} 超过 16 bit 上限 {maxPositionSteps} step。"
            : null;
    }
}
