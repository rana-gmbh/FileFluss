using System.Windows;

namespace FileFluss.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var rows = Enumerable.Range(0, 5000).Select(i => new Row($"File {i}.txt", i * 1024L, "Text Document")).ToList();
        LeftGrid.ItemsSource = rows;
        RightList.ItemsSource = rows;
    }

    public sealed record Row(string Name, long Size, string Kind);
}
