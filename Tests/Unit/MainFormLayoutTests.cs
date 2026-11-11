using Xunit;

namespace VideoDownloader.Tests.Unit;

[Trait("Category", "Unit")]
[Collection(nameof(SettingsEnvironment))]
public class MainFormLayoutTests
{
    static T WithForm<T>(Func<MainForm, T> look) => OnSta(() =>
    {
        using var dir = new TempDir();
        Environment.SetEnvironmentVariable("NYADOWNLOADER_DATA", dir.Path);
        try
        {
            using var form = new MainForm();
            return look(form);
        }
        finally { Environment.SetEnvironmentVariable("NYADOWNLOADER_DATA", null); }
    });

    internal static T OnSta<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception e) { error = e; }
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
        string[] expected = ["url", "outDir", "video", "quality", "subtitles", "from", "to", "chapters", "download", "cancel"];
        var positions = expected.Select(id => order.IndexOf(id)).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.True(positions.SequenceEqual(positions.Order()), text);
    }

    [Fact]
    public void Controls_have_automation_ids()
    {
        var names = WithForm(f => All(f).Select(c => c.Name).ToHashSet());
        Assert.Superset(new HashSet<string> { "url", "outDir", "download", "cancel", "quality", "video", "m4a", "mp3", "cover", "subtitles", "from", "to", "chapters",
                                              "appBanner", "appUpdateText", "appUpdateNow", "appNotes" }, names);
    }

    [Fact]
    public void Subtitle_choices_start_with_none()
    {
        var items = WithForm(f => ((ComboBox)All(f).Single(c => c.Name == "subtitles")).Items.Cast<object>().Select(o => o.ToString()).ToList());
        Assert.Equal(SubtitleLanguage.All.Select(l => l.Name), items);
    }
}

[Trait("Category", "Unit")]
public class PickDialogTests
{
    static IEnumerable<Control> All(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => All(c).Prepend(c));

    static T WithDialog<T>(PickDialog.Item[] items, Func<PickDialog, Func<string, Control>, T> look) => MainFormLayoutTests.OnSta(() =>
    {
        using var d = new PickDialog("Choose videos", "Heading", [new("#", 40), new("Title"), new("Length", 70)], items, "video", "videos");
        return look(d, name => All(d).Single(c => c.Name == name));
    });

    static void Click(Control button) =>
        typeof(Control).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(button, [EventArgs.Empty]);

    static readonly PickDialog.Item[] Three =
    [
        new(["1", "A", "1:00"], 60, false),
        new(["2", "B", "2:00"], 120, true),
        new(["3", "C", ""], null, true),
    ];

    [Fact]
    public void Shows_what_is_checked_and_the_total_length()
    {
        var (summary, ok, enabled, picked) = WithDialog(Three, (d, c) => (c("pickSummary").Text, c("pickOk").Text, c("pickOk").Enabled, d.Picked));
        Assert.Equal("2 of 3 selected, 2:00 in total", summary);
        Assert.Equal("Download 2 videos", ok);
        Assert.True(enabled);
        Assert.Equal([1, 2], picked);
    }

    [Fact]
    public void Select_all_and_none()
    {
        var (all, none, okNone, okAll) = WithDialog(Three, (d, c) =>
        {
            Click(c("pickAll"));
            var all = (d.Picked, c("pickSummary").Text);
            var okAll = c("pickOk").Text;
            Click(c("pickNone"));
            return (all, (d.Picked, c("pickSummary").Text), (c("pickOk").Text, c("pickOk").Enabled), okAll);
        });
        Assert.Equal([0, 1, 2], all.Picked);
        Assert.Equal("3 of 3 selected, 3:00 in total", all.Text);
        Assert.Equal("Download 3 videos", okAll);
        Assert.Empty(none.Picked);
        Assert.Equal("0 of 3 selected", none.Text);
        Assert.Equal(("Download", false), okNone);
    }

    [Fact]
    public void One_item_and_list_columns()
    {
        var (ok, columns, rows) = WithDialog([new(["1", "Only", "0:30"], 30, true)], (d, c) =>
        {
            var list = (ListView)c("pickList");
            return (c("pickOk").Text, list.Columns.Count, list.Items.Count);
        });
        Assert.Equal("Download 1 video", ok);
        Assert.Equal((3, 1), (columns, rows));
    }
}
