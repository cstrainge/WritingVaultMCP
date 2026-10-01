using System.Data.Common;
using System.Data.OleDb;
using System.Globalization;
using System.Text.Json;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Mcp;
using WritingVaultMcp.Mcp.V4;

namespace WritingVaultMcp.Infrastructure.Access;

public sealed class AccessV4TemporalService(
    IAccessConnectionFactory factory,
    VaultWriteCoordinator coordinator,
    VaultReferenceService references,
    V4TargetResolver targets,
    VaultSessionContext session,
    AccessV4ReadService reads)
{
    private sealed record Effect(int Id, string Name, StoryDate Period, double BiologicalRate,
        double ExperiencedRate, int Version, bool Deleted);
    private sealed record CharacterData(int Id, string Name, int Version, bool Deleted, StoryDate Birth,
        StoryDate Death, bool Tracking, string LegalPolicy, IReadOnlyList<Effect> Effects);

    public async Task<JsonElement> ProfileAsync(string characterReference, CancellationToken token = default)
    {
        var continuity = session.RequireContinuityId();
        var target = await ResolveCharacterAsync(characterReference, continuity, token).ConfigureAwait(false);
        await using var connection = factory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection,
                "SELECT [Enabled],[LegalAgePolicy],[Notes],[Version],[IsDeleted] FROM [CharacterTemporalProfiles] WHERE [CharacterId]=?")
            .Add(OleDbType.Integer, target.StorageKey);
        var rows = await command.QueryAsync(reader => new
        {
            Enabled = reader.GetBoolean(0), Policy = reader.GetString(1),
            Notes = reader.IsDBNull(2) ? null : reader.GetString(2), Version = reader.GetInt32(3),
            Deleted = reader.GetBoolean(4)
        }, token).ConfigureAwait(false);
        if (rows.Count == 0)
            return JsonSerializer.SerializeToElement(new { exists = false, enabled = false, legalAgePolicy = "CalendarAge", notes = (string?)null, version = (int?)null, isDeleted = false });
        var row = rows[0];
        return JsonSerializer.SerializeToElement(new { exists = true, enabled = row.Enabled && !row.Deleted, legalAgePolicy = row.Policy, notes = row.Notes, version = (int?)row.Version, isDeleted = row.Deleted });
    }

    public async Task<V4CharacterAgeResult> AgeAsync(V4CharacterAgeRequest request, CancellationToken token = default)
    {
        var continuity = session.RequireContinuityId();
        var target = await targets.EntityAsync(request.Character, continuity, token).ConfigureAwait(false);
        if (!target.ResourceType.Equals("Character", StringComparison.OrdinalIgnoreCase))
            throw new V4ResolutionException("reference.type_invalid", "A character is required.");
        var data = await LoadAsync(target.StorageKey, token).ConfigureAwait(false);
        if (request.At is null && session.CurrentDateOverride is { } date)
            return await CalculateDayAsync(target, data, date, null, token).ConfigureAwait(false);
        var (instant, zone) = await ResolveAsOfAsync(continuity, request.At, token).ConfigureAwait(false);
        return await CalculateAsync(target, data, instant, zone, null, token).ConfigureAwait(false);
    }

    public async Task<V4TemporalPreviewResult> PreviewAsync(V4TemporalEffectPreviewRequest request, CancellationToken token = default)
    {
        var revision = await reads.RevisionAsync(token).ConfigureAwait(false);
        var conflicts = new List<V4Error>();
        StoryDate period;
        try
        {
            period = Parse(request.Period); ValidateEffectPeriod(period); ValidateRates(request.BiologicalRate, request.ExperiencedRate);
            ValidateOptionalText(request.Name,255,"name");
        }
        catch (VaultValidationException exception)
        {
            conflicts.AddRange(exception.Errors.Select(e => new V4Error(e.Code, e.Message, Details: [new(e.Field, e.Message)])));
            return new(false, conflicts, null, revision);
        }
        var continuity = session.RequireContinuityId();
        var target = await targets.EntityAsync(request.Character, continuity, token).ConfigureAwait(false);
        if (!target.ResourceType.Equals("Character", StringComparison.OrdinalIgnoreCase))
            throw new V4ResolutionException("reference.type_invalid", "A character is required.");
        var data = await LoadAsync(target.StorageKey, token).ConfigureAwait(false);
        int? excluded = null;
        if (!string.IsNullOrWhiteSpace(request.ExcludeEffectRef))
        {
            var excludedTarget=await targets.RelationAsync(request.ExcludeEffectRef,continuity,true,token).ConfigureAwait(false);
            if(!excludedTarget.ResourceType.Equals("CharacterTemporalEffect",StringComparison.OrdinalIgnoreCase)||data.Effects.All(effect=>effect.Id!=excludedTarget.StorageKey))
                throw new V4ResolutionException("reference.type_invalid","excludeEffectRef must identify an effect belonging to this character.");
            excluded=excludedTarget.StorageKey;
        }
        var overlaps = data.Effects.Where(e => !e.Deleted && e.Id != excluded && e.Period.Overlaps(period)).ToArray();
        if (overlaps.Length > 0)
            conflicts.Add(new("temporal.effect_overlap", "The proposed temporal effect overlaps an active effect.",
                Candidates: overlaps.Select(e => EffectSummary(e, session.ContinuityName)).ToArray()));
        if (conflicts.Count > 0) return new(false, conflicts, null, revision);
        var hypothetical = new Effect(-1, request.Name?.Trim() is { Length: > 0 } n ? n : "Preview effect", period,
            request.BiologicalRate, request.ExperiencedRate, 0, false);
        if (request.At is null && session.CurrentDateOverride is { } date)
        {
            var day = await CalculateDayAsync(target, data with { Tracking = true,
                Effects = data.Effects.Where(effect => effect.Id != excluded).ToArray() },
                date, hypothetical, token).ConfigureAwait(false);
            return new(true, [], day, day.ObservedRevision);
        }
        var (instant, zone) = await ResolveAsOfAsync(continuity, request.At, token).ConfigureAwait(false);
        var projected = await CalculateAsync(target, data with { Tracking = true, Effects=data.Effects.Where(effect=>effect.Id!=excluded).ToArray() }, instant, zone, hypothetical, token).ConfigureAwait(false);
        return new(true, [], projected, projected.ObservedRevision);
    }

    public async Task<VaultMutationResult> SetProfileAsync(V4TemporalProfileSetRequest request, CancellationToken token = default)
    {
        if (!request.LegalAgePolicy.Equals("CalendarAge", StringComparison.OrdinalIgnoreCase))
            return new(false, "validation.legal_age_policy", Message: "Only CalendarAge is supported.");
        try{ValidateOptionalText(request.Notes,V4ContractLimits.MaximumLongTextLength,"notes");}
        catch(VaultValidationException e){return new(false,e.Errors[0].Code,Message:e.Message);}
        var continuity = session.RequireContinuityId();
        V4ResolvedTarget character;
        try { character = await ResolveCharacterAsync(request.Character, continuity, token).ConfigureAwait(false); }
        catch (V4ResolutionException e) { return new(false, e.Code, Message: e.Message); }
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException e) { return new(false, "validation.mutation_token", Message: e.Message); }
        return await coordinator.ExecuteAsync(operation, "v4.temporal.profile.set", request, "character_temporal_profile_set", session.ClientLabel,
            async (context, ct) =>
            {
                await RequireCharacterAsync(context, character.StorageKey, continuity, ct).ConfigureAwait(false);
                using var find = context.Command("SELECT [Version],[IsDeleted] FROM [CharacterTemporalProfiles] WHERE [CharacterId]=?")
                    .Add(OleDbType.Integer, character.StorageKey);
                var rows = await find.QueryAsync(r => (Version: r.GetInt32(0), Deleted: r.GetBoolean(1)), ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                int version;
                if (rows.Count == 0)
                {
                    if (request.ExpectedVersion is not null) throw new VaultCommandException("concurrency.conflict", "The temporal profile does not exist.");
                    using var insert = context.Command("INSERT INTO [CharacterTemporalProfiles] ([CharacterId],[Enabled],[LegalAgePolicy],[Notes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?)")
                        .Add(OleDbType.Integer, character.StorageKey).Add(OleDbType.Boolean, request.Enabled)
                        .Add(OleDbType.VarWChar, "CalendarAge", 30).Add(OleDbType.LongVarWChar, request.Notes)
                        .Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                    await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false); version = 1;
                }
                else
                {
                    if (request.ExpectedVersion is null || request.ExpectedVersion != rows[0].Version)
                        throw new VaultCommandException("concurrency.conflict", $"The current version is {rows[0].Version}.", actualVersion: rows[0].Version);
                    using var update = context.Command("UPDATE [CharacterTemporalProfiles] SET [Enabled]=?,[LegalAgePolicy]='CalendarAge',[Notes]=?,[IsDeleted]=False,[DeletedAtUtc]=Null,[DeletedOperationId]=Null,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [CharacterId]=? AND [Version]=?")
                        .Add(OleDbType.Boolean, request.Enabled).Add(OleDbType.LongVarWChar, request.Notes).Add(OleDbType.Date, now)
                        .Add(OleDbType.Integer, character.StorageKey).Add(OleDbType.Integer, rows[0].Version);
                    if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new VaultCommandException("concurrency.conflict", "The temporal profile changed.");
                    version = rows[0].Version + 1;
                }
                return new VaultMutationOutcome("Character", character.StorageKey.ToString(CultureInfo.InvariantCulture), null, "temporal-profile-set", new { profileVersion=version, request.Enabled, legalAgePolicy = "CalendarAge" });
            }, token).ConfigureAwait(false);
    }

    public async Task<VaultMutationResult> CreateEffectAsync(V4TemporalEffectCreateRequest request, CancellationToken token = default)
    {
        StoryDate period;
        try
        {
            period = Parse(request.Period); ValidateEffectPeriod(period); ValidateRates(request.BiologicalRate, request.ExperiencedRate);
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 255) throw Validation("temporal.name", "name", "Name is required and cannot exceed 255 characters.");
            ValidateOptionalText(request.Notes,V4ContractLimits.MaximumLongTextLength,"notes");
        }
        catch (VaultValidationException e) { return new(false, e.Errors[0].Code, Message: e.Message); }
        var continuity = session.RequireContinuityId();
        V4ResolvedTarget character;
        V4ResolvedTarget? cause = null;
        try
        {
            character = await ResolveCharacterAsync(request.Character, continuity, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(request.CauseWorldEvent))
            {
                cause = await targets.EventAsync(request.CauseWorldEvent, continuity, token).ConfigureAwait(false);
                if (!cause.ResourceType.Equals("WorldEvent", StringComparison.OrdinalIgnoreCase))
                    throw new V4ResolutionException("reference.type_invalid", "The effect cause must be a world event.");
            }
        }
        catch (V4ResolutionException e) { return new(false, e.Code, Message: e.Message); }
        string operation;
        try { operation = references.OperationId(request.MutationToken); }
        catch (ArgumentException e) { return new(false, "validation.mutation_token", Message: e.Message); }
        return await coordinator.ExecuteAsync(operation, "v4.temporal.effect.create", request, "character_temporal_effect_create", session.ClientLabel,
            async (context, ct) =>
            {
                await RequireCharacterAsync(context, character.StorageKey, continuity, ct).ConfigureAwait(false);
                if(cause is not null)await RequireWorldEventAsync(context,cause.StorageKey,continuity,ct).ConfigureAwait(false);
                await EnsureNoOverlapAsync(context, character.StorageKey, period, null, ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                using var insert = context.Command("INSERT INTO [CharacterTemporalEffects] ([ContinuityId],[CharacterId],[Name],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[BiologicalRate],[ExperiencedRate],[WorldEventId],[Notes],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)")
                    .Add(OleDbType.Integer, continuity).Add(OleDbType.Integer, character.StorageKey).Add(OleDbType.VarWChar, request.Name.Trim(), 255);
                AddDate(insert, period); insert.Add(OleDbType.Double, request.BiologicalRate).Add(OleDbType.Double, request.ExperiencedRate)
                    .Add(OleDbType.Integer, cause?.StorageKey).Add(OleDbType.LongVarWChar, request.Notes).Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                var id = await IdentityAsync(context, ct).ConfigureAwait(false);
                using var profile = context.Command("SELECT COUNT(*) FROM [CharacterTemporalProfiles] WHERE [CharacterId]=?").Add(OleDbType.Integer, character.StorageKey);
                if (Convert.ToInt32(await profile.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0)
                {
                    using var enable = context.Command("INSERT INTO [CharacterTemporalProfiles] ([CharacterId],[Enabled],[LegalAgePolicy],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,True,'CalendarAge',?,?)")
                        .Add(OleDbType.Integer, character.StorageKey).Add(OleDbType.Date, now).Add(OleDbType.Date, now);
                    await enable.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    using var enable = context.Command("UPDATE [CharacterTemporalProfiles] SET [Enabled]=True,[IsDeleted]=False,[DeletedAtUtc]=Null,[DeletedOperationId]=Null,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [CharacterId]=?")
                        .Add(OleDbType.Date, now).Add(OleDbType.Integer, character.StorageKey);
                    await enable.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                return new VaultMutationOutcome("CharacterTemporalEffect", id.ToString(CultureInfo.InvariantCulture), 1, "create", new { character = character.Reference, request.Name, request.BiologicalRate, request.ExperiencedRate });
            }, token).ConfigureAwait(false);
    }

    public async Task<VaultMutationResult> UpdateEffectAsync(V4TemporalEffectUpdateRequest request, CancellationToken token = default)
    {
        var allowed=new HashSet<string>(["name","period","biologicalRate","experiencedRate","causeWorldEvent","notes"],StringComparer.OrdinalIgnoreCase);
        try
        {
            V4SparseChangeValidator.Validate(request.Changes,allowed);
            if(request.Changes.TryGetValue("name",out var nameValue)&&nameValue.ValueKind!=JsonValueKind.Null)ValidateRequiredText(nameValue.GetString(),255,"name");
            if(request.Changes.TryGetValue("notes",out var notesValue)&&notesValue.ValueKind!=JsonValueKind.Null)ValidateOptionalText(notesValue.GetString(),V4ContractLimits.MaximumLongTextLength,"notes");
        }
        catch(VaultValidationException e){return new(false,e.Errors[0].Code,Message:e.Message);}
        var continuity=session.RequireContinuityId(); V4ResolvedTarget target;
        try{target=await targets.RelationAsync(request.EffectRef,continuity,false,token).ConfigureAwait(false);if(!target.ResourceType.Equals("CharacterTemporalEffect",StringComparison.OrdinalIgnoreCase))throw new V4ResolutionException("reference.type_invalid","A temporal effect is required.");}
        catch(V4ResolutionException e){return new(false,e.Code,Message:e.Message);}
        var causeSpecified=request.Changes.TryGetValue("causeWorldEvent",out var causeInput);
        V4ResolvedTarget? resolvedCause=null;
        if(causeSpecified&&causeInput.ValueKind!=JsonValueKind.Null)
        {
            try{var causeTarget=await targets.EventAsync(causeInput.GetString()!,continuity,token).ConfigureAwait(false);if(!causeTarget.ResourceType.Equals("WorldEvent",StringComparison.OrdinalIgnoreCase))return new(false,"reference.type_invalid",Message:"The cause must be a world event.");resolvedCause=causeTarget;}
            catch(V4ResolutionException e){return new(false,e.Code,Message:e.Message);}
        }
        string operation;try{operation=references.OperationId(request.MutationToken);}catch(ArgumentException e){return new(false,"validation.mutation_token",Message:e.Message);}
        return await coordinator.ExecuteAsync(operation,"v4.temporal.effect.update",request,"character_temporal_effect_update",session.ClientLabel,async(context,ct)=>
        {
            using var find=context.Command("SELECT [CharacterId],[Name],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[BiologicalRate],[ExperiencedRate],[WorldEventId],[Notes],[Version] FROM [CharacterTemporalEffects] WHERE [Id]=? AND [ContinuityId]=? AND [IsDeleted]=False")
                .Add(OleDbType.Integer,target.StorageKey).Add(OleDbType.Integer,continuity);
            var rows=await find.QueryAsync(r=>new{Character=r.GetInt32(0),Name=r.GetString(1),Period=ReadDate(r,2),Bio=r.GetDouble(9),Exp=r.GetDouble(10),Cause=r.IsDBNull(11)?(int?)null:r.GetInt32(11),Notes=r.IsDBNull(12)?null:r.GetString(12),Version=r.GetInt32(13)},ct).ConfigureAwait(false);
            if(rows.Count!=1)throw new VaultCommandException("record.not_found","The temporal effect is missing or deleted."); var old=rows[0];
            if(old.Version!=request.ExpectedVersion)throw new VaultCommandException("concurrency.conflict",$"The current version is {old.Version}.",actualVersion:old.Version);
            if(resolvedCause is not null)await RequireWorldEventAsync(context,resolvedCause.StorageKey,continuity,ct).ConfigureAwait(false);
            var name=Text(request.Changes,"name",old.Name,false)!; var period=DateChange(request.Changes,"period",old.Period); var bio=Number(request.Changes,"biologicalRate",old.Bio); var exp=Number(request.Changes,"experiencedRate",old.Exp); ValidateRates(bio,exp);
            if (request.Changes.ContainsKey("period")) ValidateEffectPeriod(period);
            var notes=Text(request.Changes,"notes",old.Notes,true); int? cause=causeSpecified?resolvedCause?.StorageKey:old.Cause;
            await EnsureNoOverlapAsync(context,old.Character,period,target.StorageKey,ct).ConfigureAwait(false);
            using var update=context.Command("UPDATE [CharacterTemporalEffects] SET [Name]=?,[PeriodKind]=?,[PeriodLowerBound]=?,[PeriodUpperBound]=?,[PeriodLowerInclusive]=?,[PeriodUpperInclusive]=?,[PeriodOriginalText]=?,[PeriodCalendarId]=?,[BiologicalRate]=?,[ExperiencedRate]=?,[WorldEventId]=?,[Notes]=?,[UpdatedAtUtc]=?,[Version]=[Version]+1 WHERE [Id]=? AND [Version]=?")
                .Add(OleDbType.VarWChar,name,255);AddDate(update,period);update.Add(OleDbType.Double,bio).Add(OleDbType.Double,exp).Add(OleDbType.Integer,cause).Add(OleDbType.LongVarWChar,notes).Add(OleDbType.Date,DateTime.UtcNow).Add(OleDbType.Integer,target.StorageKey).Add(OleDbType.Integer,old.Version);
            if(await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false)!=1)throw new VaultCommandException("concurrency.conflict","The temporal effect changed.");
            return new VaultMutationOutcome("CharacterTemporalEffect",target.StorageKey.ToString(),old.Version+1,"update",new{name,biologicalRate=bio,experiencedRate=exp},old.Version);
        },token).ConfigureAwait(false);
    }

    private async Task<V4CharacterAgeResult> CalculateAsync(V4ResolvedTarget target, CharacterData data,
        DateTimeOffset? instant, string? zone, Effect? hypothetical, CancellationToken token)
    {
        var revision = await reads.RevisionAsync(token).ConfigureAwait(false);
        var character = new V4ReferenceSummary(target.Reference, V4RecordKind.Character, target.Label,
            ContinuityName: session.ContinuityName, Version: data.Version, IsDeleted: data.Deleted);
        if (instant is null || zone is null)
        {
            var unset = new V4AgeMeasure("TimelineUnset", null, null, null, "Story time is unset");
            return new(character, "TimelineUnset", null, null, unset, unset, unset, unset, [], ["Set a session time or continuity clock to calculate age."], revision);
        }
        var tz = FindZone(zone);
        var local = DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant.Value, tz).DateTime, DateTimeKind.Unspecified);
        var calendarAge = StoryAge.Calculate(data.Birth, DateOnly.FromDateTime(local), data.Death);
        var calendar = Measure(calendarAge);
        var effects = data.Effects.Where(e => !e.Deleted).Concat(hypothetical is null ? [] : [hypothetical]).ToArray();
        var warnings = new List<string>();
        V4AgeMeasure biological = calendar, experienced = calendar;
        var applied = new List<V4ReferenceSummary>();
        var unresolvedEffect = data.Tracking && effects.Any(e => e.Period.Kind is StoryDateKind.Range or StoryDateKind.UncertainRange);
        if (unresolvedEffect)
        {
            biological = new("EffectTimingUncertain", null, null, null, "Effect timing needs review");
            experienced = biological;
            warnings.Add("At least one temporal effect has an uncertain or legacy range; biological and experienced age are withheld until its timing is reviewed.");
        }
        if (data.Birth.Kind == StoryDateKind.Unknown)
            warnings.Add("The birth date is unknown; age cannot be calculated.");
        else if (data.Birth.Kind is not (StoryDateKind.ExactDate or StoryDateKind.ExactInstant))
            warnings.Add("The birth date is uncertain; age categories are reported as bounds.");
        if (data.Death.Kind is not (StoryDateKind.Unknown or StoryDateKind.ExactDate or StoryDateKind.ExactInstant))
            warnings.Add("The death date is uncertain; elapsed age is clamped to defensible bounds.");
        if (!data.Tracking)
            warnings.Add("Temporal aging is disabled; biological and experienced age equal calendar age.");
        if (data.Tracking && !unresolvedEffect && calendarAge.Status is not CharacterAgeStatus.BirthDateUnknown and not CharacterAgeStatus.InvalidChronology and not CharacterAgeStatus.NotYetBorn)
        {
            var endMinimum = local;
            var endMaximum = local;
            if (data.Death.Kind != StoryDateKind.Unknown)
            {
                if (data.Death.LowerBound is { } deathLower && deathLower < endMinimum) endMinimum = deathLower;
                if (data.Death.UpperBound is { } deathUpper && deathUpper < endMaximum)
                    endMaximum = data.Death.UpperInclusive ? deathUpper : deathUpper.AddTicks(-1);
            }
            var birthMin = data.Birth.LowerBound!.Value;
            var birthMax = data.Birth.Kind is StoryDateKind.ExactDate or StoryDateKind.ExactInstant
                ? birthMin
                : data.Birth.UpperBound!.Value;
            if (data.Birth.Kind is not (StoryDateKind.ExactDate or StoryDateKind.ExactInstant) && !data.Birth.UpperInclusive)
                birthMax = birthMax.AddTicks(-1);
            var bioBounds = AccumulatedBounds(birthMin, birthMax, endMinimum, endMaximum, effects, e => e.BiologicalRate, warnings);
            var expBounds = AccumulatedBounds(birthMin, birthMax, endMinimum, endMaximum, effects, e => e.ExperiencedRate, warnings);
            biological = DurationMeasure(bioBounds.Min, bioBounds.Max);
            experienced = DurationMeasure(expBounds.Min, expBounds.Max);
            foreach (var effect in effects.Where(e => (e.BiologicalRate != 1d || e.ExperiencedRate != 1d) &&
                         (e.Period.LowerBound is null || e.Period.LowerBound < endMaximum) &&
                         (e.Period.UpperBound is null || e.Period.UpperBound > birthMin)))
                applied.Add(effect.Id < 0
                    ? new("preview:temporal-effect", V4RecordKind.TemporalEffect, effect.Name, "not saved", session.ContinuityName)
                    : EffectSummary(effect, session.ContinuityName));
        }
        return new(character, calendarAge.Status.ToString(), instant.Value.ToString("O", CultureInfo.InvariantCulture), zone,
            calendar, calendar with { Display = "CalendarAge legal policy" }, biological, experienced,
            applied, warnings.Distinct().ToArray(), revision);
    }

    private async Task<V4CharacterAgeResult> CalculateDayAsync(V4ResolvedTarget target,
        CharacterData data, DateOnly date, Effect? hypothetical, CancellationToken token)
    {
        var zone = FindZone(session.CurrentTimeZoneId!);
        var first = FirstInstantOnDate(date, zone);
        var next = FirstInstantOnDate(date.AddDays(1), zone);
        var beginning = await CalculateAsync(target, data, first, zone.Id, hypothetical, token)
            .ConfigureAwait(false);
        var ending = await CalculateAsync(target, data, next.AddTicks(-1), zone.Id, hypothetical, token)
            .ConfigureAwait(false);
        static V4AgeMeasure Range(V4AgeMeasure a, V4AgeMeasure b)
        {
            var minima = new[] { a.MinimumYears, b.MinimumYears, a.ExactYears, b.ExactYears }
                .OfType<double>().ToArray();
            var maxima = new[] { a.MaximumYears, b.MaximumYears, a.ExactYears, b.ExactYears }
                .OfType<double>().ToArray();
            if (minima.Length == 0)
                return a.Status == b.Status ? a : new("DayRange", null, null, null,
                    "The result can change during this day.");
            var minimum = minima.Min();
            var maximum = maxima.Max();
            if (a.Status != b.Status)
            {
                var firstMissing = a.MinimumYears is null && a.MaximumYears is null && a.ExactYears is null;
                var lastMissing = b.MinimumYears is null && b.MaximumYears is null && b.ExactYears is null;
                if (firstMissing || lastMissing) minimum = 0;
                return new("DayRange", null, minimum, maximum,
                    $"{minimum:0.##}–{maximum:0.##} years during this day");
            }
            return Math.Abs(maximum - minimum) < 1e-9
                ? a with { ExactYears = minimum, MinimumYears = minimum, MaximumYears = maximum }
                : new("Bounded", null, minimum, maximum,
                    $"{minimum:0.##}–{maximum:0.##} years");
        }
        return beginning with
        {
            Status = beginning.Status == ending.Status ? beginning.Status : "DayRange",
            AsOf = null,
            AsOfDate = date,
            Calendar = Range(beginning.Calendar, ending.Calendar),
            Legal = Range(beginning.Legal, ending.Legal),
            Biological = Range(beginning.Biological, ending.Biological),
            Experienced = Range(beginning.Experienced, ending.Experienced),
            AppliedEffects = beginning.AppliedEffects.Concat(ending.AppliedEffects)
                .DistinctBy(item => item.Ref).ToArray(),
            Warnings = beginning.Warnings.Concat(ending.Warnings).Distinct().ToArray()
        };
    }

    private static DateTimeOffset FirstInstantOnDate(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        while (zone.IsInvalidTime(local) && local.Date == date.ToDateTime(TimeOnly.MinValue))
            local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    private static (double Min, double Max) AccumulatedBounds(DateTime birthMin, DateTime birthMax, DateTime endMin, DateTime endMax,
        IReadOnlyList<Effect> effects, Func<Effect, double> rate, ICollection<string> warnings)
    {
        var minima = new List<double>();
        var maxima = new List<double>();
        foreach (var birth in new[] { birthMin, birthMax })
        foreach (var end in new[] { endMin, endMax })
        {
            var total = Math.Max(0, (end - birth).TotalDays);
            var minimum = total;
            var maximum = total;
            foreach (var effect in effects)
            {
                var start = effect.Period.LowerBound ?? birth;
                var finish = effect.Period.UpperBound ?? end;
                var overlap = Math.Max(0, (Min(end, finish) - Max(birth, start)).TotalDays);
                var contribution = (rate(effect) - 1d) * overlap;
                if (effect.Period.Kind is StoryDateKind.Circa or StoryDateKind.Month or StoryDateKind.Year or StoryDateKind.Before or StoryDateKind.After)
                {
                    minimum += Math.Min(0, contribution);
                    maximum += Math.Max(0, contribution);
                    warnings.Add($"Effect '{effect.Name}' has an uncertain period; its contribution is reported as a bound.");
                }
                else { minimum += contribution; maximum += contribution; }
                if (effect.Period.LowerBound is null || effect.Period.UpperBound is null)
                    warnings.Add($"Effect '{effect.Name}' has an open boundary; it was clamped to birth and the calculation time.");
            }
            minima.Add(Math.Max(0, minimum));
            maxima.Add(Math.Max(0, maximum));
        }
        return (minima.Min(), maxima.Max());
    }

    private async Task<CharacterData> LoadAsync(int id, CancellationToken token)
    {
        await using var connection = factory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var character = new AccessCommand(connection, "SELECT IIf(ch.[PreferredName] Is Null,ch.[GivenName],ch.[PreferredName]),c.[Version],c.[IsDeleted],ch.[BirthKind],ch.[BirthLowerBound],ch.[BirthUpperBound],ch.[BirthLowerInclusive],ch.[BirthUpperInclusive],ch.[BirthOriginalText],ch.[BirthCalendarId],ch.[DeathKind],ch.[DeathLowerBound],ch.[DeathUpperBound],ch.[DeathLowerInclusive],ch.[DeathUpperInclusive],ch.[DeathOriginalText],ch.[DeathCalendarId] FROM [Characters] AS ch INNER JOIN [CanonEntities] AS c ON ch.[EntityId]=c.[Id] WHERE ch.[EntityId]=?")
            .Add(OleDbType.Integer, id);
        var chars = await character.QueryAsync(r => new { Name = r.GetString(0), Version = r.GetInt32(1), Deleted = r.GetBoolean(2), Birth = ReadDate(r, 3), Death = ReadDate(r, 10) }, token).ConfigureAwait(false);
        if (chars.Count != 1) throw new V4ResolutionException("record.not_found", "The character was not found.");
        using var profile = new AccessCommand(connection, "SELECT [Enabled],[LegalAgePolicy] FROM [CharacterTemporalProfiles] WHERE [CharacterId]=? AND [IsDeleted]=False").Add(OleDbType.Integer, id);
        var profiles = await profile.QueryAsync(r => (Enabled: r.GetBoolean(0), Policy: r.GetString(1)), token).ConfigureAwait(false);
        using var effect = new AccessCommand(connection, "SELECT [Id],[Name],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId],[BiologicalRate],[ExperiencedRate],[Version],[IsDeleted] FROM [CharacterTemporalEffects] WHERE [CharacterId]=? ORDER BY [Id]").Add(OleDbType.Integer, id);
        var effects = await effect.QueryAsync(r => new Effect(r.GetInt32(0), r.GetString(1), ReadDate(r, 2), r.GetDouble(9), r.GetDouble(10), r.GetInt32(11), r.GetBoolean(12)), token).ConfigureAwait(false);
        var c = chars[0]; return new(id, c.Name, c.Version, c.Deleted, c.Birth, c.Death,
            profiles.Count == 1 && profiles[0].Enabled, profiles.Count == 1 ? profiles[0].Policy : "CalendarAge", effects);
    }

    private async Task<(DateTimeOffset? Instant, string? Zone)> ResolveAsOfAsync(int continuity, V4AsOfInput? at, CancellationToken token)
    {
        if (at is not null) return (at.CurrentTime, FindZone(at.ReferenceTimeZoneId).Id);
        if (session.CurrentTimeOverride is { } current) return (current, FindZone(session.CurrentTimeZoneId!).Id);
        await using var connection = factory.Create(); await connection.OpenAsync(token).ConfigureAwait(false);
        using var command = new AccessCommand(connection, "SELECT k.[CurrentInstantUtc],IIf(k.[ReferenceTimeZoneId] Is Null,c.[DefaultTimeZoneId],k.[ReferenceTimeZoneId]) FROM [ContinuityClocks] AS k INNER JOIN [Continuities] AS c ON k.[ContinuityId]=c.[Id] WHERE k.[ContinuityId]=?").Add(OleDbType.Integer, continuity);
        var rows = await command.QueryAsync(r => (Instant: r.IsDBNull(0) ? (DateTime?)null : r.GetDateTime(0), Zone: r.GetString(1)), token).ConfigureAwait(false);
        return rows.Count == 0 || rows[0].Instant is null ? (null, rows.Count == 0 ? null : rows[0].Zone) :
            (new DateTimeOffset(DateTime.SpecifyKind(rows[0].Instant.GetValueOrDefault(), DateTimeKind.Utc)), rows[0].Zone);
    }

    private async Task<V4ResolvedTarget> ResolveCharacterAsync(string value, int continuity, CancellationToken token)
    {
        var target = await targets.EntityAsync(value, continuity, token).ConfigureAwait(false);
        if (!target.ResourceType.Equals("Character", StringComparison.OrdinalIgnoreCase)) throw new V4ResolutionException("reference.type_invalid", "A character is required.");
        return target;
    }

    private static async Task RequireCharacterAsync(VaultWriteContext context, int id, int continuity, CancellationToken token)
    {
        using var command = context.Command("SELECT COUNT(*) FROM [CanonEntities] WHERE [Id]=? AND [ContinuityId]=? AND [EntityType]='Character' AND [IsDeleted]=False")
            .Add(OleDbType.Integer, id).Add(OleDbType.Integer, continuity);
        if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) != 1) throw new VaultCommandException("record.not_found", "The character is missing or deleted.");
    }

    private static async Task RequireWorldEventAsync(VaultWriteContext context,int id,int continuity,CancellationToken token)
    {
        using var command=context.Command("SELECT COUNT(*) FROM [CanonEntities] WHERE [Id]=? AND [ContinuityId]=? AND [EntityType]='WorldEvent' AND [IsDeleted]=False")
            .Add(OleDbType.Integer,id).Add(OleDbType.Integer,continuity);
        if(Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false))!=1)
            throw new VaultCommandException("record.not_found","The causal world event is missing or deleted.");
    }

    private static async Task EnsureNoOverlapAsync(VaultWriteContext context, int character, StoryDate period, int? exclude, CancellationToken token)
    {
        using var command = context.Command("SELECT [Id],[PeriodKind],[PeriodLowerBound],[PeriodUpperBound],[PeriodLowerInclusive],[PeriodUpperInclusive],[PeriodOriginalText],[PeriodCalendarId] FROM [CharacterTemporalEffects] WHERE [CharacterId]=? AND [IsDeleted]=False")
            .Add(OleDbType.Integer, character);
        foreach (var row in await command.QueryAsync(r => (Id: r.GetInt32(0), Date: ReadDate(r, 1)), token).ConfigureAwait(false))
            if (row.Id != exclude && row.Date.Overlaps(period)) throw new VaultCommandException("temporal.effect_overlap", "Temporal effects for one character cannot overlap.");
    }

    private V4ReferenceSummary EffectSummary(Effect effect, string? continuityName) =>
        new(references.ReferenceFromKnownRecord("CharacterTemporalEffect", effect.Id, effect.Name),
            V4RecordKind.TemporalEffect, effect.Name, ContinuityName: continuityName,
            Version: effect.Version, IsDeleted: effect.Deleted);
    private static V4AgeMeasure Measure(StoryAge value) => new(value.Status.ToString(), value.ExactYears, value.MinimumYears, value.MaximumYears,
        value.ExactYears is { } exact ? $"{exact} years" : value.MinimumYears is { } min && value.MaximumYears is { } max ? $"{min}–{max} years" : null);
    private static V4AgeMeasure DurationMeasure(double minimumDays, double maximumDays)
    {
        var min = minimumDays / 365.2425d; var max = maximumDays / 365.2425d;
        return Math.Abs(max - min) < 1e-9 ? new("Exact", min, min, max, $"{min:0.##} years") : new("Bounded", null, min, max, $"{min:0.##}–{max:0.##} years");
    }
    private static DateTime Min(DateTime a, DateTime b) => a <= b ? a : b;
    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
    private static TimeZoneInfo FindZone(string zone)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(zone); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw Validation("timezone.invalid", "referenceTimeZoneId", "The timezone is not available on this host."); }
    }
    private static void ValidateEffectPeriod(StoryDate period)
    {
        if (period.Kind is StoryDateKind.Range or StoryDateKind.UncertainRange)
            throw Validation("temporal.effect_period_uncertain", "period",
                "A temporal aging effect needs a known active interval. Use KnownRange, or review the legacy range before calculating age.");
    }

    private static void ValidateRates(double biological, double experienced)
    {
        if (!double.IsFinite(biological) || biological < 0) throw Validation("temporal.rate", "biologicalRate", "Biological rate must be finite and non-negative.");
        if (!double.IsFinite(experienced) || experienced < 0) throw Validation("temporal.rate", "experiencedRate", "Experienced rate must be finite and non-negative.");
    }
    private static void ValidateOptionalText(string? value,int maximum,string field){if(value?.Length>maximum)throw Validation("validation.text_too_long",field,$"{field} cannot exceed {maximum} characters.");}
    private static void ValidateRequiredText(string? value,int maximum,string field){if(string.IsNullOrWhiteSpace(value)||value.Length>maximum)throw Validation("validation.text",field,$"{field} is required and cannot exceed {maximum} characters.");}
    private static string? Text(IReadOnlyDictionary<string,JsonElement> changes,string key,string? fallback,bool nullable)
    {if(!changes.TryGetValue(key,out var value))return fallback;if(value.ValueKind==JsonValueKind.Null){if(!nullable)throw Validation("changes.null",key,$"{key} cannot be null.");return null;}var result=value.GetString()?.Trim();if(string.IsNullOrWhiteSpace(result)&&!nullable)throw Validation("changes.value",key,$"{key} is required.");return result;}
    private static double Number(IReadOnlyDictionary<string,JsonElement> changes,string key,double fallback)=>changes.TryGetValue(key,out var value)?value.GetDouble():fallback;
    private static StoryDate DateChange(IReadOnlyDictionary<string,JsonElement> changes,string key,StoryDate fallback)=>changes.TryGetValue(key,out var value)?Parse(value.Deserialize<V4StoryDateInput>()!):fallback;
    private static StoryDate Parse(V4StoryDateInput input) => V4StoryDateParser.Parse(new(
        Kind: input.Kind is null ? null : Enum.Parse<StoryDateKind>(input.Kind.ToString()!),
        Value: input.Value, Lower: input.Lower, Upper: input.Upper,
        LowerInclusive: input.LowerInclusive, UpperInclusive: input.UpperInclusive,
        OriginalText: input.OriginalText, CalendarId: input.CalendarId ?? "Gregorian"));
    private static StoryDate ReadDate(DbDataReader r, int o) => new(Enum.Parse<StoryDateKind>(r.GetString(o)), r.IsDBNull(o+1)?null:DateTime.SpecifyKind(r.GetDateTime(o+1),DateTimeKind.Unspecified), r.IsDBNull(o+2)?null:DateTime.SpecifyKind(r.GetDateTime(o+2),DateTimeKind.Unspecified), r.GetBoolean(o+3), r.GetBoolean(o+4), r.IsDBNull(o+5)?null:r.GetString(o+5), r.GetString(o+6));
    private static void AddDate(AccessCommand c, StoryDate d) => c.Add(OleDbType.VarWChar,d.Kind.ToString(),30).Add(OleDbType.Date,d.LowerBound).Add(OleDbType.Date,d.UpperBound).Add(OleDbType.Boolean,d.LowerInclusive).Add(OleDbType.Boolean,d.UpperInclusive).Add(OleDbType.LongVarWChar,d.OriginalText).Add(OleDbType.VarWChar,d.CalendarId,50);
    private static async Task<int> IdentityAsync(VaultWriteContext c, CancellationToken token) { using var q=c.Command("SELECT @@IDENTITY"); return Convert.ToInt32(await q.ExecuteScalarAsync(token).ConfigureAwait(false)); }
    private static VaultValidationException Validation(string code,string field,string message)=>new([new(code,field,message)]);
}
