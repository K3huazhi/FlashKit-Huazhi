using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace FlashKit.App.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hasValue = value is not null;
        if (Invert)
        {
            hasValue = !hasValue;
        }

        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hasText = value is string text && !string.IsNullOrWhiteSpace(text);
        if (Invert)
        {
            hasText = !hasText;
        }

        return hasText ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is bool b ? !b : true;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is bool b ? !b : true;
}

public sealed class RiskToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is Core.Models.PartitionRisk risk
            ? risk switch
            {
                Core.Models.PartitionRisk.Critical => "SystemFillColorCriticalBrush",
                Core.Models.PartitionRisk.DataLoss => "SystemFillColorCautionBrush",
                _ => "TextFillColorSecondaryBrush"
            }
            : "TextFillColorSecondaryBrush";
        return ThemeBrush(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();

    internal static Brush ThemeBrush(string key)
    {
        if (Application.Current.Resources.TryGetValue(key, out var resource) && resource is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(Colors.Gray);
    }
}

public sealed class FolderIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is true ? "\uE8B7" : "\uE7C3";

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class TaskStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is Core.Models.OperationTaskStatus status
            ? status switch
            {
                Core.Models.OperationTaskStatus.Failed => "SystemFillColorCriticalBrush",
                Core.Models.OperationTaskStatus.Cancelled => "SystemFillColorCautionBrush",
                Core.Models.OperationTaskStatus.Running => "AccentTextFillColorPrimaryBrush",
                Core.Models.OperationTaskStatus.Succeeded => "SystemFillColorSuccessBrush",
                _ => "TextFillColorSecondaryBrush"
            }
            : "TextFillColorSecondaryBrush";
        return RiskToBrushConverter.ThemeBrush(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class LogKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is Core.Models.LogKind kind
            ? kind switch
            {
                Core.Models.LogKind.Error => "SystemFillColorCriticalBrush",
                Core.Models.LogKind.Warning => "SystemFillColorCautionBrush",
                Core.Models.LogKind.Command => "TextFillColorPrimaryBrush",
                _ => "TextFillColorSecondaryBrush"
            }
            : "TextFillColorSecondaryBrush";
        return RiskToBrushConverter.ThemeBrush(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class FlashStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value is Core.Models.FlashItemState state
            ? state switch
            {
                Core.Models.FlashItemState.Succeeded => "SystemFillColorSuccessBrush",
                Core.Models.FlashItemState.Failed => "SystemFillColorCriticalBrush",
                Core.Models.FlashItemState.Running => "AccentTextFillColorPrimaryBrush",
                Core.Models.FlashItemState.Cancelled => "SystemFillColorCautionBrush",
                _ => "TextFillColorSecondaryBrush"
            }
            : "TextFillColorSecondaryBrush";
        return RiskToBrushConverter.ThemeBrush(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        return RiskToBrushConverter.ThemeBrush(flag ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class ConnectionToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var key = value switch
        {
            Core.Models.DeviceConnectionState.Connected => "SystemFillColorSuccessBrush",
            Core.Models.DeviceConnectionState.Unauthorized => "SystemFillColorCautionBrush",
            Core.Models.DeviceConnectionState.Offline => "SystemFillColorCriticalBrush",
            Core.Models.DeviceConnectionState.Error => "SystemFillColorCriticalBrush",
            _ => "TextFillColorTertiaryBrush"
        };
        return RiskToBrushConverter.ThemeBrush(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
