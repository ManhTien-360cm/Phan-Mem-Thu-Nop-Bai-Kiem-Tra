using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ExamTransfer.Desktop.Views;
using Xunit;

namespace ExamTransfer.Desktop.Tests;

public sealed class ResponsiveVisualEvidenceTests
{
    [Fact]
    public void LongQuizContentRendersWithoutActionOverlapAtRequiredViewports()
    {
        var scenarios = new[]
        {
            new RenderScenario(1366, 768, 96, "1366x768-100pct"),
            new RenderScenario(1280, 720, 120, "1280x720-125pct"),
            new RenderScenario(1024, 768, 144, "1024x768-150pct")
        };
        var outputRoot = Environment.GetEnvironmentVariable("EXAMTRANSFER_UI_EVIDENCE_DIR");
        if (string.IsNullOrWhiteSpace(outputRoot))
            outputRoot = Path.Combine(Path.GetTempPath(), "examtransfer-responsive-evidence");
        Directory.CreateDirectory(outputRoot);

        WpfTestHost.Run(() =>
        {
            foreach (var scenario in scenarios)
                RenderAndAssert(scenario, outputRoot);
        });
    }

    private static void RenderAndAssert(RenderScenario scenario, string outputRoot)
    {
        var scale = scenario.Dpi / 96d;
        var logicalWidth = scenario.PixelWidth / scale;
        var logicalHeight = scenario.PixelHeight / scale;
        var quiz = new StudentQuizView
        {
            DataContext = QuizFixture.Create(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var shell = BuildDesktopShell(quiz, logicalWidth, logicalHeight);

        shell.Measure(new Size(logicalWidth, logicalHeight));
        shell.Arrange(new Rect(0, 0, logicalWidth, logicalHeight));
        shell.UpdateLayout();

        var buttons = Descendants<Button>(quiz).Where(button => button.ActualWidth > 0).ToArray();
        Assert.Equal(5, buttons.Length);
        var buttonBounds = buttons.Select(button => BoundsWithin(button, quiz)).ToArray();
        Assert.All(buttonBounds, bounds =>
        {
            Assert.True(bounds.Left >= 0 && bounds.Right <= quiz.ActualWidth + 0.5);
            Assert.True(bounds.Top >= 0 && bounds.Bottom <= quiz.ActualHeight + 0.5);
        });
        for (var left = 0; left < buttonBounds.Length; left++)
        for (var right = left + 1; right < buttonBounds.Length; right++)
            Assert.False(buttonBounds[left].IntersectsWith(buttonBounds[right]));

        var scroller = Assert.Single(Descendants<ScrollViewer>(quiz));
        Assert.Equal(ScrollBarVisibility.Disabled, scroller.HorizontalScrollBarVisibility);
        Assert.True(scroller.ViewportHeight >= 100);
        var longTextBlocks = Descendants<TextBlock>(quiz)
            .Where(block => block.Text.StartsWith("Dữ liệu kiểm thử responsive", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, longTextBlocks.Length);
        Assert.All(longTextBlocks, block =>
        {
            Assert.Equal(TextWrapping.Wrap, block.TextWrapping);
            Assert.True(block.ActualWidth <= quiz.ActualWidth);
            Assert.True(block.ActualHeight >= block.FontSize * 1.5);
        });

        var bitmap = new RenderTargetBitmap(
            scenario.PixelWidth,
            scenario.PixelHeight,
            scenario.Dpi,
            scenario.Dpi,
            PixelFormats.Pbgra32);
        bitmap.Render(shell);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var outputPath = Path.Combine(outputRoot, $"student-quiz-{scenario.Name}.png");
        using var output = File.Create(outputPath);
        encoder.Save(output);
    }

    private static Grid BuildDesktopShell(
        StudentQuizView quiz,
        double logicalWidth,
        double logicalHeight)
    {
        var shell = new Grid
        {
            Width = logicalWidth,
            Height = logicalHeight,
            Background = new SolidColorBrush(Color.FromRgb(245, 247, 250))
        };
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        shell.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        shell.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(25, 37, 54)),
            Child = new TextBlock
            {
                Text = "ExamTransfer\n\nPHÒNG THI LAN\n\nHọc sinh Nguyễn Văn Minh Anh",
                Foreground = Brushes.White,
                Margin = new Thickness(18),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            }
        });
        var main = new Grid();
        Grid.SetColumn(main, 1);
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(56) });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.Children.Add(new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(220, 225, 232)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new TextBlock
            {
                Text = "Thi trắc nghiệm · Kết nối LAN đã xác thực",
                Margin = new Thickness(20, 0, 20, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontWeight = FontWeights.SemiBold
            }
        });
        Grid.SetRow(quiz, 1);
        main.Children.Add(quiz);
        shell.Children.Add(main);
        return shell;
    }

    private static Rect BoundsWithin(FrameworkElement element, Visual ancestor)
    {
        var origin = element.TransformToAncestor(ancestor).Transform(new Point());
        return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private sealed record RenderScenario(
        int PixelWidth,
        int PixelHeight,
        int Dpi,
        string Name);

    private sealed class QuizFixture
    {
        public required string TimeLeft { get; init; }
        public required string ClockStatus { get; init; }
        public required string ProgressText { get; init; }
        public required IReadOnlyList<QuestionFixture> Questions { get; init; }
        public required ReviewFixture Review { get; init; }
        public bool CanEditAnswers => true;
        public string Result => "Đáp án được lưu an toàn trên thiết bị";
        public string Status => "Thông báo trạng thái dài: đang đồng bộ dữ liệu qua mạng LAN và sẽ tự thử lại nếu kết nối tạm thời gián đoạn.";
        public string ReviewSummary => "Chưa chốt bài";
        public string ReviewComment => "Nút chốt bài và điều hướng vẫn truy cập được ở kích thước hiện tại.";

        public static QuizFixture Create() => new()
        {
            TimeLeft = "00:42:18",
            ClockStatus = "Đồng hồ máy chủ đang hoạt động",
            ProgressText = "Đã trả lời 1/2 câu",
            Review = new ReviewFixture { Questions = [] },
            Questions =
            [
                new QuestionFixture
                {
                    Order = 1,
                    Points = 5,
                    Text = LongText("Câu hỏi"),
                    Choices =
                    [
                        new ChoiceFixture { Text = LongText("Đáp án A"), IsSelected = true },
                        new ChoiceFixture { Text = LongText("Đáp án B") }
                    ]
                }
            ]
        };

        private static string LongText(string prefix) =>
            $"Dữ liệu kiểm thử responsive — {prefix}: nội dung tiếng Việt rất dài có dấu và nhiều khoảng trắng, " +
            "được lặp lại để buộc giao diện hiển thị thành nhiều dòng mà không cắt chữ, đè chữ hoặc chồng lên control bên cạnh.";
    }

    private sealed class ReviewFixture
    {
        public required IReadOnlyList<object> Questions { get; init; }
    }

    private sealed class QuestionFixture
    {
        public int Order { get; init; }
        public decimal Points { get; init; }
        public required string Text { get; init; }
        public required IReadOnlyList<ChoiceFixture> Choices { get; init; }
    }

    private sealed class ChoiceFixture
    {
        public required string Text { get; init; }
        public bool IsSelected { get; set; }
    }
}
