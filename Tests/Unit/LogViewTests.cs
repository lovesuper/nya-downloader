using System.Reflection;
using Xunit;

namespace VideoDownloader.Tests.Unit;

[Trait("Category", "Unit")]
public class LogViewTests
{
    static T With<T>(Func<LogView, T> work, int width = 400, int height = 120) => MainFormLayoutTests.OnSta(() =>
    {
        using var log = new LogView { Font = new Font("Consolas", 9f), Size = new Size(width, height) };
        return work(log);
    });

    static void Call(LogView log, string method, EventArgs e) =>
        typeof(LogView).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(log, [e]);

    static void Mouse(LogView log, string method, int x, int y, int clicks = 1) =>
        Call(log, method, new MouseEventArgs(MouseButtons.Left, clicks, x, y, 0));

    [Fact]
    public void Lines_are_appended_replaced_and_cleared()
    {
        var (text, replaced, after, cleared, value) = With(log =>
        {
            log.AppendLine("first");
            log.AppendLine("two\nlines\r\n");
            log.AppendLine("tab\there");
            var text = log.LogText;
            log.ReplaceLastLine("[progress] 50%");
            log.ReplaceLastLine("[progress] 100%");
            var replaced = log.LogText;
            log.AppendLine("next");
            var after = log.LineCount;
            var value = log.AccessibilityObject.Value;
            log.Clear();
            return (text, replaced, after, log.LogText, value);
        });
        Assert.Equal("first\r\ntwo\r\nlines\r\ntab    here\r\n", text);
        Assert.Equal("first\r\ntwo\r\nlines\r\n[progress] 100%\r\n", replaced);
        Assert.Equal(5, after);
        Assert.Equal("first\r\ntwo\r\nlines\r\n[progress] 100%\r\nnext\r\n", value);
        Assert.Equal("", cleared);
    }

    [Fact]
    public void Long_lines_wrap_at_spaces_and_long_words_are_cut()
    {
        var (columns, rows, split, words) = With(log =>
        {
            int columns = log.Columns;
            log.AppendLine(string.Join(" ", Enumerable.Repeat("word", columns)));
            var rows = log.RowTexts();
            log.Clear();
            log.AppendLine(new string('a', columns - 3) + " bbbbbb cc");
            var split = log.RowTexts();
            log.Clear();
            log.AppendLine(new string('x', columns * 3 + 1));
            return (columns, rows, split, log.RowTexts());
        });
        Assert.InRange(columns, 20, 80);
        Assert.All(rows, r => Assert.True(r.Length <= columns + 1, $"row longer than {columns}: '{r}'"));
        Assert.All(rows, r => Assert.Matches("^(word ?)+$", r));
        Assert.All(rows.SkipLast(1), r => Assert.EndsWith(" ", r));
        Assert.Equal(string.Join(" ", Enumerable.Repeat("word", columns)), string.Concat(rows));
        Assert.Equal([new string('a', columns - 3) + " ", "bbbbbb cc"], split);
        Assert.Equal([columns, columns, columns, 1], words.Select(w => w.Length));
    }

    [Fact]
    public void New_lines_scroll_into_view_unless_the_user_scrolled_up()
    {
        var (following, held, back) = With(log =>
        {
            for (int i = 0; i < 40; i++) log.AppendLine($"line {i}");
            var following = (log.AtBottom, log.FirstVisibleRow);
            log.ScrollTo(3);
            log.AppendLine("more");
            var held = (log.AtBottom, log.FirstVisibleRow);
            log.ScrollTo(int.MaxValue);
            log.AppendLine("and more");
            return (following, held, (log.AtBottom, log.FirstVisibleRow));
        });
        Assert.True(following.AtBottom);
        Assert.True(following.FirstVisibleRow > 30, $"top row {following.FirstVisibleRow}");
        Assert.Equal((false, 3), held);
        Assert.Equal((true, following.FirstVisibleRow + 2), back);
    }

    [Fact]
    public void Text_is_selected_with_the_mouse_and_the_keyboard()
    {
        var (dragged, fromEnd, word, all, none) = With(log =>
        {
            log.AppendLine("alpha beta");
            log.AppendLine("gamma");
            log.AppendLine("delta");
            Mouse(log, "OnMouseDown", 0, 1);
            Mouse(log, "OnMouseMove", 2000, 1);
            Mouse(log, "OnMouseUp", 2000, 1);
            var dragged = log.SelectedText;
            Mouse(log, "OnMouseDown", 300, 1);
            Mouse(log, "OnMouseMove", 50, 5000);
            Mouse(log, "OnMouseUp", 50, 5000);
            var fromEnd = log.SelectedText;
            Mouse(log, "OnMouseDoubleClick", 2, 1, 2);
            var word = log.SelectedText;
            Call(log, "OnKeyDown", new KeyEventArgs(Keys.Control | Keys.A));
            var all = log.SelectedText;
            Mouse(log, "OnMouseDown", 0, 1);
            Mouse(log, "OnMouseUp", 0, 1);
            return (dragged, fromEnd, word, all, log.HasSelection);
        });
        Assert.Equal("alpha beta", dragged);
        Assert.Equal("\r\ngamma\r\ndelta", fromEnd);
        Assert.Equal("alpha", word);
        Assert.Equal("alpha beta\r\ngamma\r\ndelta", all);
        Assert.False(none);
    }

    [Fact]
    public void Keys_scroll_the_log()
    {
        var tops = With(log =>
        {
            for (int i = 0; i < 60; i++) log.AppendLine($"line {i}");
            var tops = new List<int> { log.FirstVisibleRow };
            foreach (var key in new[] { Keys.Home, Keys.Down, Keys.PageDown, Keys.Up, Keys.End })
            {
                Call(log, "OnKeyDown", new KeyEventArgs(key));
                tops.Add(log.FirstVisibleRow);
            }
            tops.Add(log.VisibleRows);
            return tops;
        });
        int page = tops[^1], bottom = tops[0];
        Assert.Equal([bottom, 0, 1, 1 + page, page, bottom], tops.Take(6));
    }
}
