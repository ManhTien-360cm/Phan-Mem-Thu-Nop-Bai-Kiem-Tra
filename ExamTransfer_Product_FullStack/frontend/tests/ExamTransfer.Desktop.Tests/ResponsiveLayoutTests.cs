using System.IO;
using System.Xml.Linq;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class ResponsiveLayoutTests
{
    [Fact]
    public void MainWindowAllowsCompactDesktopSizing()
    {
        var document = XDocument.Load(FindView("MainWindow.xaml"));
        var window = Assert.IsType<XElement>(document.Root);

        Assert.Equal("640", window.Attribute("MinWidth")?.Value);
        Assert.Equal("480", window.Attribute("MinHeight")?.Value);
        Assert.Contains(
            window.Descendants().Where(element => element.Name.LocalName == "ColumnDefinition"),
            column => column.Attribute("Width")?.Value == "220");
    }

    [Fact]
    public void QuizActionsAndDynamicAnswerTextCanWrap()
    {
        var source = File.ReadAllText(FindView("StudentQuizView.xaml"));
        var document = XDocument.Parse(source);

        Assert.Contains(
            document.Descendants().Where(element => element.Name.LocalName == "WrapPanel"),
            panel => panel.Attributes().Any(attribute => attribute.Name.LocalName == "Grid.Row" && attribute.Value == "1"));
        Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("<CheckBox Content=\"{Binding Text}\"", source, StringComparison.Ordinal);
        Assert.True(Count(source, "Text=\"{Binding Text}\" TextWrapping=\"Wrap\"") >= 3);
    }

    [Theory]
    [InlineData("StudentWaitingView.xaml")]
    [InlineData("StudentDownloadView.xaml")]
    [InlineData("StudentSubmissionView.xaml")]
    public void TransferScreensConstrainStatusTextToRemainingWidth(string viewName)
    {
        var source = File.ReadAllText(FindView(viewName));

        Assert.Contains("Grid.Column=\"1\" Text=\"{Binding Status}\"", source, StringComparison.Ordinal);
        Assert.Contains("TextWrapping=\"Wrap\"", source, StringComparison.Ordinal);
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        for (var index = 0; (index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
        {
            count++;
        }
        return count;
    }

    private static string FindView(string name)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, "frontend", "src", "ExamTransfer.Desktop", "Views", name);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException(name);
    }
}
