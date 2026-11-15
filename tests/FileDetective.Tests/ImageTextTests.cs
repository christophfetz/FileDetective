using System.Text;
using FileDetective.Core;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace FileDetective.Tests;

/// <summary>Stand-in for the Windows text recognition: the "image" is the text itself, padded with spaces; every call is counted.</summary>
internal sealed class FakeRecognizer : IImageTextRecognizer
{
    public bool IsAvailable { get; set; } = true;

    public int Calls { get; private set; }

    public List<string> Texts { get; } = [];

    public string Recognize(Stream image, CancellationToken cancellationToken)
    {
        Calls++;
        using var reader = new StreamReader(image, Encoding.UTF8);
        string text = reader.ReadToEnd().Trim();
        if (text == "BROKEN")
        {
            throw new InvalidDataException("not an image");
        }

        Texts.Add(text);
        return text;
    }

    /// <summary>The content of a fake image with this text: at least <paramref name="size"/> bytes (default 6,000, above 5 KB).</summary>
    public static byte[] Image(string text, int size = 6_000)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var content = new byte[Math.Max(size, bytes.Length)];
        Array.Fill(content, (byte)' ');
        bytes.CopyTo(content, 0);
        return content;
    }
}

/// <summary>Directory, fake files and fake recognizer for the image text service; drive R: is local, N: a network drive.</summary>
internal sealed class ImageTextFixture : IDisposable
{
    public const string Folder = @"R:\Shots";

    private readonly TempDir _dir = new();

