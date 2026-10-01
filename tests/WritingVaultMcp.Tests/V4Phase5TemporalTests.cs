using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase5TemporalTests
{
    [Fact]
    public async Task DateOnlySessionReportsDayWideTemporalAgeBounds()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Day precision", "America/Vancouver"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity,
            CanonEntityType.Character, "Alex", Birth: StoryDate.ExactDate(new DateOnly(2000, 1, 1))))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Day precision");
        session.SetDate(new DateOnly(2030, 3, 10), "America/Vancouver");
        var clock = await reads.EffectiveClockAsync(continuity, CancellationToken.None);
        Assert.Equal("DateOnly", clock.Status);
        Assert.Null(clock.CurrentTime);
        Assert.Equal(new DateOnly(2030, 3, 10), clock.CurrentDate);
        var temporal = new AccessV4TemporalService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)),
            session, reads);
        Assert.True((await temporal.SetProfileAsync(new(Guid.NewGuid().ToString(),
            "Alex", null, true))).Success);
        var age = await temporal.AgeAsync(new("Alex"));
        Assert.Null(age.AsOf);
        Assert.Equal(new DateOnly(2030, 3, 10), age.AsOfDate);
        Assert.True(age.Biological.MaximumYears > age.Biological.MinimumYears);
        var storyClockHours = (age.Biological.MaximumYears!.Value - age.Biological.MinimumYears!.Value)
            * 365.2425d * 24d;
        // Aging is measured on timezone-free story dates: this is a full civil
        // story day even when its corresponding UTC interval is shorter.
        Assert.InRange(storyClockHours, 23.99d, 24.01d);
        var reference = await references.ReferenceAsync("Character", character);
        var state = await reads.CurrentTemporalStateAsync(reference);
        Assert.Equal("DateOnly", state.GetProperty("status").GetString());
        Assert.Empty((await vault.Integrity.VerifyAsync()).Issues);
    }

    [Fact]
    public async Task KirstysLostYearSeparatesCalendarLegalBiologicalAndExperiencedAge()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Lostville", "America/Vancouver"))).ResourceKey!);
        var character = int.Parse((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity,
            CanonEntityType.Character, "Kirsty", Birth: StoryDate.ExactDate(new DateOnly(2012, 8, 11))))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.WorldEvent,
            "The Disappearance", Occurred: StoryDate.ExactDate(new DateOnly(2023, 8, 14))));
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Lostville");
        session.SetTime(new DateTimeOffset(2024, 9, 23, 12, 0, 0, TimeSpan.FromHours(-7)), "America/Vancouver");
        var targets = new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references));
        var temporal = new AccessV4TemporalService(vault.Factory, vault.Coordinator, references, targets, session, reads);

        var created = await temporal.CreateEffectAsync(new(Guid.NewGuid().ToString(), "Kirsty", "The Lost Year",
            new(V4StoryDateKind.KnownRange, Lower: "2023-08-14", Upper: "2024-09-23"), 0, 0, CauseWorldEvent:"The Disappearance"));
        Assert.True(created.Success, created.Message);
        var result = await temporal.AgeAsync(new("Kirsty"));

        Assert.Equal(12, result.Calendar.ExactYears);
        Assert.Equal(12, result.Legal.ExactYears);
        Assert.InRange(result.Biological.ExactYears!.Value, 10.9, 11.1);
        Assert.InRange(result.Experienced.ExactYears!.Value, 10.9, 11.1);
        Assert.Single(result.AppliedEffects);
        var effect=await reads.GetAsync(new(await references.ReferenceAsync("CharacterTemporalEffect",int.Parse(created.ResourceKey!))));
        Assert.Equal("The Disappearance",Assert.Single(effect.Sections["worldEvent"].Items).Label);
    }

    [Fact]
    public async Task PreviewRejectsOverlapAndInvalidRatesWithoutWriting()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Temporal", "UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity, CanonEntityType.Character,
            "Alex", Birth: StoryDate.ExactDate(new DateOnly(2000, 1, 1))));
        var (reads, session, references) = vault.V4(); session.SelectContinuity(continuity, "Temporal");
        var temporal = new AccessV4TemporalService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)), session, reads);
        Assert.True((await temporal.CreateEffectAsync(new(Guid.NewGuid().ToString(), "Alex", "Pause",
            new(V4StoryDateKind.KnownRange, Lower: "2020-01-01", Upper: "2021-01-01"), 0, 0))).Success);

        var overlap = await temporal.PreviewAsync(new("Alex",
            new(V4StoryDateKind.KnownRange, Lower: "2020-06-01", Upper: "2022-01-01"), 1, 1));
        Assert.False(overlap.Valid);
        Assert.Contains(overlap.Conflicts, issue => issue.Code == "temporal.effect_overlap");
        var uncertain = await temporal.PreviewAsync(new("Alex",
            new(V4StoryDateKind.UncertainRange, Lower: "2022-01-01", Upper: "2023-01-01"), 0, 0));
        Assert.False(uncertain.Valid);
        Assert.Contains(uncertain.Conflicts, issue => issue.Code == "temporal.effect_period_uncertain");
        Assert.Equal("temporal.effect_period_uncertain", (await temporal.CreateEffectAsync(new(
            Guid.NewGuid().ToString(), "Alex", "Uncertain pause",
            new(V4StoryDateKind.UncertainRange, Lower: "2022-01-01", Upper: "2023-01-01"), 0, 0))).Code);
        var invalid = await temporal.PreviewAsync(new("Alex",
            new(V4StoryDateKind.KnownRange, Lower: "2022-01-01", Upper: "2023-01-01"), double.NaN, 1));
        Assert.False(invalid.Valid);
    }

    [Fact]
    public async Task FuzzyBirthOpenEffectAndDeathProduceBoundsAndWarnings()
    {
        await using var vault=await TestVault.CreateAsync();
        var continuity=int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(),"Bounds","UTC"))).ResourceKey!);
        var birth=new StoryDate(StoryDateKind.Range,new DateTime(2000,1,1),new DateTime(2000,12,31),true,true,"born in 2000","Gregorian");
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),continuity,CanonEntityType.Character,"Bounded",Birth:birth,
            Death:StoryDate.ExactDate(new DateOnly(2010,1,1))));
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Bounds");session.SetTime(new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero),"UTC");
        var temporal=new AccessV4TemporalService(vault.Factory,vault.Coordinator,references,new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,vault.Coordinator,references)),session,reads);
        Assert.True((await temporal.CreateEffectAsync(new(Guid.NewGuid().ToString(),"Bounded","Open pause",
            new(V4StoryDateKind.After,Lower:"2005-01-01"),0,0))).Success);
        var age=await temporal.AgeAsync(new("Bounded"));
        Assert.Null(age.Biological.ExactYears);
        Assert.True(age.Biological.MaximumYears<=10.1);
        Assert.Contains(age.Warnings,x=>x.Contains("birth date is uncertain",StringComparison.OrdinalIgnoreCase));
        Assert.Contains(age.Warnings,x=>x.Contains("open boundary",StringComparison.OrdinalIgnoreCase));
        Assert.Single(age.AppliedEffects);
    }

    [Fact]
    public async Task UnknownBirthDoesNotClaimAgeBounds()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Unknown birth", "UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(), continuity,
            CanonEntityType.Character, "Mara", Birth: StoryDate.Unknown()));
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Unknown birth");
        session.SetTime(new DateTimeOffset(2026, 9, 29, 12, 0, 0, TimeSpan.Zero), "UTC");
        var temporal = new AccessV4TemporalService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)), session, reads);

        var age = await temporal.AgeAsync(new("Mara"));

        Assert.Equal("BirthDateUnknown", age.Status);
        Assert.Null(age.Calendar.MinimumYears);
        Assert.Contains(age.Warnings, warning => warning.Contains("cannot be calculated", StringComparison.Ordinal));
        Assert.DoesNotContain(age.Warnings, warning => warning.Contains("reported as bounds", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisabledProfileRetainsEffectsButExcludesThemAndLeapDayPolicyIsStable()
    {
        await using var vault=await TestVault.CreateAsync();
        var continuity=int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(),"Leap","UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),continuity,CanonEntityType.Character,"Leapling",Birth:StoryDate.ExactDate(new DateOnly(2000,2,29))));
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Leap");session.SetTime(new DateTimeOffset(2021,2,28,12,0,0,TimeSpan.Zero),"UTC");
        var temporal=new AccessV4TemporalService(vault.Factory,vault.Coordinator,references,new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,vault.Coordinator,references)),session,reads);
        Assert.True((await temporal.CreateEffectAsync(new(Guid.NewGuid().ToString(),"Leapling","Pause",new(V4StoryDateKind.KnownRange,Lower:"2020-01-01",Upper:"2021-01-01"),0,0))).Success);
        Assert.True((await temporal.SetProfileAsync(new(Guid.NewGuid().ToString(),"Leapling",1,false))).Success);
        var age=await temporal.AgeAsync(new("Leapling"));
        Assert.Equal(21,age.Calendar.ExactYears);Assert.Equal(age.Calendar.ExactYears,age.Biological.ExactYears);
        Assert.Empty(age.AppliedEffects);Assert.Contains(age.Warnings,x=>x.Contains("disabled",StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ReplacementPreviewExcludesOriginalEffectAndProfileMutationMapsToCharacter()
    {
        await using var vault=await TestVault.CreateAsync();
        var continuity=int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(),"Preview parity","UTC"))).ResourceKey!);
        await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),continuity,CanonEntityType.Character,"Preview Person",Birth:StoryDate.ExactDate(new DateOnly(2000,1,1))));
        var (reads,session,references)=vault.V4();session.SelectContinuity(continuity,"Preview parity");session.SetTime(new DateTimeOffset(2022,1,1,0,0,0,TimeSpan.Zero),"UTC");
        var temporal=new AccessV4TemporalService(vault.Factory,vault.Coordinator,references,new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory,vault.Coordinator,references)),session,reads);
        var created=await temporal.CreateEffectAsync(new(Guid.NewGuid().ToString(),"Preview Person","Old pause",new(V4StoryDateKind.KnownRange,Lower:"2020-01-01",Upper:"2021-01-01"),0,0));
        Assert.True(created.Success,created.Message);var effectRef=await references.ReferenceAsync("CharacterTemporalEffect",int.Parse(created.ResourceKey!));
        var preview=await temporal.PreviewAsync(new("Preview Person",new(V4StoryDateKind.KnownRange,Lower:"2020-01-01",Upper:"2021-01-01"),1,1,ExcludeEffectRef:effectRef));
        Assert.True(preview.Valid);Assert.NotNull(preview.ProjectedAge);Assert.True(preview.ProjectedAge!.Biological.ExactYears>21.9);
        var profile=await temporal.SetProfileAsync(new(Guid.NewGuid().ToString(),"Preview Person",1,false));
        Assert.True(profile.Success,profile.Message);
        var mapped=await new V4ResultMapper(references).MutationAsync(profile);
        Assert.True(mapped.Success);Assert.Equal(V4RecordKind.Character,Assert.Single(mapped.Affected).Kind);Assert.Null(mapped.Affected[0].Version);
        var profileView=await temporal.ProfileAsync("Preview Person");
        Assert.True(profileView.GetProperty("exists").GetBoolean());Assert.False(profileView.GetProperty("enabled").GetBoolean());
        Assert.Equal(2,profileView.GetProperty("version").GetInt32());Assert.Equal("CalendarAge",profileView.GetProperty("legalAgePolicy").GetString());
    }
}
