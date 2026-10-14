using System.Windows.Data;
using System.Windows.Markup;
using D2cDataUpload.Core;

namespace D2cDataUpload.App;

/// <summary>
/// XAML-Kurzform für übersetzte Texte, die sich beim Sprachwechsel sofort aktualisieren:
///   Header="{l:Tr Menu_File}"
/// entspricht {Binding [Menu_File], Source={x:Static core:Loc.Instance}}.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TrExtension : MarkupExtension
{
    public TrExtension() { }
    public TrExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]") { Source = Loc.Instance, Mode = BindingMode.OneWay };
        return binding.ProvideValue(serviceProvider);
    }
}