    public ImageTextFixture()
    {
        F = new StorageFixture(_dir);
        Root = F.Store.AddRoot(@"R:\", null, RootKind.Local);
        FolderId = F.Add(Root, Root.RootEntryId, "Shots", true);
        Texts = new ImageTextIndex();
        Service = CreateService();
    }

    public StorageFixture F { get; private set; }

    public RootInfo Root { get; }

    public int FolderId { get; }

    public FakeLocalFiles Files { get; } = new();

    public FakeRecognizer Recognizer { get; } = new();

    public DriveDiscovery Drives { get; } = new(new DuplicateDriveSource());

    public ImageTextIndex Texts { get; private set; }

    public ImageTextService Service { get; private set; }

    public static long Seconds(long ticks) => IndexStore.ToUnixSeconds(new DateTime(ticks, DateTimeKind.Utc));

    /// <summary>A file in the directory and in the fake file system with the same size and time.</summary>
    public int AddImage(int parent, string parentPath, string name, string text, long? ticks = null, FileAttributes attributes = FileAttributes.Archive, int size = 6_000)
    {
        var content = FakeRecognizer.Image(text, size);
        long t = ticks ?? FakeLocalFiles.BaseTicks;
        Files.Add(parentPath + "\\" + name, content, t, attributes);
        int id = F.Store.AllocateId();
        var batch = new ChangeBatch();
        batch.Inserts.Add(new EntryInsert(id, parent, name, false, content.Length, Seconds(t), attributes));
        F.Store.Apply(Root.Id, batch);
        return id;
    }

    public SearchEngine Engine()
        => new(F.Index, new DatabaseEntryResolver(F.Db), F.Usage,
            new ImageTextSearch(Texts, new DatabaseEntryResolver(F.Db), F.Store.GetImageTexts, () => Service.Folders));

    public long Rows() => Scalar("SELECT COUNT(*) FROM ocr");

    public long Scalar(string sql)
    {
        using var c = F.Db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Program restart: database, memory and service anew; the directory is loaded again.</summary>
    public void Restart()
    {
        Service.Dispose();
        F.Restart();
        F.Load();
        Texts = new ImageTextIndex();
        Service = CreateService();
    }

    public void Enable(params string[] folders)
    {
        Service.Configure(true, folders.Length == 0 ? [Folder] : folders);
        Service.ProcessPending();
    }

    private ImageTextService CreateService()
        => new(F.Store, F.Index, Texts, Recognizer, Files, new DatabaseEntryResolver(F.Db), new ImageTextServiceOptions
        {
            RunWorker = false,
            IsLocalPath = path => Drives.Describe(path) is { Kind: RootKind.Local },
        });

    public void Dispose()
    {
        Service.Dispose();
        F.Dispose();
        _dir.Dispose();
    }
}

public class ImageTextRuleTests
{
    [Theory]
    [InlineData("shot.png", 6_000, ImageSkipReason.None)]
    [InlineData("SHOT.PNG", 6_000, ImageSkipReason.None)]
    [InlineData("photo.jpg", 6_000, ImageSkipReason.None)]
    [InlineData("photo.jpeg", 6_000, ImageSkipReason.None)]
    [InlineData("scan.bmp", 6_000, ImageSkipReason.None)]
    [InlineData("anim.gif", 6_000, ImageSkipReason.None)]
    [InlineData("fax.tif", 6_000, ImageSkipReason.None)]
    [InlineData("fax.tiff", 6_000, ImageSkipReason.None)]
    [InlineData("doc.pdf", 6_000, ImageSkipReason.NotAnImage)]
    [InlineData("pic.webp", 6_000, ImageSkipReason.NotAnImage)]
    [InlineData(".png", 6_000, ImageSkipReason.NotAnImage)]
    [InlineData("preview.thumb.jpg", 6_000, ImageSkipReason.Thumbnail)]
    [InlineData("Preview.THUMB.JPG", 6_000, ImageSkipReason.Thumbnail)]
    [InlineData("tiny.png", 5 * 1024 - 1, ImageSkipReason.TooSmall)]
    [InlineData("edge.png", 5 * 1024, ImageSkipReason.None)]
    [InlineData("edge.png", 50L * 1024 * 1024, ImageSkipReason.None)]
    [InlineData("huge.png", 50L * 1024 * 1024 + 1, ImageSkipReason.TooLarge)]
    public void EndingsThumbnailsAndSizes(string name, long size, ImageSkipReason expected)
        => Assert.Equal(expected, ImageTextRules.Check(name, size, FileAttributes.Archive));

    [Theory]
    [InlineData(0x0040_0000)] // RecallOnDataAccess
    [InlineData((int)FileAttributes.Offline)]
    [InlineData(0x0004_0000)] // RecallOnOpen
    public void CloudFiles_AreSkipped_WhateverTheirSize(int attribute)
        => Assert.Equal(ImageSkipReason.CloudOnly, ImageTextRules.Check("shot.png", 6_000, FileAttributes.Archive | (FileAttributes)attribute));

    [Fact]
    public void NetworkFolders_AreRefused()
    {
        var drives = new DriveDiscovery(new DuplicateDriveSource());
        Assert.Equal(ImageFolderCheck.NotLocal, ImageTextFolders.Check(@"\\server\share\Shots", drives, [], out _));
        Assert.Equal(ImageFolderCheck.NotLocal, ImageTextFolders.Check(@"N:\Shots", drives, [], out _));
        Assert.Equal(ImageFolderCheck.NotLocal, ImageTextFolders.Check(@"U:\Shots", drives, [], out _));
        Assert.Equal(ImageFolderCheck.Invalid, ImageTextFolders.Check(@"Shots", drives, [], out _));
        Assert.Equal(ImageFolderCheck.Ok, ImageTextFolders.Check(@"r:\shots\", drives, [], out string? path));
        Assert.Equal(@"R:\shots", path);
        Assert.Equal(ImageFolderCheck.AlreadyInList, ImageTextFolders.Check(@"R:\SHOTS", drives, [@"R:\Shots"], out _));

        // A UNC path in settings.json written by hand never survives the clean-up.
        Assert.Equal([@"R:\Shots"], ImageTextFolders.Clean([@"\\server\share\x", @"R:\Shots\", @"r:\shots", " "]));
    }

    [Fact]
    public void Effective_IsTheScreenshotsFolder_UntilTheListIsSet()
    {
        var settings = new AppSettings();
        Assert.Equal([@"C:\Users\anna\Pictures\Screenshots"], ImageTextFolders.Effective(settings, () => @"C:\Users\anna\Pictures\Screenshots\"));
        Assert.Empty(ImageTextFolders.Effective(settings, () => null));
        Assert.Empty(ImageTextFolders.Effective(settings, () => @"\\server\redirected\Screenshots"));
        Assert.Equal([@"D:\Shots"], ImageTextFolders.Effective(settings with { OcrFolders = [@"D:\Shots"] }, () => @"C:\x"));
        Assert.Empty(ImageTextFolders.Effective(settings with { OcrFolders = [] }, () => @"C:\x"));
    }

    [Fact]
    public void Settings_DefaultOff_AndFoldersAreIncludedInTheScanOnlyWhenOn()
    {
        var settings = new AppSettings();
        Assert.False(settings.OcrEnabled);
        Assert.False(settings.OcrHintDismissed);
        Assert.Null(settings.OcrFolders);

        // Below an excluded path (AppData) a folder of the image text is still recorded while the image text is on.
        var folder = @"C:\Users\anna\AppData\Local\Shots";
        var on = settings with { OcrEnabled = true, OcrFolders = [folder] };
        Assert.False(on.CreateExcludeRules().IsExcluded(@"C:\Users\anna", "AppData", true, FileAttributes.Directory));
        Assert.True(on.CreateExcludeRules().IsExcluded(@"C:\Users\anna\AppData\Local", "Temp", true, FileAttributes.Directory));
        var off = on with { OcrEnabled = false };
        Assert.True(off.CreateExcludeRules().IsExcluded(@"C:\Users\anna", "AppData", true, FileAttributes.Directory));
    }

    [Fact]
    public void FoldersOutsideEveryLocation_BecomeLocations()
    {
        var roots = new List<RootInfo> { new(1, @"C:\Users\anna", null, RootKind.Local, true, null, null, ScanStatus.Complete, 0, 0, 1) };
        var missing = ImageTextFolders.MissingLocations(
            [@"C:\Users\anna\Pictures\Screenshots", @"D:\Shots\Old", @"D:\Shots", @"E:\Gone", @"\\server\share"], roots,
            path => path != @"E:\Gone");
        Assert.Equal([@"D:\Shots"], missing);
    }

    [Fact]
    public void TabCycle_HasImageText_OnlyWhenSwitchedOn()
    {
        Assert.Equal(SearchMode.ImageText, BarState.CycleMode(SearchMode.Programs, false, imageText: true));
        Assert.Equal(SearchMode.All, BarState.CycleMode(SearchMode.ImageText, false, imageText: true));
        Assert.Equal(SearchMode.ImageText, BarState.CycleMode(SearchMode.All, true, imageText: true));
        Assert.Equal(SearchMode.Programs, BarState.CycleMode(SearchMode.ImageText, true, imageText: true));

        // Switched off: Image text is not in the cycle, and a bar still in that mode goes back to All.
        var seen = new HashSet<SearchMode>();
        var mode = SearchMode.All;
        for (int i = 0; i < 10; i++)
        {
            mode = BarState.CycleMode(mode, false, imageText: false);
            seen.Add(mode);
        }

        Assert.DoesNotContain(SearchMode.ImageText, seen);
        Assert.Equal(4, seen.Count);
        Assert.Equal(SearchMode.All, BarState.CycleMode(SearchMode.ImageText, false, imageText: false));
    }
}

public class ImageTextSnippetTests
{
    [Fact]
    public void SearchForm_JoinsLines_AndSpellsOutUmlauts()
    {
        Assert.Equal("rechnung herr mueller strasse 5", Encoding.UTF8.GetString(ImageTextIndex.SearchForm("Rechnung\r\nHerr  Müller\nStraße 5")));
    }

    [Fact]
    public void Snippet_MarksTheTerms_InTheOriginalText()
    {
        var query = SearchQuery.Parse("mueller rech", SearchMode.ImageText);
        var snippet = ImageTextSnippet.Create("Rechnung\nHerr Müller", query);
        Assert.Equal("Rechnung Herr Müller", snippet.Text);
        Assert.Equal(
            [new TextSegment("Rech", true), new TextSegment("nung Herr ", false), new TextSegment("Müller", true)],
            snippet.Segments);
    }

    [Fact]
    public void Snippet_CutsAroundTheFirstHit_InALongText()
    {
        string before = string.Join(' ', Enumerable.Range(1, 60).Select(i => "wort" + i));
        string after = string.Join(' ', Enumerable.Range(1, 60).Select(i => "rest" + i));
        var query = SearchQuery.Parse("fehlercode", SearchMode.ImageText);
        var snippet = ImageTextSnippet.Create(before + " Fehlercode 0x80070005 " + after, query);

        Assert.StartsWith("…", snippet.Text, StringComparison.Ordinal);
        Assert.EndsWith("…", snippet.Text, StringComparison.Ordinal);
        Assert.Contains("Fehlercode 0x80070005", snippet.Text, StringComparison.Ordinal);
        Assert.True(snippet.Text.Length <= ImageTextSnippet.Length + 2);
        int hit = snippet.Text.IndexOf("Fehlercode", StringComparison.Ordinal);
        Assert.InRange(hit, 1, ImageTextSnippet.Before + 2);
        Assert.Equal("Fehlercode", Assert.Single(snippet.Segments, s => s.Highlighted).Text);
        Assert.Equal(snippet.Text, string.Concat(snippet.Segments.Select(s => s.Text)));
    }

    [Fact]
    public void Snippet_WithoutQuery_IsTheBeginning()
    {
        var snippet = ImageTextSnippet.Create(new string('a', 300), null);
        Assert.Equal(ImageTextSnippet.Length + 1, snippet.Text.Length);
        Assert.DoesNotContain(snippet.Segments, s => s.Highlighted);
        Assert.Empty(ImageTextSnippet.Create("  \n ", null).Segments);
    }

    [Fact]
    public void Index_FindsImagesWithAllTerms_NewestFirst()
    {
        var index = new ImageTextIndex();
        index.Set(1, "Rechnung Müller", 100);
        index.Set(2, "Rechnung Meier", 300);
        index.Set(3, "Angebot Müller Rechnung", 200);
        index.Set(4, string.Empty, 400);

        Assert.Equal([3, 1], index.Find(SearchQuery.Parse("mueller rechnung", SearchMode.ImageText)));
        Assert.Equal([2, 3, 1], index.Find(SearchQuery.Parse("RECHNUNG", SearchMode.ImageText)));
        Assert.Equal([4, 2, 3, 1], index.Find(SearchQuery.Parse(string.Empty, SearchMode.ImageText)));
        Assert.Empty(index.Find(SearchQuery.Parse("rechnung schulz", SearchMode.ImageText)));
    }
}

public sealed class ImageTextServiceTests : IDisposable
{
    private readonly ImageTextFixture _x = new();

    public void Dispose() => _x.Dispose();

    private static string P(string name) => ImageTextFixture.Folder + "\\" + name;

    [Fact]
    public void SwitchedOff_NoImageIsOpened_NothingIsQueued_AndTheModeHasNoHits()
    {
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "a.png", "Rechnung");
        _x.Service.ProcessPending();
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "b.png", "Rechnung");
        _x.Service.Configure(false, [ImageTextFixture.Folder]);
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "c.png", "Rechnung");
        _x.Service.ProcessPending();

        Assert.Equal(0, _x.Files.TotalCalls);
        Assert.Equal(0, _x.Recognizer.Calls);
        Assert.Equal(new ImageTextProgress(false, 0, 0, 0), _x.Service.Progress);
        Assert.Equal(0, _x.Rows());
        Assert.Empty(_x.Engine().Search("rechnung", SearchMode.ImageText).Hits);
    }

    [Fact]
    public void SwitchingOn_ReadsTheImages_AndTheModeFindsThem_NeverAll()
    {
        int a = _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "Screenshot 1.png", "Fehler 0x80070005 beim Speichern", FakeLocalFiles.BaseTicks);
        int b = _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "Screenshot 2.png", "Speichern erfolgreich", FakeLocalFiles.BaseTicks + TimeSpan.TicksPerHour);
        int other = _x.AddImage(_x.Root.RootEntryId, @"R:", "elsewhere.png", "Speichern");

        _x.Enable();

        Assert.Equal(2, _x.Recognizer.Calls);
        Assert.Equal(0, _x.Files.CallsOf(@"R:\elsewhere.png")); // not in a folder: never touched
        Assert.Equal(new ImageTextProgress(true, 2, 2, 0), _x.Service.Progress);
        Assert.Equal(2, _x.Rows());

        var engine = _x.Engine();
        var hits = engine.Search("speichern", SearchMode.ImageText).Hits;
        Assert.Equal([b, a], hits.Select(h => h.EntryId)); // newest first
        Assert.All(hits, h => Assert.NotNull(h.Snippet));
        Assert.Equal("Speichern", hits[0].Snippet!.Segments.First(s => s.Highlighted).Text);
        Assert.Equal([a], engine.Search("0x80070005 fehler", SearchMode.ImageText).Hits.Select(h => h.EntryId));
        Assert.Equal([b, a], engine.RecentlyOpened(SearchMode.ImageText).Hits.Select(h => h.EntryId)); // empty input: newest images
        Assert.Equal([b, a], engine.Search(".png", SearchMode.ImageText).Hits.Select(h => h.EntryId));
        Assert.Empty(engine.Search(".jpg", SearchMode.ImageText).Hits);

        // Image text hits never come up under All (the names do not contain the word).
        Assert.Empty(engine.Search("speichern", SearchMode.All).Hits);
        Assert.Empty(engine.Search("0x80070005", SearchMode.Files).Hits);
        Assert.DoesNotContain(other, engine.Search("speichern", SearchMode.ImageText).Hits.Select(h => h.EntryId));
    }

    [Fact]
    public void CloudFiles_AreNeverOpened()
    {
        const FileAttributes recall = (FileAttributes)0x0040_0000;
        const FileAttributes recallOnOpen = (FileAttributes)0x0004_0000;
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "cloud.png", "secret", attributes: FileAttributes.Archive | recall);
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "offline.png", "secret", attributes: FileAttributes.Archive | FileAttributes.Offline);
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "onopen.png", "secret", attributes: FileAttributes.Archive | recallOnOpen);
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "local.png", "visible");

        _x.Enable();

        Assert.Equal(0, _x.Files.CallsOf(P("cloud.png")));
        Assert.Equal(0, _x.Files.CallsOf(P("offline.png")));
        Assert.Equal(0, _x.Files.CallsOf(P("onopen.png")));
        Assert.Equal(["visible"], _x.Recognizer.Texts);

        // A file that went to the cloud after the scan (the directory still says local): looked at, never opened.
        int late = _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "late.png", "secret");
        _x.Files.Change(P("late.png"), attributes: FileAttributes.Archive | recall);
        _x.Service.ProcessPending();
        Assert.Equal(0, _x.Files.OpensOf(P("late.png")));
        Assert.Equal(1, _x.Recognizer.Calls);
        Assert.False(_x.Texts.Contains(late));
    }

    [Fact]
    public void ThumbnailsAndSizeLimits_AreSkipped()
    {
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "x.thumb.jpg", "thumb");
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "small.png", "small", size: 4_000);
        int huge = _x.F.Store.AllocateId();
        var batch = new ChangeBatch();
        batch.Inserts.Add(new EntryInsert(huge, _x.FolderId, "huge.png", false, 60L * 1024 * 1024, 0, FileAttributes.Archive));
        _x.F.Store.Apply(_x.Root.Id, batch);
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "notes.txt", "text file");
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "ok.jpeg", "fine");

        _x.Enable();

        Assert.Equal(["fine"], _x.Recognizer.Texts);
        Assert.Equal(0, _x.Files.CallsOf(P("x.thumb.jpg")));
        Assert.Equal(0, _x.Files.CallsOf(P("small.png")));
        Assert.Equal(0, _x.Files.CallsOf(P("huge.png")));
        Assert.Equal(0, _x.Files.CallsOf(P("notes.txt")));
        Assert.Equal(1, _x.Service.Progress.Total);

        // The directory said 6,000 bytes, the file is smaller by now: probed, not opened.
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "shrunk.png", "x");
        _x.Files.Change(P("shrunk.png"), content: new byte[100]);
        _x.Service.ProcessPending();
        Assert.Equal(0, _x.Files.OpensOf(P("shrunk.png")));
    }

    [Fact]
    public void NewImages_FromScanOrWatcher_AreRead_DeletedOnesLeave()
    {
        _x.Enable();
        Assert.Equal(0, _x.Recognizer.Calls);

        int sub = _x.F.Add(_x.Root, _x.FolderId, "2026", true);
        int id = _x.AddImage(sub, ImageTextFixture.Folder + @"\2026", "new.png", "Ticket 4711");
        _x.Service.ProcessPending();
        Assert.Equal(1, _x.Recognizer.Calls);
        Assert.Single(_x.Engine().Search("4711", SearchMode.ImageText).Hits);

        _x.F.Store.DeleteSubtree(sub);
        _x.Service.ProcessPending();
        Assert.False(_x.Texts.Contains(id));
        Assert.Equal(0, _x.Rows()); // the trigger removed the row
        Assert.Empty(_x.Engine().Search("4711", SearchMode.ImageText).Hits);
    }

    [Fact]
    public void ChangedImage_InvalidatesTheText()
    {
        int id = _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "a.png", "alt");
        _x.Enable();
        Assert.Equal(1, _x.Recognizer.Calls);

        // Overwritten with the same size: only the time differs. The scan updates the entry.
        long newTicks = FakeLocalFiles.BaseTicks + TimeSpan.TicksPerSecond * 5;
        _x.Files.Change(P("a.png"), content: FakeRecognizer.Image("neu"), ticks: newTicks);
        var batch = new ChangeBatch();
        batch.Updates.Add(new EntryUpdate(id, 6_000, ImageTextFixture.Seconds(newTicks), FileAttributes.Archive));
        _x.F.Store.Apply(_x.Root.Id, batch);
        _x.Service.ProcessPending();

        Assert.Equal(2, _x.Recognizer.Calls);
        Assert.Single(_x.Engine().Search("neu", SearchMode.ImageText).Hits);
        Assert.Empty(_x.Engine().Search("alt", SearchMode.ImageText).Hits);

        // Changed while the program was not running: the start compares with the directory and reads it again.
        long laterTicks = newTicks + TimeSpan.TicksPerMinute;
        _x.Files.Change(P("a.png"), content: FakeRecognizer.Image("drittens"), ticks: laterTicks);
        _x.Restart();
        batch = new ChangeBatch();
        batch.Updates.Add(new EntryUpdate(id, 6_000, ImageTextFixture.Seconds(laterTicks), FileAttributes.Archive));
        _x.F.Store.Apply(_x.Root.Id, batch); // the scan after the start
        _x.Recognizer.Texts.Clear();
        _x.Enable();
        Assert.Equal(["drittens"], _x.Recognizer.Texts);
    }

    [Fact]
    public void Restart_UsesTheStoredTexts_WithoutOpeningAnything()
    {
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "a.png", "Rechnung");
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "b.png", "Lieferschein");
        _x.Enable();
        Assert.Equal(2, _x.Recognizer.Calls);

        _x.Restart();
        _x.Files.ResetCounters();
        _x.Enable();

        Assert.Equal(2, _x.Recognizer.Calls);
        Assert.Equal(0, _x.Files.TotalCalls);
        Assert.Equal(new ImageTextProgress(true, 2, 2, 0), _x.Service.Progress);
        Assert.Single(_x.Engine().Search("lieferschein", SearchMode.ImageText).Hits);
    }

    [Fact]
    public void RemovingAFolder_DeletesItsText_AtOnce()
    {
        int other = _x.F.Add(_x.Root, _x.Root.RootEntryId, "Other", true);
        int keep = _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "a.png", "bleibt");
        int gone = _x.AddImage(other, @"R:\Other", "b.png", "geht");
        _x.Enable(ImageTextFixture.Folder, @"R:\Other");
        Assert.Equal(2, _x.Rows());

        _x.Service.Configure(true, [ImageTextFixture.Folder]); // no ProcessPending: deleted at once

        Assert.Equal(1, _x.Rows());
        Assert.True(_x.Texts.Contains(keep));
        Assert.False(_x.Texts.Contains(gone));
        Assert.Equal(1, _x.Scalar($"SELECT COUNT(*) FROM ocr WHERE entry_id = {keep}"));
        Assert.Empty(_x.Engine().Search("geht", SearchMode.ImageText).Hits);
        Assert.NotNull(_x.F.Store.GetEntry(gone)); // the entry itself stays in the directory
    }

    [Fact]
    public void MovingAnImageOutOfTheFolder_DropsItFromTheHits()
    {
        int other = _x.F.Add(_x.Root, _x.Root.RootEntryId, "Other", true);
        int id = _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "a.png", "Umzug");
        _x.Enable();
        Assert.Single(_x.Engine().Search("umzug", SearchMode.ImageText).Hits);

        _x.F.Store.Move(id, other);
        Assert.Empty(_x.Engine().Search("umzug", SearchMode.ImageText).Hits); // at once, by path
        _x.Service.ProcessPending();
        Assert.Equal(0, _x.Rows());
        Assert.False(_x.Texts.Contains(id));
    }

    [Fact]
    public void SwitchingOff_DeletesTheStoredText()
    {
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "a.png", "Rechnung");
        _x.Enable();
        Assert.Equal(1, _x.Rows());

        _x.Service.Configure(false, [ImageTextFixture.Folder]);

        Assert.Equal(0, _x.Rows());
        Assert.Equal(0, _x.Texts.Count);
        Assert.Empty(_x.Engine().Search("rechnung", SearchMode.ImageText).Hits);

        // Switching on again reads the images again.
        _x.Enable();
        Assert.Equal(2, _x.Recognizer.Calls);
        Assert.Equal(1, _x.Rows());
    }

    [Fact]
    public void NetworkFolders_AreNeverRead()
    {
        var share = _x.F.Store.AddRoot(@"N:\", @"\\server\share", RootKind.Network);
        int folder = _x.F.Add(share, share.RootEntryId, "Shots", true);
        int id = _x.F.Store.AllocateId();
        var batch = new ChangeBatch();
        batch.Inserts.Add(new EntryInsert(id, folder, "s.png", false, 6_000, ImageTextFixture.Seconds(FakeLocalFiles.BaseTicks), FileAttributes.Archive));
        _x.F.Store.Apply(share.Id, batch);
        _x.Files.Add(@"N:\Shots\s.png", FakeRecognizer.Image("share"));

        _x.Enable(@"N:\Shots", @"\\server\share\Shots");

        Assert.Empty(_x.Service.Folders);
        Assert.Equal(0, _x.Files.TotalCalls);
        Assert.Equal(0, _x.Recognizer.Calls);
    }

    [Fact]
    public void BrokenImage_IsStoredEmpty_AndNotReadAgain()
    {
        int id = _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "broken.png", "BROKEN");
        _x.Enable();
        Assert.True(_x.Texts.Contains(id));
        Assert.Equal(1, _x.Rows());

        _x.Restart();
        _x.Enable();
        Assert.Equal(1, _x.Recognizer.Calls);
    }

    [Fact]
    public void WithoutARecognitionLanguage_NothingIsOpened_AndTheImagesWait()
    {
        _x.Recognizer.IsAvailable = false;
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "a.png", "Rechnung");
        _x.Enable();
        Assert.Equal(0, _x.Files.OpenCount);
        Assert.Equal(1, _x.Service.Progress.Pending);
    }

    [Fact]
    public void RebuildingTheDirectory_EmptiesTheMemory()
    {
        _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "a.png", "Rechnung");
        _x.Enable();
        _x.F.Store.ResetAllEntries();
        _x.Service.ProcessPending();
        Assert.Equal(0, _x.Texts.Count);
        Assert.Equal(0, _x.Rows());
    }

    [Fact]
    public void ImageTextSearch_WithFiveThousandImages_IsFast()
    {
        // PLAN.md 6.5 stays: typing in mode Image text must not be noticeably slower than a name search.
        var index = new ImageTextIndex();
        var random = new Random(5);
        string[] words = ["rechnung", "fehler", "server", "kunde", "ticket", "speichern", "drucker", "freigabe", "update", "konto"];
        for (int i = 1; i <= 5_000; i++)
        {
            var text = new StringBuilder();
            for (int w = 0; w < 200; w++)
            {
                text.Append(words[random.Next(words.Length)]).Append(random.Next(1000)).Append(' ');
            }

            index.Set(i, text.ToString(), i);
        }

        var query = SearchQuery.Parse("fehler4 kunde", SearchMode.ImageText);
        index.Find(query);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10; i++)
        {
            index.Find(query);
        }

        Assert.True(timer.ElapsedMilliseconds / 10 < 50, $"{timer.ElapsedMilliseconds / 10} ms per search");
    }
}

