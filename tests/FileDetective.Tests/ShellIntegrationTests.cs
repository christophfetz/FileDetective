using FileDetective.Core;
using Microsoft.Win32;

namespace FileDetective.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Alt+Space", HotkeyModifiers.Alt, 0x20u)]
    [InlineData("alt+space", HotkeyModifiers.Alt, 0x20u)]
    [InlineData("Ctrl+Alt+K", HotkeyModifiers.Control | HotkeyModifiers.Alt, (uint)'K')]
    [InlineData("K+Alt+Ctrl", HotkeyModifiers.Control | HotkeyModifiers.Alt, (uint)'K')]
    [InlineData("Win+F12", HotkeyModifiers.Win, 0x7Bu)]
    [InlineData("Ctrl+Shift+5", HotkeyModifiers.Control | HotkeyModifiers.Shift, (uint)'5')]
    [InlineData("Strg+Leertaste", HotkeyModifiers.Control, 0x20u)]
    [InlineData(" Ctrl + PageDown ", HotkeyModifiers.Control, 0x22u)]
    public void Parse_Valid(string text, HotkeyModifiers modifiers, uint key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(new HotkeyGesture(modifiers, key), gesture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Space")]
    [InlineData("Shift+A")]
    [InlineData("Alt")]
    [InlineData("Alt+")]
    [InlineData("Alt+A+B")]
    [InlineData("Alt+Escape")]
    [InlineData("Alt+F25")]
    [InlineData("Alt+Ü")]
    [InlineData("Alt+Nonsense")]
    public void Parse_Invalid(string? text)
        => Assert.False(HotkeyGesture.TryParse(text, out _));

    [Theory]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Alt+K")]
    [InlineData("Ctrl+Alt+Shift+Win+F1")]
    [InlineData("Alt+PageUp")]
    public void Format_RoundTrips_AndHasFixedOrder(string text)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(text, gesture.Format());
        Assert.True(HotkeyGesture.TryParse(gesture.Format(new System.Globalization.CultureInfo("de-DE")), out var german));
        Assert.Equal(gesture, german);
    }

    [Fact]
    public void Format_GermanNames()
        => Assert.Equal("Strg+Alt+Leertaste", new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20)
            .Format(new System.Globalization.CultureInfo("de-DE")));
}

public class AutostartManagerTests : IDisposable
{
    private readonly string _keyPath = @"Software\FileDetectiveTests\" + Guid.NewGuid().ToString("N");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\FileDetectiveTests", throwOnMissingSubKey: false);

    [Fact]
    public void EnableDisable_WritesTheQuotedCommandWithAutostartArgument()
    {
        var manager = new AutostartManager(_keyPath, "FileDetectiveTest");
        Assert.False(manager.IsEnabled());
        Assert.Null(manager.GetCommand());

        manager.Enable(@"C:\Program Files\FileDetective\FileDetective.exe");
        Assert.True(manager.IsEnabled());
        Assert.Equal("\"C:\\Program Files\\FileDetective\\FileDetective.exe\" --autostart", manager.GetCommand());

        manager.Enable(@"D:\other\fd.exe"); // overwrites
        Assert.Equal("\"D:\\other\\fd.exe\" --autostart", manager.GetCommand());

        manager.Disable();
        Assert.False(manager.IsEnabled());
        manager.Disable(); // twice is fine
    }

    [Fact]
    public void IsEnabled_FollowsTheRegistry_NotAnyOtherState()
    {
        var manager = new AutostartManager(_keyPath, "FileDetectiveTest");
        using (var key = Registry.CurrentUser.CreateSubKey(_keyPath))
        {
            key.SetValue("FileDetectiveTest", "\"x.exe\" --autostart");
        }

        Assert.True(manager.IsEnabled());
        using (var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true)!)
        {
            key.DeleteValue("FileDetectiveTest");
        }

        Assert.False(manager.IsEnabled());
    }

    [Fact]
    public void DefaultsPointAtTheRunKey()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", AutostartManager.DefaultKeyPath);
        Assert.Equal("\"C:\\a b\\x.exe\" --autostart", AutostartManager.BuildCommand(@"C:\a b\x.exe"));
    }
}

