using System.Data.OleDb;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;

namespace WritingVaultMcp.Tests;

public sealed class Phase6InfrastructureTests
{
    [Fact]
    public async Task EveryCharacterCoreFieldRoundTripsAndPatchesThroughTheActiveAccessContract()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var created = await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Character, "Ada",
            Birth: StoryDate.Year(1988),
            MiddleNames: "Robin", FamilyName: "North", PreferredName: "Ari",
            Gender: "nonbinary", Pronouns: "they/them", Species: "Human",
            Occupation: "Cartographer", Nationality: "Lostvillian",
            PhysicalDescription: "Tall", PersonalitySummary: "Careful"));
        Assert.True(created.Success, created.Message);
        var id = int.Parse(created.ResourceKey!);

        var details = (await vault.Service.GetEntityAsync(id))!;
        Assert.Equal("Robin", details.Fields["MiddleNames"]);
        Assert.Equal("North", details.Fields["FamilyName"]);
        Assert.Equal("Ari", details.Fields["PreferredName"]);
        Assert.Equal("nonbinary", details.Fields["Gender"]);
        Assert.Equal("they/them", details.Fields["Pronouns"]);
        Assert.Equal("Human", details.Fields["Species"]);
        Assert.Equal("Cartographer", details.Fields["Occupation"]);
        Assert.Equal("Lostvillian", details.Fields["Nationality"]);
        Assert.Equal("Tall", details.Fields["PhysicalDescription"]);
        Assert.Equal("Careful", details.Fields["PersonalitySummary"]);

        var longDescription = new string('x', 70_000);
        var patched = await vault.Service.PatchEntityAsync(new(
            Guid.NewGuid().ToString(), id, 1,
            MiddleNames: new(true, null), PreferredName: new(true, "Ada"),
            PhysicalDescription: new(true, longDescription), PersonalitySummary: new(true, "Decisive")));
        Assert.True(patched.Success, patched.Message);
        details = (await vault.Service.GetEntityAsync(id))!;
        Assert.Null(details.Fields["MiddleNames"]);
        Assert.Equal("Ada", details.Fields["PreferredName"]);
        Assert.Equal(65_536, Assert.IsType<string>(details.Fields["PhysicalDescription"]).Length);
        Assert.Equal(true, details.Fields["PhysicalDescriptionTruncated"]);
        Assert.Equal("Decisive", details.Fields["PersonalitySummary"]);
        Assert.Equal(2, details.Summary.Version);
    }

    [Fact]
    public async Task GraphExpansionAndLongTextHaveHardReadBounds()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(
            Guid.NewGuid().ToString(), "Canon", "UTC"))).ResourceKey!);
        var entity = int.Parse((await vault.Service.CreateEntityAsync(new(
            Guid.NewGuid().ToString(), continuity, CanonEntityType.Object, "Artifact"))).ResourceKey!);
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            for (var index = 0; index < 55; index++)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO [EntityNotes] ([EntityId],[Title],[Body],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?,?)";
                insert.Parameters.Add("p0", OleDbType.Integer).Value = entity;
                insert.Parameters.Add("p1", OleDbType.VarWChar, 255).Value = $"Note {index:D2}";
                insert.Parameters.Add("p2", OleDbType.LongVarWChar).Value = index == 0 ? new string('z', 70_000) : "body";
                insert.Parameters.Add("p3", OleDbType.Date).Value = DateTime.UtcNow;
                insert.Parameters.Add("p4", OleDbType.Date).Value = DateTime.UtcNow;
                await insert.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }

        var graph = (await vault.Service.GetEntityGraphAsync(entity, relationLimit: 200))!;
        Assert.Equal(50, graph.Notes.Count);
        Assert.True(graph.Truncated);
        var first = graph.Notes[0];
        Assert.Equal(65_536, Assert.IsType<string>(first["Body"]).Length);
        Assert.Equal(true, first["BodyTruncated"]);
    }

    [Fact]
    public async Task SourceSnapshotUpdatesDerivedRetrievalStateAtomically()
    {
        await using var vault = await TestVault.CreateAsync();
        var source = int.Parse((await vault.Service.CreateSourceAsync(new(
            Guid.NewGuid().ToString(), "Archive"))).ResourceKey!);
        var retrievedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var snapshot = await vault.Service.AddSourceSnapshotAsync(new(
            Guid.NewGuid().ToString(), source, "cached claim", RetrievedAtUtc: retrievedAt));
        Assert.True(snapshot.Success, snapshot.Message);

        await using var connection = vault.Factory.Create();
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT [LastRetrievedAtUtc],[RetrievalStatus] FROM [Sources] WHERE [Id]=?";
        command.Parameters.Add("p0", OleDbType.Integer).Value = source;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(retrievedAt, DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc));
        Assert.Equal("Cached", reader.GetString(1));
    }

    [Fact]
    public void ClientReadErrorsDoNotExposeProviderDetails()
    {
        var exception = AccessErrorClassifier.ToClientReadException(
            new IOException(@"C:\private\WritingVault.accdb failed while running SELECT * FROM Secrets"),
            "entity search");

        Assert.IsType<VaultReadException>(exception);
        Assert.Equal("The vault could not complete entity search.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("WritingVault.accdb", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SELECT", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