/// <summary>Stage 11: the table <c>ocr</c> comes into existing databases without loss, like <c>hashes</c> in stage 10.</summary>
public sealed class ImageTextDatabaseTests : IDisposable
{
    /// <summary>A database as version 0.1.0 leaves it: schema 1 with the stage 10 extensions, without <c>ocr</c>.</summary>
    private const string Version010Schema = """
        CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE roots(
          id INTEGER PRIMARY KEY, path TEXT NOT NULL, unc_path TEXT, kind INTEGER NOT NULL, enabled INTEGER NOT NULL,
          last_scan_started_utc INTEGER, last_scan_completed_utc INTEGER, last_scan_status INTEGER NOT NULL,
          last_scan_failed_dirs INTEGER NOT NULL DEFAULT 0, attempts_today INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE entries(
          id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL, root_id INTEGER NOT NULL, name TEXT NOT NULL,
          is_dir INTEGER NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL, attrs INTEGER NOT NULL);
        CREATE INDEX ix_entries_parent ON entries(parent_id);
        CREATE TABLE usage(path TEXT PRIMARY KEY COLLATE NOCASE, open_count INTEGER NOT NULL, last_opened_utc INTEGER NOT NULL);
        CREATE TABLE hashes(entry_id INTEGER PRIMARY KEY, size INTEGER NOT NULL, mtime INTEGER NOT NULL, partial BLOB, full BLOB);
        CREATE INDEX ix_entries_size ON entries(size);
        CREATE TRIGGER tr_entries_delete_hashes AFTER DELETE ON entries BEGIN DELETE FROM hashes WHERE entry_id = old.id; END;
        INSERT INTO meta(key, value) VALUES('schema_version', '1'), ('max_id', '40');
        INSERT INTO roots(id, path, unc_path, kind, enabled, last_scan_started_utc, last_scan_completed_utc, last_scan_status)
          VALUES(1, 'R:\', NULL, 0, 1, 1700000000, 1700000100, 2);
        INSERT INTO entries VALUES(10, 0, 1, 'R:\', 1, 0, 0, 16);
        INSERT INTO entries VALUES(11, 10, 1, 'Shots', 1, 0, 1700000000, 16);
        INSERT INTO entries VALUES(12, 11, 1, 'Screenshot Müller.png', 0, 6000, 1700000000, 32);
        INSERT INTO entries VALUES(13, 10, 1, 'copy.pdf', 0, 2000000, 1700000001, 32);
        INSERT INTO hashes VALUES(13, 2000000, 5, x'0102', NULL);
        INSERT INTO usage VALUES('R:\copy.pdf', 3, 1700000500);
        """;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private long Scalar(IndexDatabase db, string sql)
    {
        using var c = db.OpenConnection();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private long Count(IndexDatabase db, string type, string name)
        => Scalar(db, $"SELECT COUNT(*) FROM sqlite_master WHERE type = '{type}' AND name = '{name}'");

    [Fact]
    public void ExistingDatabase_IsExtended_WithoutLoss()
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dir.DbPath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = Version010Schema;
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        var f = new StorageFixture(_dir);
        try
        {
            Assert.Null(f.Db.BackupPath);
            Assert.Empty(Directory.GetFiles(_dir.Path, "index.db.bak-*"));
            Assert.Equal(1, Scalar(f.Db, "SELECT CAST(value AS INTEGER) FROM meta WHERE key = 'schema_version'"));
            Assert.Equal(1, Count(f.Db, "table", "ocr"));
            Assert.Equal(1, Count(f.Db, "trigger", "tr_entries_delete_ocr"));
            Assert.Equal(1, Count(f.Db, "table", "notes")); // stage 13
            Assert.Equal(4, Scalar(f.Db, "SELECT COUNT(*) FROM entries"));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM hashes"));
            Assert.Equal(1, Scalar(f.Db, "SELECT COUNT(*) FROM usage"));
            Assert.Equal(4, f.Load());
            Assert.Equal([@"R:\Shots\Screenshot Müller.png"], f.Paths("mueller"));

            Assert.True(f.Store.SaveImageText(new StoredImageText(12, 6000, 5, "Text")));
            Assert.False(f.Store.SaveImageText(new StoredImageText(11, 0, 5, "a folder"))); // only files
            Assert.False(f.Store.SaveImageText(new StoredImageText(99, 0, 5, "no entry")));
            f.Store.DeleteSubtree(11);
            Assert.Equal(0, Scalar(f.Db, "SELECT COUNT(*) FROM ocr")); // trigger
        }
        finally
        {
            f.Dispose();
        }
    }

