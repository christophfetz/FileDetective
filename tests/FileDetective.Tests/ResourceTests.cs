using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace FileDetective.Tests;

/// <summary>
/// PLAN.md 8.6 and stage 7: both language files hold the same keys with non-empty values, the program uses only keys that
/// exist, and no visible text is written directly into XAML.
/// </summary>
public partial class ResourceTests
{
    private static readonly string AppFolder = Path.Combine(RepoRoot(), "src", "FileDetective.App");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FileDetective.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("The repository root (FileDetective.slnx) was not found.");
    }

    private static Dictionary<string, string> ReadResx(string fileName)
    {
        var doc = XDocument.Load(Path.Combine(AppFolder, fileName));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var data in doc.Root!.Elements("data"))
        {
            string name = data.Attribute("name")?.Value ?? throw new InvalidOperationException("data without name in " + fileName);
            Assert.True(result.TryAdd(name, data.Element("value")?.Value ?? string.Empty), $"{fileName}: duplicate key {name}");
        }

        return result;
    }

    [Fact]
    public void BothLanguageFiles_HaveTheSameKeys()
    {
        var english = ReadResx("Strings.resx");
        var german = ReadResx("Strings.de.resx");

        Assert.Empty(english.Keys.Except(german.Keys).OrderBy(k => k, StringComparer.Ordinal));
        Assert.Empty(german.Keys.Except(english.Keys).OrderBy(k => k, StringComparer.Ordinal));
        Assert.True(english.Count > 100);
    }

    [Fact]
    public void NoValueIsEmptyOrWhitespace()
    {
        foreach (string file in new[] { "Strings.resx", "Strings.de.resx" })
        {
            foreach (var (key, value) in ReadResx(file))
            {
                Assert.False(string.IsNullOrWhiteSpace(value), $"{file}: '{key}' is empty");
            }
        }
    }

    [Fact]
    public void GermanTexts_AreReallyTranslated_NotCopiedFromEnglish()
    {
        // Short technical words may be the same in both languages; everything longer than 25 characters must differ.
        var english = ReadResx("Strings.resx");
        var german = ReadResx("Strings.de.resx");
        var copied = english.Where(kv => kv.Value.Length > 25 && german[kv.Key] == kv.Value).Select(kv => kv.Key).ToArray();
        Assert.Empty(copied);
    }

    [Fact]
    public void PlaceholdersMatchBetweenTheLanguages()
    {
        var english = ReadResx("Strings.resx");
        var german = ReadResx("Strings.de.resx");
        foreach (var (key, value) in english)
        {
            Assert.Equal(Placeholders(value), Placeholders(german[key]));
        }
    }

    private static string[] Placeholders(string text)
        => [.. PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value).Distinct().OrderBy(s => s, StringComparer.Ordinal)];

    [GeneratedRegex(@"\{(\d+)(?:[:,][^}]*)?\}")]
    private static partial Regex PlaceholderPattern();

    [Fact]
    public void EveryKeyUsedInCode_Exists()
    {
        var keys = ReadResx("Strings.resx").Keys.ToHashSet();
        var used = new HashSet<string>();
        foreach (string file in Directory.EnumerateFiles(AppFolder, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(@"\obj\", StringComparison.Ordinal) && !f.Contains(@"\bin\", StringComparison.Ordinal)))
        {
            foreach (Match m in KeyLikeLiteral().Matches(File.ReadAllText(file)))
            {
                used.Add(m.Groups[1].Value);
            }
        }

        Assert.NotEmpty(used);
        Assert.Empty(used.Where(k => !keys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>String literals that look like resource keys: Prefix_Name.</summary>
    [GeneratedRegex("\"([A-Z][a-z]+_[A-Za-z0-9]+)\"")]
    private static partial Regex KeyLikeLiteral();

    [Fact]
    public void EveryKey_IsUsedSomewhere()
    {
        // Keeps the files free of dead texts. XAML does not use keys at all (texts are set in code).
        var keys = ReadResx("Strings.resx").Keys;
        string code = string.Concat(
            Directory.EnumerateFiles(AppFolder, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(@"\obj\", StringComparison.Ordinal) && !f.Contains(@"\bin\", StringComparison.Ordinal))
                .Select(File.ReadAllText));
        Assert.Empty(keys.Where(k => !code.Contains('"' + k + '"', StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void Xaml_HasNoVisibleLiteralText()
    {
        // Text, Content, Header, ToolTip, Title, Description, Watermark must be bindings or empty; glyph entities
        // (&#xE721;) for icon fonts are allowed.
        var attribute = new Regex(@"\b(Text|Content|Header|ToolTip|Title|Description|Watermark|Label|Tag)=""([^""]*)""");
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(AppFolder, "*.xaml", SearchOption.AllDirectories))
        {
            foreach (Match m in attribute.Matches(File.ReadAllText(file)))
            {
                string value = m.Groups[2].Value;
                bool allowed = value.Length == 0 || value.StartsWith('{') || Regex.IsMatch(value, @"^(&#x[0-9A-Fa-f]+;)+$");
                if (!allowed)
                {
                    offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
                }
            }

            // Text between tags (e.g. <TextBlock>Hello</TextBlock>) is not allowed either.
            string withoutComments = Regex.Replace(File.ReadAllText(file), "<!--.*?-->", string.Empty, RegexOptions.Singleline);
            foreach (Match m in Regex.Matches(withoutComments, @">([^<>{]*[A-Za-z]{2,}[^<>]*)</"))
            {
                offenders.Add($"{Path.GetFileName(file)}: element text '{m.Groups[1].Value.Trim()}'");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Code_HasNoVisibleLiteralText_InWindowsAndViewModels()
    {
        // Heuristic: assignments of a literal with letters and a space to Text/Content/Header/Title/ToolTip/Description,
        // and literals in MessageBox.Show or ShowBalloon calls, must not exist. Resource keys have no spaces.
        var assignment = new Regex(@"\b(Text|Content|Header|Title|ToolTip|Description)\s*=\s*""[^""]*[A-Za-z]+[^""]*""");
        var offenders = new List<string>();
        foreach (string file in Directory.EnumerateFiles(AppFolder, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(@"\obj\", StringComparison.Ordinal) && !f.Contains(@"\bin\", StringComparison.Ordinal)))
        {
            string text = File.ReadAllText(file);
            foreach (Match m in assignment.Matches(text))
            {
                offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
            }
        }

        // (The language list shows "Deutsch" and "English" in their own language on purpose; they are added with
        // Items.Add and are not covered by this pattern.)
        Assert.Empty(offenders);
    }
}
