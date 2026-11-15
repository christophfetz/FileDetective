using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>Stage 12 (PLAN.md 11.3): grouping by days, rows of the grid, moving the selection, positions of the rows.</summary>
public class HistoryLayoutTests
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

    private static HistoryImage Img(int id, DateTime utc, string folder = @"R:\Shots", FileAttributes attributes = FileAttributes.Archive)
        => new(id, $"img{id}.png", $@"{folder}\img{id}.png", folder, DateTime.SpecifyKind(utc, DateTimeKind.Utc), 6_000, attributes, true);

    private static DateTime U(int year, int month, int day, int hour, int minute) => new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static string[] Layout(IReadOnlyList<HistoryDay> days)
        => [.. days.Select(d => $"{d.Day:yyyy-MM-dd}:{string.Join(',', d.Images.Select(i => i.EntryId))}")];

    [Fact]
    public void NewestFirst_ByTime_ThenByHigherEntryNumber()
    {
        var sorted = ImageHistory.NewestFirst([Img(1, U(2026, 5, 1, 8, 0)), Img(2, U(2026, 5, 3, 8, 0)), Img(3, U(2026, 5, 3, 8, 0)), Img(4, U(2026, 5, 2, 8, 0))]);

        Assert.Equal([3, 2, 4, 1], sorted.Select(i => i.EntryId));
    }

    [Fact]
    public void GroupByDay_UsesTheGivenZone_NewestDayFirst_NewestImageFirst()
    {
        var zone = ManualTimeProvider.UtcPlusOne; // +01:00, no daylight saving
        var days = HistoryLayout.GroupByDay(
            [
                Img(1, U(2026, 5, 1, 22, 30)), // 23:30 on May 1st
                Img(2, U(2026, 5, 1, 23, 0)), // 00:00 on May 2nd
                Img(3, U(2026, 5, 2, 9, 0)),
                Img(4, U(2026, 4, 30, 12, 0)),
            ],
            zone);

        Assert.Equal(["2026-05-02:3,2", "2026-05-01:1", "2026-04-30:4"], Layout(days));
    }

    [Fact]
    public void GroupByDay_AtTheEndOfSummerTime_TheLongDayStaysOneDay()
    {
        // Europe/Berlin: on 2026-10-25 at 03:00 CEST the clocks go back to 02:00 CET, a day of 25 hours.
        var days = HistoryLayout.GroupByDay(
            [
                Img(1, U(2026, 10, 24, 21, 59)), // 23:59 CEST on the 24th
                Img(2, U(2026, 10, 24, 22, 0)), // 00:00 CEST on the 25th
                Img(3, U(2026, 10, 25, 0, 30)), // 02:30 CEST
                Img(4, U(2026, 10, 25, 1, 30)), // 02:30 CET (the second 02:30)
                Img(5, U(2026, 10, 25, 22, 59)), // 23:59 CET
                Img(6, U(2026, 10, 25, 23, 0)), // 00:00 CET on the 26th
            ],
            Berlin);

        Assert.Equal(["2026-10-26:6", "2026-10-25:5,4,3,2", "2026-10-24:1"], Layout(days));
    }

    [Fact]
    public void GroupByDay_AtTheStartOfSummerTime_TheShortDayStaysOneDay()
    {
        // On 2026-03-29 at 02:00 CET the clocks jump to 03:00 CEST, a day of 23 hours.
        var days = HistoryLayout.GroupByDay(
            [
                Img(1, U(2026, 3, 28, 22, 59)), // 23:59 CET on the 28th
                Img(2, U(2026, 3, 28, 23, 0)), // 00:00 CET on the 29th
                Img(3, U(2026, 3, 29, 0, 59)), // 01:59 CET
                Img(4, U(2026, 3, 29, 1, 0)), // 03:00 CEST
                Img(5, U(2026, 3, 29, 21, 59)), // 23:59 CEST
                Img(6, U(2026, 3, 29, 22, 0)), // 00:00 CEST on the 30th
            ],
            Berlin);

        Assert.Equal(["2026-03-30:6", "2026-03-29:5,4,3,2", "2026-03-28:1"], Layout(days));
    }

    [Fact]
    public void GroupByDay_OfNothing_IsEmpty() => Assert.Empty(HistoryLayout.GroupByDay([], Berlin));

    [Fact]
    public void DayKind_TodayYesterdayOther_InTheZoneOfTheClock()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 25, 22, 30, 0, TimeSpan.Zero)); // 23:30 CET on the 25th
        var today = HistoryLayout.Today(time, Berlin);

        Assert.Equal(new DateOnly(2026, 10, 25), today);
        Assert.Equal(HistoryDayKind.Today, HistoryLayout.KindOf(new DateOnly(2026, 10, 25), today));
        Assert.Equal(HistoryDayKind.Yesterday, HistoryLayout.KindOf(new DateOnly(2026, 10, 24), today));
        Assert.Equal(HistoryDayKind.Other, HistoryLayout.KindOf(new DateOnly(2026, 10, 23), today));
        time.Advance(TimeSpan.FromHours(1)); // 00:30 on the 26th
        Assert.Equal(new DateOnly(2026, 10, 26), HistoryLayout.Today(time, Berlin));
    }

    [Theory]
    [InlineData(1000, 148, 6)]
    [InlineData(148, 148, 1)]
    [InlineData(100, 148, 1)]
    [InlineData(0, 148, 1)]
    [InlineData(double.NaN, 148, 1)]
    public void ColumnsFor_FitsWholeTiles_AtLeastOne(double width, double tile, int expected)
        => Assert.Equal(expected, HistoryLayout.ColumnsFor(width, tile));

    private static (IReadOnlyList<HistoryDay> Days, IReadOnlyList<HistoryRow> Rows) TwoDays(int columns)
    {
        var days = new List<HistoryDay>
        {
            new(new DateOnly(2026, 5, 2), [Img(7, U(2026, 5, 2, 9, 0)), Img(6, U(2026, 5, 2, 8, 0)), Img(5, U(2026, 5, 2, 7, 0)), Img(4, U(2026, 5, 2, 6, 0))]),
            new(new DateOnly(2026, 5, 1), [Img(3, U(2026, 5, 1, 9, 0)), Img(2, U(2026, 5, 1, 8, 0)), Img(1, U(2026, 5, 1, 7, 0))]),
        };
        return (days, HistoryLayout.BuildRows(days, columns));
    }

    [Fact]
    public void BuildRows_HeaderPerDay_ThenRowsOfTheColumns()
    {
        var (_, rows) = TwoDays(3);

        Assert.Equal(5, rows.Count);
        Assert.Equal(new HistoryHeaderRow(new DateOnly(2026, 5, 2), 4), rows[0]);
        Assert.Equal([7, 6, 5], ((HistoryTileRow)rows[1]).Images.Select(i => i.EntryId));
        Assert.Equal([4], ((HistoryTileRow)rows[2]).Images.Select(i => i.EntryId));
        Assert.IsType<HistoryHeaderRow>(rows[3]);
        Assert.Equal([3, 2, 1], ((HistoryTileRow)rows[4]).Images.Select(i => i.EntryId));
    }

    [Theory]
    [InlineData(5, HistoryMove.Right, 4)]
    [InlineData(4, HistoryMove.Left, 5)]
    [InlineData(7, HistoryMove.Left, 7)]
    [InlineData(1, HistoryMove.Right, 1)]
    [InlineData(6, HistoryMove.Down, 4)] // column 1 of a one-image row: the last column there
    [InlineData(4, HistoryMove.Down, 3)]
    [InlineData(2, HistoryMove.Up, 4)]
    [InlineData(7, HistoryMove.Up, 7)]
    [InlineData(5, HistoryMove.Home, 7)]
    [InlineData(5, HistoryMove.End, 1)]
    [InlineData(7, HistoryMove.PageDown, 3)]
    [InlineData(1, HistoryMove.PageUp, 5)] // two rows up, still column 2
    public void Move_KeepsTheColumn_AndCrossesTheDays(int from, HistoryMove move, int expected)
    {
        var (_, rows) = TwoDays(3);

        Assert.Equal(expected, HistoryLayout.Move(rows, from, move, pageRows: 2));
    }

    [Fact]
    public void Move_WithoutSelection_StartsAtTheFirst_EndAtTheLast_AndOfNothingIsNull()
    {
        var (_, rows) = TwoDays(3);

        Assert.Equal(7, HistoryLayout.Move(rows, null, HistoryMove.Down));
        Assert.Equal(1, HistoryLayout.Move(rows, null, HistoryMove.End));
        Assert.Equal(7, HistoryLayout.Move(rows, 999, HistoryMove.Right)); // an image that is not in the list
        Assert.Null(HistoryLayout.Move([], null, HistoryMove.Down));
    }

    [Fact]
    public void RowMap_PositionsRows_ByTheirFixedHeights()
    {
        var (_, rows) = TwoDays(3); // header, tiles, tiles, header, tiles
        var map = new HistoryRowMap(rows, 40, 132);

        Assert.Equal([0, 40, 172, 304, 344], Enumerable.Range(0, 5).Select(map.Top));
        Assert.Equal(476, map.TotalHeight);
        Assert.Equal(0, map.IndexAt(0));
        Assert.Equal(1, map.IndexAt(40));
        Assert.Equal(1, map.IndexAt(171));
        Assert.Equal(2, map.IndexAt(172));
        Assert.Equal(4, map.IndexAt(10_000)); // clamped
        Assert.Equal((0, 1), map.Visible(0, 100));
        Assert.Equal((1, 2), map.Visible(40, 132 + 1));
        Assert.Equal((1, 1), map.Visible(40, 132));
        Assert.Equal(1, map.RowOfImage(5));
        Assert.Equal(4, map.RowOfImage(2));
        Assert.Equal(-1, map.RowOfImage(99));
        Assert.Equal(3, map.RowOfDay(new DateOnly(2026, 5, 1)));
    }

    [Fact]
    public void RowMap_OfNoRows_IsHarmless()
    {
        var map = new HistoryRowMap([], 40, 132);

        Assert.Equal(0, map.TotalHeight);
        Assert.Equal(-1, map.IndexAt(10));
        Assert.Equal((0, -1), map.Visible(0, 500));
        Assert.Null(map.AnchorAt(30));
        Assert.Equal(0, map.OffsetOf(new HistoryAnchor(1, null, 0)));
    }

    [Fact]
    public void RowMap_OffsetToReveal_MovesAsLittleAsPossible()
    {
        var (_, rows) = TwoDays(3);
        var map = new HistoryRowMap(rows, 40, 132);

        Assert.Equal(0, map.OffsetToReveal(1, 0, 400)); // in view already: no movement
        Assert.Equal(76, map.OffsetToReveal(4, 0, 400)); // below: its bottom edge at the bottom of the view
        Assert.Equal(40, map.OffsetToReveal(1, 300, 400)); // above: its top edge at the top of the view
    }

    [Fact]
    public void RowMap_Anchor_KeepsThePlace_WhenTheColumnsChange_OrNewImagesArrive()
    {
        var (_, rows3) = TwoDays(3);
        var old = new HistoryRowMap(rows3, 40, 132);

        // Scrolled 60 px into the second row (the one with 7, 6, 5 sits at 40).
        var anchor = old.AnchorAt(100);
        Assert.Equal(new HistoryAnchor(7, null, 60), anchor);

        // Two columns: 7 and 6 first, so the row of 7 still starts at 40.
        var (_, rows2) = TwoDays(2);
        Assert.Equal(100, new HistoryRowMap(rows2, 40, 132).OffsetOf(anchor));

        // A new image of a day before: everything moves down by a header plus a row.
        var days = new List<HistoryDay>
        {
            new(new DateOnly(2026, 5, 3), [Img(8, U(2026, 5, 3, 9, 0))]),
            new(new DateOnly(2026, 5, 2), [Img(7, U(2026, 5, 2, 9, 0)), Img(6, U(2026, 5, 2, 8, 0)), Img(5, U(2026, 5, 2, 7, 0)), Img(4, U(2026, 5, 2, 6, 0))]),
            new(new DateOnly(2026, 5, 1), [Img(3, U(2026, 5, 1, 9, 0)), Img(2, U(2026, 5, 1, 8, 0)), Img(1, U(2026, 5, 1, 7, 0))]),
        };
        var moved = new HistoryRowMap(HistoryLayout.BuildRows(days, 3), 40, 132);
        Assert.Equal(100 + 40 + 132, moved.OffsetOf(anchor));

        // At the very top there is no anchor: the view stays at the top and shows the new image.
        Assert.Null(old.AnchorAt(0));
        Assert.Equal(0, moved.OffsetOf(old.AnchorAt(0)));
    }

    [Fact]
    public void RowMap_Anchor_OnAHeader_FallsBackToTheDay_AndToTheTopWhenTheImageIsGone()
    {
        var (_, rows) = TwoDays(3);
        var map = new HistoryRowMap(rows, 40, 132);

        var onHeader = map.AnchorAt(310); // header of May 1st starts at 304
        Assert.Equal(new HistoryAnchor(null, new DateOnly(2026, 5, 1), 6), onHeader);
        Assert.Equal(310, map.OffsetOf(onHeader));

        Assert.Equal(0, map.OffsetOf(new HistoryAnchor(99, null, 10))); // image and day unknown
        Assert.Equal(304 + 5, map.OffsetOf(new HistoryAnchor(99, new DateOnly(2026, 5, 1), 5)));
    }
}