    [Fact]
    public void Renumbering_MovesTheTexts_AndKeepsTheTrigger()
    {
        var f = new StorageFixture(_dir);
        int id;
        try
        {
            var root = f.Store.AddRoot(@"R:\", null, RootKind.Local);
            for (int i = 0; i < 50; i++)
            {
                f.Store.AllocateId();
            }

            id = f.Add(root, root.RootEntryId, "kept.png", false, size: 6_000);
            int gone = f.Add(root, root.RootEntryId, "gone.png", false, size: 6_000);
            f.Store.SaveImageText(new StoredImageText(id, 6_000, 7, "kept"));
            f.Store.SaveImageText(new StoredImageText(gone, 6_000, 7, "gone"));
            f.Store.DeleteSubtree(gone);
        }
        finally
        {
            f.Dispose();
        }

        using (var db = IndexDatabase.Open(_dir.DbPath))
        {
            Assert.True(DirectoryRenumbering.RenumberIfNeeded(db, force: true).Performed);
            Assert.Equal(1, Count(db, "trigger", "tr_entries_delete_ocr"));
        }

        var after = new StorageFixture(_dir);
        try
        {
            int newId = after.Store.FindEntryId(@"R:\kept.png")!.Value;
            Assert.NotEqual(id, newId);
            Assert.Equal("kept", Assert.Single(after.Store.GetImageTexts([newId]).Values).Text);
            Assert.Equal(1, Scalar(after.Db, "SELECT COUNT(*) FROM ocr"));
            after.Store.DeleteSubtree(newId);
            Assert.Equal(0, Scalar(after.Db, "SELECT COUNT(*) FROM ocr"));
        }
        finally
        {
            after.Dispose();
        }
    }

    [Fact]
    public void TextsWithoutTheirTrigger_AreDropped()
    {
        var f = new StorageFixture(_dir);
        try
        {
            var root = f.Store.AddRoot(@"R:\", null, RootKind.Local);
            int id = f.Add(root, root.RootEntryId, "a.png", false, size: 6_000);
            f.Store.SaveImageText(new StoredImageText(id, 6_000, 7, "x"));
            using var c = f.Db.OpenConnection();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DROP TRIGGER tr_entries_delete_ocr"; // as after an older version renumbered
            cmd.ExecuteNonQuery();
        }
        finally
        {
            f.Dispose();
        }

        var again = new StorageFixture(_dir);
        try
        {
            Assert.Equal(0, Scalar(again.Db, "SELECT COUNT(*) FROM ocr"));
            Assert.Equal(1, Count(again.Db, "trigger", "tr_entries_delete_ocr"));
            Assert.Equal(2, Scalar(again.Db, "SELECT COUNT(*) FROM entries"));
        }
        finally
        {
            again.Dispose();
        }
    }
}

/// <summary>A fact that is skipped (not failed) when Windows has no text recognition language for the user profile.</summary>
public sealed class OcrFactAttribute : FactAttribute
{
    public OcrFactAttribute()
    {
        if (!WindowsImageTextRecognizer.AnyLanguageAvailable())
        {
            Skip = "Windows has no text recognition language for this user profile.";
        }
    }
}

/// <summary>The real Windows text recognition on images drawn here, in a temp folder scanned with the real scanner.</summary>
public sealed class RealImageTextTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static void DrawText(string path, string text, System.Drawing.Imaging.ImageFormat format, int width = 900, int height = 160)
    {
        using var bitmap = new System.Drawing.Bitmap(width, height);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new System.Drawing.Font("Arial", 32, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);

            // Some noise lines keep PNG files above 5 KB like real screenshots.
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(235, 235, 235));
            var random = new Random(text.Length);
            for (int i = 0; i < 400; i++)
            {
                g.DrawLine(pen, random.Next(width), random.Next(height), random.Next(width), random.Next(height));
            }

            g.DrawString(text, font, System.Drawing.Brushes.Black, 20, 50);
        }

