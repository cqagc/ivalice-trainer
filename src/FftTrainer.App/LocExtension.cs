using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using FftTrainer.Core;

namespace FftTrainer.App;

// XAML 里写 {local:Loc battle.title}：绑定到 Localizer 的索引器，切换语言时自动刷新
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]") { Source = Localizer.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}

// 带绑定值的文字：{local:LocFormat ui.roster.count, Path=Units.Count}
// 生成 MultiBinding（值 + Localizer 索引器），值或语言任一变化都会重新 string.Format
[MarkupExtensionReturnType(typeof(object))]
public sealed class LocFormatExtension(string key) : MarkupExtension
{
    public string Key { get; set; } = key;
    public string Path { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new MultiBinding { Converter = LocFormatConverter.Instance, Mode = BindingMode.OneWay };
        binding.Bindings.Add(new Binding(Path) { Mode = BindingMode.OneWay });
        binding.Bindings.Add(new Binding($"[{Key}]") { Source = Localizer.Instance, Mode = BindingMode.OneWay });
        return binding.ProvideValue(serviceProvider);
    }
}

public sealed class LocFormatConverter : IMultiValueConverter
{
    public static LocFormatConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[1] is string format && values[0] != System.Windows.DependencyProperty.UnsetValue
            ?string.Format(Localizer.Instance.Culture, format, values[0])
            : "";

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