/// <summary>Stage 12: the history reads the directory only; cloud files get no preview; search and folder filter.</summary>
public sealed class ImageHistoryTests : IDisposable
{
    private const string Shots = ImageTextFixture.Folder;
    private static readonly long T0 = FakeLocalFiles.BaseTicks;
    private const FileAttributes CloudAttribute = (FileAttributes)0x400000; // RecallOnDataAccess

    private readonly ImageTextFixture _x = new();

    public void Dispose() => _x.Dispose();

    private ImageHistory History(params string[] folders)
        => new(new StoreImageHistorySource(_x.F.Store, new DatabaseEntryResolver(_x.F.Db)), _x.Texts, () => folders.Length == 0 ? [Shots] : folders);

    private int Add(string name, string text, long hours, FileAttributes attributes = FileAttributes.Archive, int size = 6_000, int? parent = null, string? parentPath = null)
        => _x.AddImage(parent ?? _x.FolderId, parentPath ?? Shots, name, text, T0 + hours * TimeSpan.TicksPerHour, attributes, size);

    [Fact]
    public void Load_ListsTheImagesNewestFirst_WithPathsFromTheDirectory_AndOpensNoFile()
    {
        int a = Add("a.png", "Müller Rechnung", 1);
        int b = Add("b.jpg", "Fehler beim Speichern", 3);
        int c = Add("c.png", "Angebot", 2);
        _x.Enable();
        _x.Files.ResetCounters();
        _x.Recognizer.Texts.Clear();
        int recognizedBefore = _x.Recognizer.Calls;

        var snapshot = History().Load();

        Assert.Equal([b, c, a], snapshot.Images.Select(i => i.EntryId));
        Assert.Equal([$@"{Shots}\b.jpg", $@"{Shots}\c.png", $@"{Shots}\a.png"], snapshot.Images.Select(i => i.FullPath));
        Assert.All(snapshot.Images, i => Assert.Equal(Shots, i.Folder));
        Assert.All(snapshot.Images, i => Assert.True(i.HasText));
        Assert.Equal(DateTime.SpecifyKind(new DateTime(T0 + 3 * TimeSpan.TicksPerHour), DateTimeKind.Utc), snapshot.Images[0].LastWriteUtc);
        Assert.Equal(0, _x.Files.TotalCalls); // from the directory: no file is probed, opened or listed
        Assert.Equal(recognizedBefore, _x.Recognizer.Calls);
    }

