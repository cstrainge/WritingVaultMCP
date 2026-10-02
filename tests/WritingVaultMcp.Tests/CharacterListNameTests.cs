using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class CharacterListNameTests
{
    [Fact]
    public async Task FullNamesAndPreferredNamesAreOrderedAcrossPageBoundaries()
    {
        await using var vault = await TestVault.CreateAsync();
        var created = await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Character Names", "UTC"));
        var continuity = int.Parse(created.ResourceKey!);
        async Task Add(string first, string? middle = null, string? family = null, string? preferred = null)
        {
            var result = await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity,
                CanonEntityType.Character, first, MiddleNames: middle, FamilyName: family, PreferredName: preferred));
            Assert.True(result.Success, result.Message);
        }
        await Add("Robin", family: "Adams", preferred: "Rob");
        await Add("Francis", "Anne", "Bell", "Frankie");
        await Add("Aurora", "Nyx", "Thyme", "Aurora");
        await Add("The Patron");
        await Add("Betsy", family: "Jones");
        await Add("Robert", family: "Smith", preferred: "Rob");
        await Add("Anne", preferred: "ANNE");
        var (reads, session, _) = vault.V4(); session.SelectContinuity(continuity, "Character Names");
        var names = new List<string>(); var refs = new HashSet<string>(); string? cursor = null;
        do
        {
            var page = await reads.SearchAsync(new(Kinds: [V4RecordKind.Character], Limit: 2, Cursor: cursor));
            foreach (var item in page.Items) { names.Add(item.Label); Assert.True(refs.Add(item.Ref)); }
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(new[] { "Anne", "Aurora Nyx Thyme", "Betsy Jones", "\"Frankie\" Francis Anne Bell",
            "\"Rob\" Robert Smith", "\"Rob\" Robin Adams", "The Patron" }, names);
        var full = await reads.SearchAsync(new(Text: "Aurora Nyx Thyme", Kinds: [V4RecordKind.Character]));
        Assert.Equal("Aurora Nyx Thyme", Assert.Single(full.Items).Label);
        var family = await reads.SearchAsync(new(Text: "Smith", Kinds: [V4RecordKind.Character]));
        Assert.Equal("\"Rob\" Robert Smith", Assert.Single(family.Items).Label);
    }
}
