using FileDetective.Core;

namespace FileDetective.Tests;

/// <summary>Stage 13 (PLAN.md 11.4): notes of the user for images.</summary>
public sealed class ImageNoteTests : IDisposable
{
    private readonly ImageTextFixture _x = new();
    private ImageNoteService _notes;

    public ImageNoteTests()
    {
        _notes = CreateNotes();
        _notes.Load();
    }

    public void Dispose() => _x.Dispose();

    private static string P(string name) => ImageTextFixture.Folder + "\\" + name;

    private ImageNoteService CreateNotes()
        => new(_x.F.Store, _x.Texts, new DatabaseEntryResolver(_x.F.Db), _x.Files, path => path.StartsWith(@"R:\", StringComparison.OrdinalIgnoreCase));

    private int AddImage(string name, string text = "", long? ticks = null)
        => _x.AddImage(_x.FolderId, ImageTextFixture.Folder, name, text, ticks);

    private IReadOnlyList<int> Find(string text) => _x.Texts.Find(SearchQuery.Parse(text, SearchMode.ImageText));

    private SearchResult Search(string text)
        => new ImageTextSearch(_x.Texts, new DatabaseEntryResolver(_x.F.Db), _x.F.Store.GetImageTexts, () => [ImageTextFixture.Folder])
            .Search(SearchQuery.Parse(text, SearchMode.ImageText));

    private long StoredNotes() => _x.Scalar("SELECT COUNT(*) FROM notes");

    [Fact]
    public void Note_IsFound_AloneTogetherWithTheText_AndInTheSnippet()
    {
        int photo = AddImage("photo.png", "Rechnung Müller");
        _x.Enable();
        Assert.True(_notes.Save(photo, P("photo.png"), "  Kindergarten Gruppenfoto  "));

        Assert.Equal([photo], Find("kindergarten"));
        Assert.Equal([photo], Find("mueller gruppenfoto")); // one term in the text, one in the note
        Assert.Empty(Find("kindergarten urlaub"));
        Assert.Equal("Kindergarten Gruppenfoto", _notes.Get(photo));

        var hit = Assert.Single(Search("kindergarten").Hits);
        Assert.StartsWith("Kindergarten Gruppenfoto", hit.Snippet!.Text, StringComparison.Ordinal);
        Assert.Contains("Rechnung", hit.Snippet.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Note_WorksWithTheTextRecognitionOff_AndShowsWithAnEmptyInput()
    {
        int a = AddImage("a.png", ticks: FakeLocalFiles.BaseTicks);
        int b = AddImage("b.png", ticks: FakeLocalFiles.BaseTicks + TimeSpan.TicksPerHour);
        _notes.Save(a, P("a.png"), "Ausweis");
        _notes.Save(b, P("b.png"), "Kinder");

        Assert.Equal(0, _x.Recognizer.Calls);
        Assert.Equal(0, _x.Files.OpenCount); // a note never opens the image
        Assert.Equal([b, a], Find(string.Empty)); // newest first
        Assert.Equal([a], Find("ausweis"));
        Assert.False(_x.Texts.Contains(a)); // no recognized text: the history still says "no text"
    }

    [Fact]
    public void Notes_SurviveRestart_SwitchingOff_AndRebuild()
    {
        int id = AddImage("scan.png", "Druckertestseite");
        _x.Enable();
        _notes.Save(id, P("scan.png"), "Drucker im Büro");

        _x.Service.Configure(false, []);
        Assert.Equal(0, _x.Rows());
        Assert.Equal(1, StoredNotes());
        Assert.Equal([id], Find("buero"));

        _x.Restart();
        _notes = CreateNotes();
        _notes.Load();
        Assert.Equal("Drucker im Büro", _notes.Get(id));
        Assert.Equal([id], Find("drucker"));

        _x.F.Store.ResetAllEntries();
        Assert.Empty(Find("drucker"));
        Assert.Equal(1, StoredNotes());
        var root = _x.F.Store.GetRoot(_x.Root.Id)!;
        int folder = _x.F.Add(root, root.RootEntryId, "Shots", true);
        int again = _x.AddImage(folder, ImageTextFixture.Folder, "scan.png", "Druckertestseite");
        Assert.NotEqual(id, again);
        Assert.Equal("Drucker im Büro", _notes.Get(again));
        Assert.Equal([again], Find("drucker"));
    }

    [Fact]
    public void DeletedImage_LosesItsNote()
    {
        int id = AddImage("gone.png");
        _notes.Save(id, P("gone.png"), "weg");

        _x.Files.Remove(P("gone.png"));
        _x.F.Store.DeleteSubtree(id);

        Assert.Equal(0, StoredNotes());
        Assert.Null(_notes.GetByPath(P("gone.png")));
        Assert.Empty(Find("weg"));
        Assert.Equal(0, _x.Files.OpenCount);
    }

    [Fact]
    public void ImageStillOnDisk_KeepsItsNote_AndGetsItBackWhenListedAgain()
    {
        int id = AddImage("still.png");
        _notes.Save(id, P("still.png"), "noch da");

        _x.F.Store.DeleteSubtree(id); // e.g. a vanished entry that is in fact still there
        Assert.Equal(1, StoredNotes());
        Assert.Empty(Find("noch"));

        int again = _x.AddImage(_x.FolderId, ImageTextFixture.Folder, "still.png", string.Empty);
        Assert.Equal([again], Find("noch"));
    }

    [Fact]
    public void RemovedLocation_KeepsTheNote()
    {
        int id = AddImage("kept.png");
        _notes.Save(id, P("kept.png"), "bleibt");
        _x.Files.Remove(P("kept.png")); // even if the file cannot be seen: the location is gone, not the image

        _x.F.Store.RemoveRoot(_x.Root.Id);

        Assert.Equal(1, StoredNotes());
        Assert.Equal("bleibt", _notes.GetByPath(P("kept.png")));
    }

    [Fact]
    public void RenamedImage_TakesItsNoteAlong()
    {
        int id = AddImage("old.png");
        _notes.Save(id, P("old.png"), "wandert mit");

        Assert.True(_x.F.Store.Rename(id, "new.png"));

        Assert.Null(_notes.GetByPath(P("old.png")));
        Assert.Equal("wandert mit", _notes.GetByPath(P("new.png")));
        Assert.Equal(1, StoredNotes());
        Assert.Equal(1, _x.Scalar(@"SELECT COUNT(*) FROM notes WHERE path = 'R:\Shots\new.png'"));
        Assert.Equal([id], Find("wandert"));
    }

    [Fact]
    public void BlankText_RemovesTheNote_AndLongTextIsCut()
    {
        int id = AddImage("x.png");
        Assert.True(_notes.Save(id, P("x.png"), "etwas"));
        Assert.False(_notes.Save(id, P("x.png"), "etwas")); // unchanged
        Assert.True(_notes.Save(id, P("x.png"), "   \r\n "));
        Assert.Equal(0, StoredNotes());
        Assert.Null(_notes.Get(id));
        Assert.Empty(Find(string.Empty));

        Assert.Equal("a\nb", ImageNoteService.Clean(" a\r\nb "));
        Assert.Equal(ImageNoteService.MaxLength, ImageNoteService.Clean(new string('x', 5000)).Length);
    }

    [Fact]
    public void History_FindsImagesByTheirNote()
    {
        int id = AddImage("kids.png");
        AddImage("other.png");
        _notes.Save(id, P("kids.png"), "Kindergarten");
        var history = new ImageHistory(
            new StoreImageHistorySource(_x.F.Store, new DatabaseEntryResolver(_x.F.Db)), _x.Texts, () => [ImageTextFixture.Folder]);

        var snapshot = history.Load();
        Assert.Equal(2, snapshot.Images.Count);
        var result = history.Filter(snapshot, "kindergarten", null);
        Assert.Equal([id], result.Images.Select(i => i.EntryId));
    }
}
