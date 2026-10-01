using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Tests;

/// <summary>Builds a disposable browser fixture that exercises limits and hostile stored text.</summary>
public sealed class V4Phase9BrowserFixtureTests
{
    [Fact]
    public async Task BuildDisposablePagingAndAdversarialRecordPages()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Browser Stress Preview", "UTC", "A disposable paging and rendering fixture."))).ResourceKey!);

        // The first continuation crosses both the 100-continuity and 60-result browser limits.
        for (var index = 1; index <= 101; index++)
            Assert.True((await vault.Service.CreateContinuityAsync(new(
                Guid.NewGuid().ToString(), $"Extra continuity {index:000}", "UTC"))).Success);

        int firstCharacter = 0;
        for (var index = 1; index <= 112; index++)
        {
            var result = await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity,
                CanonEntityType.Character, $"Paging character {index:000}",
                Birth: index == 1 ? StoryDate.Year(2000, "around the turn of the millennium") : null));
            Assert.True(result.Success, result.Message);
            if (index == 1) firstCharacter = int.Parse(result.ResourceKey!);
        }

        var longName = new string('N', 240);
        Assert.True((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Location, longName))).Success);
        Assert.True((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Object, "Unconnected object"))).Success);
        Assert.True((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.WorldEvent, "The uncertain week",
            Occurred: new StoryDate(StoryDateKind.UncertainRange, new DateTime(2001, 9, 1), new DateTime(2001, 9, 8),
                OriginalText: "sometime in the first week of September")))).Success);
        Assert.True((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.WorldEvent, "Undated memory",
            Occurred: StoryDate.Unknown("date deliberately unknown")))).Success);

        var hostileMarkdown = "# Stored markup\n<script>window.vaultXss = true</script>\n" +
            "<img src=x onerror=\"window.vaultXss = true\">\n" +
            "[unsafe link](javascript:alert(1)) [ordinary link](https://example.org/safe)\n" +
            "![remote image](https://example.org/tracker.png)\n\n" + new string('L', 60_000);
        Assert.True((await vault.Service.AddNoteAsync(new(
            Guid.NewGuid().ToString(), firstCharacter, hostileMarkdown, "Unsafe markup stays text"))).Success);
        for (var index = 1; index <= 26; index++)
            Assert.True((await vault.Service.AddNoteAsync(new(
                Guid.NewGuid().ToString(), firstCharacter, $"Note {index:00}", $"Paged note {index:00}"))).Success);

        var output = Path.Combine(RepositoryRoot(), "artifacts", "phase9-browser");
        Directory.CreateDirectory(output);
        File.Copy(vault.DatabasePath, Path.Combine(output, "WritingVault.Phase9Browser.accdb"), true);
        Directory.CreateDirectory(Path.Combine(output, "backup"));
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WritingVaultMcp.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