    [Fact]
    public void Load_LeavesOutWhatTheTextRecognitionWouldNotRead_ButKeepsCloudImages()
    {
        Add("shot.png", "text", 1);
        Add("shot.thumb.jpg", "text", 2);
        Add("tiny.png", "text", 3, size: 100);
        Add("huge.png", "text", 4, size: 51 * 1024 * 1024);
        Add("notes.txt", "text", 5);
        int cloud = Add("cloud.png", "text", 6, CloudAttribute);
        _x.Enable();

        var images = History().Load().Images;

        Assert.Equal(["cloud.png", "shot.png"], images.Select(i => i.Name));
        var cloudImage = images.Single(i => i.EntryId == cloud);
        Assert.True(cloudImage.IsCloud);
        Assert.False(cloudImage.HasText); // never read
    }

    [Fact]
    public void CloudImages_GetNoPreview_AndNoCopyAsImage_LocalOnesDo()
    {
        Add("cloud.png", "text", 1, CloudAttribute);
        Add("offline.png", "text", 2, FileAttributes.Offline);
        Add("onopen.png", "text", 3, (FileAttributes)0x40000); // RecallOnOpen
        Add("local.png", "text", 4);
        _x.Enable();

        var images = History().Load().Images.ToDictionary(i => i.Name);

        Assert.All(new[] { "cloud.png", "offline.png", "onopen.png" }, name =>
        {
            Assert.True(images[name].IsCloud);
            Assert.False(images[name].CanThumbnail);
            Assert.False(images[name].CanCopyImage);
        });
        Assert.False(images["local.png"].IsCloud);
        Assert.True(images["local.png"].CanThumbnail);
        Assert.True(images["local.png"].CanCopyImage);
    }

