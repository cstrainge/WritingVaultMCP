using System.Text.Json;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase3SemanticsTests
{
    [Fact]
    public async Task NaturalResolutionIsExactScopedAliasAwareAndBounded()
    {
        await using var vault = await TestVault.CreateAsync();
        var firstContinuity = await CreateContinuity(vault, "First");
        var secondContinuity = await CreateContinuity(vault, "Second");
        var first = await CreateCharacter(vault, firstContinuity, "Morgan", preferredName: "Mo");
        var second = await CreateCharacter(vault, firstContinuity, "Morgan");
        _ = await CreateCharacter(vault, secondContinuity, "Morgan");
        Assert.True((await vault.Service.AddAliasAsync(new(Guid.NewGuid().ToString(), first, "Captain"))).Success);
        Assert.True((await vault.Service.AddAliasAsync(new(Guid.NewGuid().ToString(), first, "Ｍｏ"))).Success);

        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var resolver = new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references);

        var preferred = await resolver.ResolveAsync("  mo  ", firstContinuity, ["Character"]);
        Assert.Equal(first, preferred.StorageKey);
        Assert.Equal("Mo", preferred.Label);

        var alias = await resolver.ResolveAsync("CAPTAIN", firstContinuity, ["Character"]);
        Assert.Equal(first, alias.StorageKey);
        Assert.Equal(first, (await resolver.ResolveAsync("mo", firstContinuity, ["Character"])).StorageKey);

        var ambiguous = await Assert.ThrowsAsync<V4ResolutionException>(() =>
            resolver.ResolveAsync("Morgan", firstContinuity, ["Character"]));
        Assert.Equal("record.ambiguous", ambiguous.Code);
        Assert.Equal(2, ambiguous.Candidates.Count);
        Assert.All(ambiguous.Candidates, candidate => Assert.DoesNotContain(first.ToString(), JsonSerializer.Serialize(candidate)));

        var isolated = await resolver.ResolveAsync("Morgan", secondContinuity, ["Character"]);
        Assert.NotEqual(first, isolated.StorageKey);
        Assert.NotEqual(second, isolated.StorageKey);
    }

    [Fact]
    public async Task AmbiguityCandidatesAreCappedWithoutGuessing()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = await CreateContinuity(vault, "Crowded");
        for (var index = 0; index < 12; index++)
        {
            var character = await CreateCharacter(vault, continuity, $"Agent {index}");
            Assert.True((await vault.Service.AddAliasAsync(new(
                Guid.NewGuid().ToString(), character, "The Witness"))).Success);
        }
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var resolver = new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references);
        var exception = await Assert.ThrowsAsync<V4ResolutionException>(() =>
            resolver.ResolveAsync("the witness", continuity, ["Character"]));
        Assert.Equal("record.ambiguous", exception.Code);
        Assert.Equal(V4ContractLimits.MaximumAmbiguityCandidates, exception.Candidates.Count);
        Assert.Contains("12", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReferencesEnforceTypeScopeAndDeletionPolicy()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = await CreateContinuity(vault, "Scoped");
        var character = await CreateCharacter(vault, continuity, "Deleted Person");
        var references = new VaultReferenceService(vault.Factory, vault.Coordinator);
        var resolver = new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references);
        var reference = await references.ReferenceAsync("Character", character);
        Assert.True((await vault.Service.AddAliasAsync(new(
            Guid.NewGuid().ToString(), character, "Ghost Alias"))).Success);
        var noteResult = await vault.Service.AddNoteAsync(new(
            Guid.NewGuid().ToString(), character, "Hidden with owner"));
        var noteReference = await references.ReferenceAsync("EntityNote", int.Parse(noteResult.ResourceKey!));

        var wrongType = await Assert.ThrowsAsync<V4ResolutionException>(() =>
            resolver.ResolveAsync(reference, continuity, ["Project"]));
        Assert.Equal("reference.invalid", wrongType.Code);

        Assert.True((await vault.Service.SoftDeleteEntityAsync(new(
            Guid.NewGuid().ToString(), character, 1))).Success);
        var deleted = await Assert.ThrowsAsync<V4ResolutionException>(() =>
            resolver.ResolveAsync(reference, continuity, ["Character"]));
        Assert.Equal("record.deleted", deleted.Code);
        Assert.True((await resolver.ResolveAsync(reference, continuity, ["Character"], true)).IsDeleted);
        var deletedAlias = await Assert.ThrowsAsync<V4ResolutionException>(() =>
            resolver.ResolveAsync("Ghost Alias", continuity, ["Character"]));
        Assert.Equal("record.not_found", deletedAlias.Code);
        var hiddenNote = await Assert.ThrowsAsync<V4ResolutionException>(() =>
            resolver.ResolveAsync(noteReference, continuity, ["EntityNote"]));
        Assert.Equal("record.deleted", hiddenNote.Code);
    }

    [Theory]
    [InlineData(StoryDateKind.ExactDate, "2026-09-28", null, null)]
    [InlineData(StoryDateKind.Month, "2026-09", null, null)]
    [InlineData(StoryDateKind.Year, "2026", null, null)]
    [InlineData(StoryDateKind.KnownRange, null, "2026-01-01", "2027-01-01")]
    [InlineData(StoryDateKind.UncertainRange, null, "2026-01-01", "2027-01-01")]
    [InlineData(StoryDateKind.Before, null, null, "2026-01-01")]
    [InlineData(StoryDateKind.After, null, "2026-01-01", null)]
    public void StoryDateParserCanonicalizesSupportedShapes(
        StoryDateKind kind, string? value, string? lower, string? upper)
    {
        var date = V4StoryDateParser.Parse(new(
            Kind: kind, Value: value, Lower: lower, Upper: upper));
        Assert.Equal(kind, date.Kind);
        Assert.Equal("Gregorian", date.CalendarId);
        Assert.Empty(date.Validate());
        Assert.All(new[] { date.LowerBound, date.UpperBound }.Where(bound => bound is not null),
            bound => Assert.Equal(DateTimeKind.Unspecified, bound!.Value.Kind));
    }

    [Fact]
    public void StoryDateParserRejectsTimezoneMalformedAndContradictoryInputs()
    {
        AssertValidation("date.range_meaning_required", new(Kind: StoryDateKind.Range,
            Lower: "2026-01-01", Upper: "2027-01-01", OriginalText: "Known festival"));
        AssertValidation("date.timezone_forbidden", new(Kind: StoryDateKind.ExactInstant, Value: "2026-09-28T12:00:00Z"));
        AssertValidation("date.year_invalid", new(Kind: StoryDateKind.Year, Value: "999"));
        AssertValidation("date.year_invalid", new(Kind: StoryDateKind.Year, Value: "0099"));
        AssertValidation("date.exact_invalid", new(Kind: StoryDateKind.ExactDate, Value: "2026-02-30"));
        AssertValidation("date.exact_invalid", new(Kind: StoryDateKind.ExactDate, Value: "9999-12-31"));
        AssertValidation("date.instant_invalid", new(Kind: StoryDateKind.ExactInstant, Value: "09/28/2026 12:00"));
        AssertValidation("date.instant_invalid", new(Kind: StoryDateKind.ExactInstant, Value: "2026-09-28T12:00:00."));
        AssertValidation("date.instant_invalid", new(Kind: StoryDateKind.ExactInstant, Value: "2026-09-28T12:00:00.12345678"));
        AssertValidation("date.shape", new(Kind: StoryDateKind.ExactDate, Value: "2026-09-28", Lower: "2026-01-01"));
        AssertValidation("date.shape", new(Kind: StoryDateKind.Unknown, LowerInclusive: true));
        AssertValidation("date.calendar_unsupported", new(Kind: StoryDateKind.Year, Value: "2026", CalendarId: "Julian"));
        Assert.Equal("Gregorian", V4StoryDateParser.Parse(new(
            Kind: StoryDateKind.Year, Value: "2026", CalendarId: " gregorian ")).CalendarId);
    }

    [Fact]
    public void SparseChangesPreserveExplicitNullAndRejectUnknownOrEmptyChanges()
    {
        using var document = JsonDocument.Parse("{\"description\":null,\"name\":\"Revised\"}");
        var changes = document.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value);
        var validated = V4SparseChangeValidator.Validate(changes, new HashSet<string>(["description", "name"], StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, validated["description"].ValueKind);
        Assert.Equal("Revised", validated["name"].GetString());

        Assert.Throws<VaultValidationException>(() => V4SparseChangeValidator.Validate(
            new Dictionary<string, JsonElement>(), new HashSet<string>(["name"], StringComparer.Ordinal)));
        Assert.Throws<VaultValidationException>(() => V4SparseChangeValidator.Validate(
            changes, new HashSet<string>(["description"], StringComparer.Ordinal)));
    }

    private static void AssertValidation(string code, V4StoryDateValue input)
    {
        var exception = Assert.Throws<VaultValidationException>(() => V4StoryDateParser.Parse(input));
        Assert.Contains(exception.Errors, error => error.Code == code);
    }

    private static async Task<int> CreateContinuity(TestVault vault, string name) =>
        int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), name, "UTC"))).ResourceKey!);

    private static async Task<int> CreateCharacter(TestVault vault, int continuity, string name, string? preferredName = null) =>
        int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, name,
            PreferredName: preferredName))).ResourceKey!);
}
