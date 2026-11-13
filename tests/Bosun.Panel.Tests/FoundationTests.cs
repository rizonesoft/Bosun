using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Data;
using Xunit;

namespace Bosun.Panel.Tests;

public class FoundationTests
{
    [Fact]
    public void Foundation_binds_a_number_in_the_culture_it_is_given()
    {
        // 1234.5 as N2 is 1.234,50 in German and only with ICU: invariant globalization gives 1,234.50.
        var text = Sta.Run(() =>
        {
            var block = new TextBlock { Language = System.Windows.Markup.XmlLanguage.GetLanguage("de-DE") };
            block.SetBinding(TextBlock.TextProperty, new Binding { Source = 1234.5, StringFormat = "N2", ConverterCulture = new CultureInfo("de-DE") });
            return block.Text;
        });
        Assert.Equal("1.234,50", text);
    }

    [Fact]
    public void Foundation_panel_runs_with_globalization_on()
    {
        // The panel's own runtime configuration, as its build wrote it: the test host's could say anything.
        var config = Path.Combine(Path.GetDirectoryName(typeof(App).Assembly.Location)!, "Bosun.Panel.runtimeconfig.json");
        using var json = JsonDocument.Parse(File.ReadAllText(config));
        var options = json.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");
        Assert.False(options.GetProperty("System.Globalization.Invariant").GetBoolean());
    }

    [Fact]
    public void Foundation_panel_xaml_names_a_type_of_its_own()
    {
        // Loading the compiled resource dictionary proves the temporary markup project built and the type resolved.
        var name = Sta.Run(() =>
        {
            // The compiled BAML is loaded from the panel's assembly by its component path, which needs no Application
            // (an absolute pack:// address needs the scheme the Application registers).
            var resources = (System.Windows.ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/Bosun.Panel;component/Foundation.xaml", UriKind.Relative));
            return ((FoundationText)resources["ProductName"]).Text;
        });
        Assert.Equal("Bosun", name);
    }
}