    [Fact]
    public void Load_TakesSubfolders_AndAnImageBelongsToTheMostSpecificFolder()
    {
        int sub = _x.F.Add(_x.Root, _x.FolderId, "Sub", true);
        int top = Add("top.png", "text", 1);
        int deep = Add("deep.png", "text", 2, parent: sub, parentPath: Shots + @"\Sub");
        _x.Enable(Shots, Shots + @"\Sub");

        var images = History(Shots, Shots + @"\Sub").Load().Images;

        Assert.Equal([deep, top], images.Select(i => i.EntryId)); // each once
        Assert.Equal(Shots + @"\Sub", images[0].Folder);
        Assert.Equal(Shots, images[1].Folder);
        Assert.Equal(Shots + @"\Sub\deep.png", images[0].FullPath);
    }

    [Fact]
    public void Load_IgnoresFoldersThatAreNotInTheDirectory_AndNonLocalLocations()
    {
        Add("a.png", "text", 1);
        var share = _x.F.Store.AddRoot(@"N:\", @"\\server\share", RootKind.Network);
        int shareFolder = _x.F.Add(share, share.RootEntryId, "Shots", true);
        var batch = new ChangeBatch();
        batch.Inserts.Add(new EntryInsert(_x.F.Store.AllocateId(), shareFolder, "remote.png", false, 6_000, 1, FileAttributes.Archive));
        _x.F.Store.Apply(share.Id, batch);

        var history = History(@"R:\Nowhere", @"N:\Shots", Shots, @"\\server\share\Shots");

        Assert.Equal(["a.png"], history.Load().Images.Select(i => i.Name));
    }

    [Fact]
    public void Filter_FindsByText_AllTermsInAnyOrder_UmlautsEqualized_NewestFirst()
    {
        int a = Add("a.png", "Rechnung Müller Hamburg", 1);
        int b = Add("b.png", "Mueller Angebot", 3);
        int c = Add("c.png", "Fehlermeldung", 2);
        _x.Enable();
        var history = History();
        var snapshot = history.Load();

        Assert.Equal([b, a], history.Filter(snapshot, "müller", null).Images.Select(i => i.EntryId));
        Assert.Equal([b, a], history.Filter(snapshot, "MUELLER", null).Images.Select(i => i.EntryId));
        Assert.Equal([a], history.Filter(snapshot, "hamburg müller rechnung", null).Images.Select(i => i.EntryId));
        Assert.Equal([c], history.Filter(snapshot, "fehler", null).Images.Select(i => i.EntryId)); // part of a word
        Assert.Empty(history.Filter(snapshot, "müller fehler", null).Images);
        Assert.Equal([b, c, a], history.Filter(snapshot, "", null).Images.Select(i => i.EntryId));
        Assert.Equal([b, c, a], history.Filter(snapshot, null, null).Images.Select(i => i.EntryId));
        Assert.Equal(3, history.Filter(snapshot, "müller", null).Total); // all images of the folders
        Assert.Single(history.Filter(snapshot, "müller", null).Query.Terms); // for highlighting the text
    }

    [Fact]
    public void Filter_TypeFilterAppliesToTheName_AndCombinesWithTerms()
    {
        int png = Add("a.png", "Rechnung", 1);
        int jpg = Add("b.jpg", "Rechnung", 2);
        _x.Enable();
        var history = History();
        var snapshot = history.Load();

        Assert.Equal([png], history.Filter(snapshot, ".png", null).Images.Select(i => i.EntryId));
        Assert.Equal([png], history.Filter(snapshot, "rechnung .png", null).Images.Select(i => i.EntryId));
        Assert.Equal([jpg, png], history.Filter(snapshot, ".png .jpg", null).Images.Select(i => i.EntryId));
    }

    [Fact]
    public void Filter_ByFolder_KeepsOnlyThatFolder_CaseInsensitive()
    {
        int sub = _x.F.Add(_x.Root, _x.Root.RootEntryId, "Other", true);
        int inShots = Add("a.png", "Rechnung", 1);
        int inOther = Add("b.png", "Rechnung", 2, parent: sub, parentPath: @"R:\Other");
        _x.Enable(Shots, @"R:\Other");
        var history = History(Shots, @"R:\Other");
        var snapshot = history.Load();

        Assert.Equal([inOther, inShots], history.Filter(snapshot, null, null).Images.Select(i => i.EntryId));
        Assert.Equal([inShots], history.Filter(snapshot, null, Shots).Images.Select(i => i.EntryId));
        Assert.Equal([inOther], history.Filter(snapshot, null, @"r:\OTHER\").Images.Select(i => i.EntryId));
        Assert.Equal([inOther], history.Filter(snapshot, "rechnung", @"R:\Other").Images.Select(i => i.EntryId));
        Assert.Empty(history.Filter(snapshot, "rechnung", @"R:\Nope").Images);
    }

    [Fact]
    public void WithTheImageTextSwitchedOff_TheImagesAreStillListed_ButNothingIsFoundByText_AndNoImageIsOpened()
    {
        int a = Add("a.png", "Rechnung", 1);
        int b = Add("b.png", "Fehler", 2);
        _x.Enable();
        _x.Service.Configure(false, [Shots]); // deletes every stored text
        int recognized = _x.Recognizer.Calls;
        _x.Files.ResetCounters();
        var history = History();

        var snapshot = history.Load();

        Assert.Equal([b, a], snapshot.Images.Select(i => i.EntryId));
        Assert.All(snapshot.Images, i => Assert.False(i.HasText));
        Assert.Empty(history.Filter(snapshot, "rechnung", null).Images);
        Assert.Equal([b, a], history.Filter(snapshot, null, null).Images.Select(i => i.EntryId));
        Assert.Equal(0, _x.Files.TotalCalls);
        Assert.Equal(recognized, _x.Recognizer.Calls);
    }

    [Fact]
    public void ANewImage_AppearsInTheNextLoad_AndAMovedOrDeletedOneDisappears()
    {
        int a = Add("a.png", "Rechnung", 1);
        _x.Enable();
        var history = History();
        Assert.Equal([a], history.Load().Images.Select(i => i.EntryId));

        int b = Add("b.png", "Fehler", 2);
        _x.Service.ProcessPending();
        Assert.Equal([b, a], history.Load().Images.Select(i => i.EntryId));
        Assert.True(history.Load().Images[0].HasText);

        _x.F.Store.DeleteSubtree(a);
        Assert.Equal([b], history.Load().Images.Select(i => i.EntryId));
    }

    [Fact]
    public void IsRelevant_OnlyForChangesThatCanAlterTheList()
    {
        static IndexChange Change(IReadOnlyList<WrittenFile>? written = null, IReadOnlyList<int>? deleted = null, bool relocated = false, bool reset = false)
            => new(written ?? [], deleted ?? [], relocated, reset);

        Assert.True(ImageHistory.IsRelevant(Change([new WrittenFile(1, "new.PNG")])));
        Assert.True(ImageHistory.IsRelevant(Change([new WrittenFile(1, null)]))); // an update has no name: possibly an image
        Assert.True(ImageHistory.IsRelevant(Change(deleted: [5])));
        Assert.True(ImageHistory.IsRelevant(Change(relocated: true)));
        Assert.True(ImageHistory.IsRelevant(Change(reset: true)));
        Assert.False(ImageHistory.IsRelevant(Change([new WrittenFile(1, "notes.txt"), new WrittenFile(2, "a.thumb.jpg")])));
        Assert.False(ImageHistory.IsRelevant(Change()));
    }
}

/// <summary>Stage 12: the refresh of the window is limited to once per 2 seconds.</summary>
public class RefreshThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFirstTrigger_RunsAtOnce_FurtherOnesDuringTheIntervalBecomeOneLaterRun()
    {
        var time = new ManualTimeProvider(Start);
        var runs = new List<DateTimeOffset>();
        using var throttle = new RefreshThrottle(TimeSpan.FromSeconds(2), () => runs.Add(time.GetUtcNow()), time);

        throttle.Trigger();
        Assert.Single(runs);

        for (int i = 0; i < 20; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            throttle.Trigger();
        }

        Assert.Single(runs); // 1 s passed
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Single(runs);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, runs.Count);
        Assert.Equal(Start + TimeSpan.FromSeconds(2), runs[1]);

        time.Advance(TimeSpan.FromSeconds(10)); // nothing is pending: no more runs
        Assert.Equal(2, runs.Count);
    }

    [Fact]
    public void AfterAQuietTime_TheNextTriggerRunsAtOnceAgain()
    {
        var time = new ManualTimeProvider(Start);
        int runs = 0;
        using var throttle = new RefreshThrottle(TimeSpan.FromSeconds(2), () => runs++, time);

        throttle.Trigger();
        time.Advance(TimeSpan.FromSeconds(5));
        throttle.Trigger();

        Assert.Equal(2, runs);
    }

    [Fact]
    public void AStreamOfTriggers_NeverRunsMoreOftenThanTheInterval_AndTheLastChangeIsNotLost()
    {
        var time = new ManualTimeProvider(Start);
        var runs = new List<DateTimeOffset>();
        using var throttle = new RefreshThrottle(TimeSpan.FromSeconds(2), () => runs.Add(time.GetUtcNow()), time);

        for (int i = 0; i < 300; i++) // one trigger every 100 ms for 30 s
        {
            throttle.Trigger();
            time.Advance(TimeSpan.FromMilliseconds(100));
        }

        for (int i = 1; i < runs.Count; i++)
        {
            Assert.True(runs[i] - runs[i - 1] >= TimeSpan.FromSeconds(2), $"runs {i - 1} and {i} are too close");
        }

        Assert.InRange(runs.Count, 14, 16);
        throttle.Trigger(); // a last change in the middle of an interval
        int before = runs.Count;
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(before + 1, runs.Count);
    }

    [Fact]
    public void AFailingAction_IsReported_AndTheThrottleKeepsWorking()
    {
        var time = new ManualTimeProvider(Start);
        int runs = 0;
        var errors = new List<Exception>();
        using var throttle = new RefreshThrottle(TimeSpan.FromSeconds(2), () =>
        {
            runs++;
            throw new InvalidOperationException("boom");
        }, time);
        throttle.Failed += errors.Add;

        throttle.Trigger();
        time.Advance(TimeSpan.FromSeconds(3));
        throttle.Trigger();

        Assert.Equal(2, runs);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public void AfterDispose_NothingRunsAnymore()
    {
        var time = new ManualTimeProvider(Start);
        int runs = 0;
        var throttle = new RefreshThrottle(TimeSpan.FromSeconds(2), () => runs++, time);
        throttle.Trigger();
        throttle.Trigger(); // waits for the end of the interval
        throttle.Dispose();

        time.Advance(TimeSpan.FromSeconds(10));
        throttle.Trigger();

        Assert.Equal(1, runs);
    }
}

/// <summary>Stage 12: the shortcut of the history (none by default) and its conflict with the one of the search bar.</summary>
public sealed class HistoryHotkeyTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static HotkeyGesture Gesture(string text)
        => HotkeyGesture.TryParse(text, out var g) ? g : throw new InvalidOperationException(text);

    [Fact]
    public void ByDefault_NoShortcutIsSet()
    {
        var settings = new AppSettings();

        Assert.Equal(string.Empty, settings.HistoryHotkey);
        Assert.Null(settings.HistoryHotkeyValue);
    }

    [Theory]
    [InlineData("Alt+Space", "Alt+Space", true)]
    [InlineData("alt+space", "Alt+Space", true)] // the same combination, written differently
    [InlineData("Ctrl+Alt+H", "Alt+Space", false)]
    [InlineData("Alt+H", "Alt+Shift+H", false)]
    public void Conflict_IsTheSameCombination(string candidate, string other, bool conflict)
        => Assert.Equal(conflict, HotkeyRules.Conflicts(Gesture(candidate), Gesture(other)));

    [Fact]
    public void Conflict_WithNoOtherShortcut_IsNone() => Assert.False(HotkeyRules.Conflicts(Gesture("Alt+H"), null));

    [Fact]
    public void TheShortcutOfTheSearchBar_IsRefusedForTheHistory_ByTheSettingsThemselves()
    {
        var clash = new AppSettings { Hotkey = "Ctrl+Alt+K", HistoryHotkey = "Ctrl+Alt+K" };

        Assert.Null(clash.HistoryHotkeyValue);
        Assert.Equal(string.Empty, clash.Sanitized().HistoryHotkey);
        Assert.Equal("Ctrl+Alt+H", new AppSettings { Hotkey = "Ctrl+Alt+K", HistoryHotkey = "ctrl+alt+h" }.Sanitized().HistoryHotkey);
        Assert.Equal(string.Empty, new AppSettings { HistoryHotkey = "H" }.Sanitized().HistoryHotkey); // no Alt, Ctrl or Win
        Assert.Equal(string.Empty, new AppSettings { HistoryHotkey = "nonsense" }.Sanitized().HistoryHotkey);
        Assert.Equal(string.Empty, new AppSettings { HistoryHotkey = "Alt+Space" }.Sanitized().HistoryHotkey); // the default of the search bar
    }

    [Fact]
    public void TheShortcut_IsStoredInSettingsJson_AndSurvivesALoad_AndAConflictingFileEntryIsDropped()
    {
        string path = Path.Combine(_dir.Path, "settings.json");
        var service = new SettingsService(path);
        service.Load();
        service.Update(s => s with { HistoryHotkey = "Ctrl+Shift+Alt+H" });

        var again = new SettingsService(path);
        again.Load();
        Assert.Equal("Ctrl+Alt+Shift+H", again.Current.HistoryHotkey);
        Assert.Equal(Gesture("Ctrl+Alt+Shift+H"), again.Current.HistoryHotkeyValue);
        Assert.Contains("\"historyHotkey\"", File.ReadAllText(path));

        File.WriteAllText(path, """{ "hotkey": "Ctrl+Alt+K", "historyHotkey": "Ctrl+Alt+K" }""");
        var conflicting = new SettingsService(path);
        conflicting.Load();
        Assert.Equal(string.Empty, conflicting.Current.HistoryHotkey);

        File.WriteAllText(path, """{ "hotkey": "Alt+Space" }""");
        var older = new SettingsService(path);
        older.Load();
        Assert.Null(older.Current.HistoryHotkeyValue); // a file of an older version: no shortcut
    }
}