public class SingleInstanceGuardTests
{
    private static string UniqueName() => "FileDetectiveTest-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void FirstIsPrimary_SecondIsNot_UntilTheFirstIsGone()
    {
        string name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        Assert.True(first.TryBecomePrimary());

        // A mutex is owned per thread: the second start runs on its own thread, as a second process would.
        bool secondPrimary = true;
        var thread = new Thread(() =>
        {
            using var second = new SingleInstanceGuard(name);
            secondPrimary = second.TryBecomePrimary();
        });
        thread.Start();
        thread.Join();
        Assert.False(secondPrimary);

        first.Dispose();
        bool thirdPrimary = false;
        thread = new Thread(() =>
        {
            using var third = new SingleInstanceGuard(name);
            thirdPrimary = third.TryBecomePrimary();
        });
        thread.Start();
        thread.Join();
        Assert.True(thirdPrimary);
    }

    [Fact]
    public void SecondStart_WakesTheFirst_AlsoIfItSignalsBeforeListening()
    {
        string name = UniqueName();
        using var first = new SingleInstanceGuard(name);
        Assert.True(first.TryBecomePrimary());

        // Signal arrives before the first instance listens: it must not get lost.
        using (var second = new SingleInstanceGuard(name))
        {
            second.SignalPrimary();
        }

        using var woken = new ManualResetEventSlim();
        int count = 0;
        first.WakeRequested += () =>
        {
            Interlocked.Increment(ref count);
            woken.Set();
        };
        first.StartListening();
        Assert.True(woken.Wait(TimeSpan.FromSeconds(5)));

        // and again while listening
        woken.Reset();
        using (var third = new SingleInstanceGuard(name))
        {
            third.SignalPrimary();
        }

        Assert.True(woken.Wait(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, Volatile.Read(ref count));
    }

    [Fact]
    public void BuildName_IsStablePerUserAndFolder()
    {
        string a = SingleInstanceGuard.BuildName("FileDetective", "S-1-5-21-1", @"C:\Data\FD");
        Assert.Equal(a, SingleInstanceGuard.BuildName("FileDetective", "S-1-5-21-1", @"c:\data\fd\"));
        Assert.NotEqual(a, SingleInstanceGuard.BuildName("FileDetective", "S-1-5-21-2", @"C:\Data\FD"));
        Assert.NotEqual(a, SingleInstanceGuard.BuildName("FileDetective", "S-1-5-21-1", @"C:\Data\Other"));
        Assert.StartsWith("FileDetective-S-1-5-21-1-", a);
    }
}

public class LocationStorageTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void RemoveRoot_RemovesItsEntriesFromSearch_AndOthersStay()
    {
        using var f = new StorageFixture(_dir);
        var a = f.Store.AddRoot(@"C:\A", null, RootKind.Local);
        var b = f.Store.AddRoot(@"D:\B", null, RootKind.Local);
        f.Add(a, a.RootEntryId, "report.txt", false);
        f.Add(b, b.RootEntryId, "report.pdf", false);

        var counts = f.Store.CountEntriesByRoot();
        Assert.Equal(2, counts[a.Id]);
        Assert.Equal(2, counts[b.Id]);
        Assert.Equal(2, f.Paths("report").Length);

        Assert.Equal(2, f.Store.RemoveRoot(a.Id));
        Assert.Equal([@"D:\B\report.pdf"], f.Paths("report"));
        Assert.Single(f.Store.GetRoots());
        Assert.False(f.Store.CountEntriesByRoot().ContainsKey(a.Id));

        // The removed location can be added again (and gets a free number).
        var again = f.Store.AddRoot(@"C:\A", null, RootKind.Local);
        Assert.Equal(1, again.Id);
        Assert.Empty(f.Paths("txt"));
    }
}
