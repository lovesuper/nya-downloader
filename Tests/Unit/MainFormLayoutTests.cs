using Xunit;

namespace VideoDownloader.Tests.Unit;

[Trait("Category", "Unit")]
[Collection(nameof(SettingsEnvironment))]
public class MainFormLayoutTests
{
    static T WithForm<T>(Func<MainForm, T> look)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            using var dir = new TempDir();
            Environment.SetEnvironmentVariable("NYADOWNLOADER_DATA", dir.Path);
            try
            {
                using var form = new MainForm();
                result = look(form);
            }
            catch (Exception e) { error = e; }
            finally { Environment.SetEnvironmentVariable("NYADOWNLOADER_DATA", null); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new Exception("building the window failed", error);
        return result;
    }

    static IEnumerable<Control> TabOrder(Control parent) =>
        parent.Controls.Cast<Control>().OrderBy(c => c.TabIndex)
            .SelectMany(c => (c.TabStop && c.Controls.Count == 0 && c is not Label ? new[] { c } : []).Concat(TabOrder(c)));

    static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));

    [Fact]
    public void Tab_order_starts_at_the_link_and_goes_top_to_bottom()
    {
        var order = WithForm(f => TabOrder(f).Select(c => c.Name).ToList());
        var text = string.Join(", ", order);
        Assert.Equal("url", order[0]);
        Assert.True(order.IndexOf("outDir") < order.IndexOf("quality"), text);
        Assert.True(order.IndexOf("quality") < order.IndexOf("download"), text);
        Assert.True(order.IndexOf("download") < order.IndexOf("cancel"), text);
    }

    [Fact]
    public void Controls_have_automation_ids()
    {
        var names = WithForm(f => All(f).Select(c => c.Name).ToHashSet());
        Assert.Superset(new HashSet<string> { "url", "outDir", "download", "cancel", "quality", "video", "audio", "cover" }, names);
    }
}
