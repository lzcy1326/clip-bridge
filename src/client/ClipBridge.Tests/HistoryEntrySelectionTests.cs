using System.Windows.Controls;
using ClipBridge.App;
using ClipBridge.Core.Models;

namespace ClipBridge.Tests;

public sealed class HistoryEntrySelectionTests
{
    [Fact]
    public async Task BindingSubscriptionsDoNotChangeWhichHistoryEntryIsSelected()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var text = Entry(ClipboardItemType.Text, "text", 1);
                var files = Entry(ClipboardItemType.Files, "[\"C:\\\\offline.txt\"]", 2);
                var image = Entry(ClipboardItemType.Image, null, 3);
                var list = new ListBox();
                list.Items.Add(text);
                list.Items.Add(files);
                list.Items.Add(image);
                list.SelectedItem = text;
                // WPF attaches/removes listeners as rows are realized or recycled.
                files.PropertyChanged += (_, _) => { };
                image.PropertyChanged += (_, _) => { };
                list.SelectedItem = files;
                Assert.Same(files, list.SelectedItem);
                list.SelectedItem = image;
                Assert.Same(image, list.SelectedItem);
                completed.SetResult();
            }
            catch (Exception exception) { completed.SetException(exception); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static HistoryListEntry Entry(ClipboardItemType type, string? text, int index) =>
        new(new(index.ToString(), "local", type, text, null, index.ToString(), 1, false, false), index);
}