        bitmap.Save(path, format);
    }

    [OcrFact]
    public void RealRecognizer_ReadsADrawnImage()
    {
        string png = Path.Combine(_dir.Path, "drawn.png");
        DrawText(png, "Rechnung 4711 FileDetective", System.Drawing.Imaging.ImageFormat.Png);
        var recognizer = new WindowsImageTextRecognizer();
        Assert.True(recognizer.IsAvailable);

        string text;
        using (var stream = File.OpenRead(png))
        {
            recognizer.Recognize(stream, CancellationToken.None); // first call loads the engine
        }

        var timer = System.Diagnostics.Stopwatch.StartNew();
        using (var stream = File.OpenRead(png))
        {
            text = recognizer.Recognize(stream, CancellationToken.None);
        }

        output.WriteLine($"Recognized in {timer.ElapsedMilliseconds} ms ({recognizer.LanguageTag}): {text}");
        Assert.Contains("4711", text, StringComparison.Ordinal);
        Assert.Contains("Rechnung", text, StringComparison.OrdinalIgnoreCase);

        // Not an image at all.
        using var garbage = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 6_000)));
        Assert.Throws<InvalidDataException>(() => recognizer.Recognize(garbage, CancellationToken.None));
    }

    [OcrFact]
    public void RealRecognizer_FullHdScreenshotWithManyLines()
    {
        // About what a full-screen screenshot holds: 1920 × 1080 with 30 lines of text.
        string png = Path.Combine(_dir.Path, "fullhd.png");
        using (var bitmap = new System.Drawing.Bitmap(1920, 1080))
        {
            using var g = System.Drawing.Graphics.FromImage(bitmap);
            g.Clear(System.Drawing.Color.White);
            using var font = new System.Drawing.Font("Segoe UI", 20, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            for (int line = 0; line < 30; line++)
            {
                g.DrawString($"Zeile {line + 1}: Kunde {1000 + line} meldet Fehler beim Speichern der Datei Bericht_{line}.xlsx", font, System.Drawing.Brushes.Black, 30, 20 + (line * 34));
            }

            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }

        var recognizer = new WindowsImageTextRecognizer();
        using (var warm = File.OpenRead(png))
        {
            recognizer.Recognize(warm, CancellationToken.None);
        }

        var times = new List<long>();
        string text = string.Empty;
        for (int i = 0; i < 3; i++)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            using var stream = File.OpenRead(png);
            text = recognizer.Recognize(stream, CancellationToken.None);
            times.Add(timer.ElapsedMilliseconds);
        }

        output.WriteLine($"1920x1080, 30 lines, {new FileInfo(png).Length / 1024} KB: {string.Join(", ", times)} ms");
        Assert.Contains("1029", text, StringComparison.Ordinal);
        Assert.Contains("Speichern", text, StringComparison.Ordinal);
    }

    [OcrFact]
    public void RealRecognizer_ScalesDownTooLargeImages()
    {
        string png = Path.Combine(_dir.Path, "wide.png");
        int width = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension + 1_000;
        DrawText(png, "Breitbild 2468", System.Drawing.Imaging.ImageFormat.Png, width, 300);
        using var stream = File.OpenRead(png);
        string text = new WindowsImageTextRecognizer().Recognize(stream, CancellationToken.None);
        output.WriteLine(text);
        Assert.Contains("2468", text, StringComparison.Ordinal);
    }

    [OcrFact]
    public void EndToEnd_RealFilesRealScanRealRecognition_FoundInModeImageText()
    {
        string shots = Path.Combine(_dir.Path, "Shots");
        Directory.CreateDirectory(shots);
        DrawText(Path.Combine(shots, "Screenshot 1.png"), "Fehlercode 0x80070005 Zugriff verweigert", System.Drawing.Imaging.ImageFormat.Png);
        DrawText(Path.Combine(shots, "Screenshot 2.jpg"), "Kundennummer 99812 Angebot", System.Drawing.Imaging.ImageFormat.Jpeg);
        DrawText(Path.Combine(shots, "Screenshot 3.bmp"), "Druckauftrag 5521 fertig", System.Drawing.Imaging.ImageFormat.Bmp);
        File.WriteAllBytes(Path.Combine(shots, "empty.png"), new byte[100]);

        var dbDir = new TempDir();
        var f = new StorageFixture(dbDir);
        try
        {
            var root = f.Store.AddRoot(_dir.Path, null, RootKind.Local);
            new Scanner(f.Store, FileSystemDirectoryLister.Instance, ExcludeRules.None).ScanRoot(root.Id);
            var texts = new ImageTextIndex();
            using var service = new ImageTextService(
                f.Store, f.Index, texts, new WindowsImageTextRecognizer(), SystemLocalFileAccess.Instance,
                new DatabaseEntryResolver(f.Db), new ImageTextServiceOptions { RunWorker = false });
            var timer = System.Diagnostics.Stopwatch.StartNew();
            service.Configure(true, [shots]);
            service.ProcessPending();
            output.WriteLine($"3 images recognized in {timer.ElapsedMilliseconds} ms (including the first start of the engine).");
            Assert.Equal(new ImageTextProgress(true, 3, 3, 0), service.Progress);
            foreach (var row in f.Store.ReadImageTexts())
            {
                output.WriteLine($"{row.EntryId}: {row.Text}");
            }

            var engine = new SearchEngine(f.Index, new DatabaseEntryResolver(f.Db), f.Usage,
                new ImageTextSearch(texts, new DatabaseEntryResolver(f.Db), f.Store.GetImageTexts, () => service.Folders));
            // Windows may read the "0" of "0x" as the letter O, so the test searches the part after it.
            var hit = Assert.Single(engine.Search("80070005 zugriff", SearchMode.ImageText).Hits);
            Assert.Equal("Screenshot 1.png", hit.Details.Name);
            Assert.Contains(hit.Snippet!.Segments, s => s.Highlighted && s.Text.Contains("80070005", StringComparison.Ordinal));
            Assert.Equal("Screenshot 2.jpg", Assert.Single(engine.Search("99812", SearchMode.ImageText).Hits).Details.Name);
            Assert.Equal("Screenshot 3.bmp", Assert.Single(engine.Search("5521", SearchMode.ImageText).Hits).Details.Name);
            Assert.Empty(engine.Search("80070005", SearchMode.All).Hits);
        }
        finally
        {
            f.Dispose();
            dbDir.Dispose();
        }
    }
}
