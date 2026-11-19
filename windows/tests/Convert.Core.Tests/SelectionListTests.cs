using Convert.Core;
using Xunit;

namespace Convert.Core.Tests;

/// <summary>
/// A long selection reaches Convert.exe as a list file (<c>--files</c>). Convert deletes the list after
/// reading it, but only a list the context menu wrote: <c>--files</c> can be pointed at any file.
/// </summary>
public sealed class SelectionListTests : IDisposable
{
    private readonly TestFolder dir = new();

    public void Dispose() => dir.Dispose();

    [Fact]
    public void AListFromTheContextMenuIsReadAndRemoved()
    {
        var paths = Enumerable.Range(1, 600).Select(i => $@"C:\Photos\Été 2024\image {i} – “final”.jpg").ToList();
        var list = SelectionList.Write(paths);
        try
        {
            Assert.True(SelectionList.IsOurs(list));
            Assert.Equal(paths, SelectionList.Read(list));
            Assert.False(File.Exists(list));
        }
        finally { File.Delete(list); }
    }

    [Fact]
    public void AnyOtherFileIsReadButNeverDeleted()
    {
        // Somebody's own list of files, or any text file at all.
        var mine = dir.Write("my files.txt", "C:\\a.mp3\r\n\r\nC:\\b.mp3\r\n");
        Assert.Equal([@"C:\a.mp3", @"C:\b.mp3"], SelectionList.Read(mine));
        Assert.True(File.Exists(mine));

        // The right kind of name in the wrong place, and the wrong kind of name in the right place.
        var name = $"convert-{Guid.NewGuid():N}.txt";
        var elsewhere = dir.Write(name, "C:\\a.mp3\n");
        var inTemp = Path.Combine(Path.GetTempPath(), $"notes-{Guid.NewGuid():N}.txt");
        File.WriteAllText(inTemp, "C:\\a.mp3\n");
        try
        {
            SelectionList.Read(elsewhere);
            SelectionList.Read(inTemp);
            Assert.True(File.Exists(elsewhere));
            Assert.True(File.Exists(inTemp));
        }
        finally { File.Delete(inTemp); }
    }

    [Theory]
    [InlineData(@"convert-0123456789abcdef0123456789abcdef.txt", true)]
    [InlineData(@"convert-0123456789ABCDEF0123456789ABCDEF.txt", false)]   // the menu writes lower case
    [InlineData(@"convert-0123456789abcdef0123456789abcde.txt", false)]
    [InlineData(@"convert-0123456789abcdef0123456789abcdef.txt.bak", false)]
    [InlineData(@"convert-zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz.txt", false)]
    [InlineData(@"sub\convert-0123456789abcdef0123456789abcdef.txt", false)]
    [InlineData(@"..\convert-0123456789abcdef0123456789abcdef.txt", false)]
    [InlineData(@"important.docx", false)]
    public void OnlyTheMenusOwnNamesInTheTempFolderCount(string name, bool ours)
    {
        Assert.Equal(ours, SelectionList.IsOurs(Path.Combine(Path.GetTempPath(), name)));
    }

    [Fact]
    public void AMissingOrUnreadableListIsAnEmptySelection()
    {
        Assert.Empty(SelectionList.Read(dir.File("gone.txt")));
        Assert.Empty(SelectionList.Read(dir.Path));
    }
}
