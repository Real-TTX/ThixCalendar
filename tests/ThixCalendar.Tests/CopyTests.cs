namespace ThixCalendar.Tests;

/// <summary>
/// MatDesktop (from MatDesign) and the contract of MatCloud are copies that are never edited here: every file matches the
/// hash its manifest names (SHA-256 of the text with LF line ends), and no file is there that the manifest does not name.
/// To change one, change it at its source and copy it again (see CLAUDE.md).
/// </summary>
public sealed class CopyTests
{
    [Fact]
    public void The_copy_of_MatDesktop_is_unchanged() =>
        AssertCopy(Path.Combine(RepositoryRoot(), "src", "ThixCalendar", "MatDesktop"), "matdesktop.version", "MatDesktop ", "*.*",
            "copy it again from MatDesign (node tools/matdesktop-copy.js there) instead of editing it");

    [Fact]
    public void The_copy_of_the_MatCloud_contract_is_unchanged() =>
        AssertCopy(Path.Combine(RepositoryRoot(), "src", "ThixCalendar.Core", "MatCloud"), "matcloud.version", "MatCloud.Contract ", "*.cs",
            "copy the contract again from MatCloud (node ../MatCloud/tools/contract-copy.js src/ThixCalendar.Core) instead of editing it");

    private static void AssertCopy(string folder, string manifest, string header, string pattern, string advice)
    {
        var lines = File.ReadAllLines(Path.Combine(folder, manifest));
        Assert.StartsWith(header, lines[0]);
        var listed = new List<string>();
        foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
        {
            var (hash, file) = (line[..64], line[66..]);
            listed.Add(file);
            var text = File.ReadAllText(Path.Combine(folder, file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
            Assert.True(hash == actual, $"{file} was changed: {advice}");
        }

        var present = Directory.GetFiles(folder, pattern, SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(folder, file).Replace('\\', '/'))
            .Where(file => file != manifest);
        Assert.Equal(listed.Order(StringComparer.Ordinal), present.Order(StringComparer.Ordinal));
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "ThixCalendar.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("The repository was not found above the test folder.");
    }
}
