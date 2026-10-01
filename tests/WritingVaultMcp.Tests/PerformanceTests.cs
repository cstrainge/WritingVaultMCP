using System.Data.OleDb;
using System.Diagnostics;
using SkiaSharp;
using WritingVaultMcp.Application;
using WritingVaultMcp.Application.V4;
using WritingVaultMcp.Domain;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Infrastructure.Access.Backup;
using WritingVaultMcp.Mcp.V4;
using Xunit.Abstractions;

namespace WritingVaultMcp.Tests;

public sealed class PerformanceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Performance")]
    public async Task RepresentativeVaultMeetsDocumentedLocalLatencyCeilings()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(), "Volume", "UTC"))).ResourceKey!);
        const int entityCount = 1_000;
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            using var transaction = connection.BeginTransaction();
            for (var index = 0; index < entityCount; index++)
            {
                using var canon = connection.CreateCommand();
                canon.Transaction = transaction;
                canon.CommandText = "INSERT INTO [CanonEntities] ([ContinuityId],[EntityType],[CreatedAtUtc],[UpdatedAtUtc]) VALUES (?,?,?,?)";
                canon.Parameters.Add("p0", OleDbType.Integer).Value = continuity;
                canon.Parameters.Add("p1", OleDbType.VarWChar, 30).Value = "Object";
                canon.Parameters.Add("p2", OleDbType.Date).Value = DateTime.UtcNow;
                canon.Parameters.Add("p3", OleDbType.Date).Value = DateTime.UtcNow;
                await canon.ExecuteNonQueryAsync();
                using var identity = connection.CreateCommand(); identity.Transaction = transaction; identity.CommandText = "SELECT @@IDENTITY";
                var id = Convert.ToInt32(await identity.ExecuteScalarAsync());
                using var subtype = connection.CreateCommand();
                subtype.Transaction = transaction;
                subtype.CommandText = "INSERT INTO [Objects] ([EntityId],[Name],[ObjectType],[Description]) VALUES (?,?,?,?)";
                subtype.Parameters.Add("p0", OleDbType.Integer).Value = id;
                subtype.Parameters.Add("p1", OleDbType.VarWChar, 255).Value = $"Artifact {index:D4}";
                subtype.Parameters.Add("p2", OleDbType.VarWChar, 100).Value = "Synthetic";
                subtype.Parameters.Add("p3", OleDbType.LongVarWChar).Value = $"Representative synthetic record {index}.";
                await subtype.ExecuteNonQueryAsync();
            }
            transaction.Commit();
        }

        var timer = Stopwatch.StartNew();
        var page = await vault.Service.SearchEntitiesAsync(CanonEntityType.Object, continuity, "Artifact", limit: 100);
        timer.Stop();
        Assert.Equal(100, page.Items.Count);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"Search took {timer.Elapsed}.");

        timer.Restart();
        var details = await vault.Service.GetEntityAsync(page.Items[50].Id);
        timer.Stop();
        Assert.NotNull(details);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(2), $"Detail read took {timer.Elapsed}.");

        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Volume");
        timer.Restart();
        var v4Page = await reads.SearchAsync(new(
            Text: "Artifact", Kinds: [V4RecordKind.Object], Limit: 100));
        timer.Stop();
        Assert.Equal(100, v4Page.Items.Count);
        output.WriteLine($"v4 search, 1,000 objects, first 100: {timer.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"v4 search took {timer.Elapsed}.");

        timer.Restart();
        var v4Record = await reads.GetAsync(new(v4Page.Items[50].Ref));
        timer.Stop();
        Assert.NotNull(v4Record);
        output.WriteLine($"v4 record page, 1,000-object continuity: {timer.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"v4 record page took {timer.Elapsed}.");

        using var bitmap = new SKBitmap(800, 600);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(new SKColor(75, 95, 115));
        using var encodedImage = SKImage.FromBitmap(bitmap);
        using var png = encodedImage.Encode(SKEncodedImageFormat.Png, 100);
        var images = new AccessV4ImageService(vault.Factory, vault.Coordinator, references,
            new V4TargetResolver(new AccessV4SemanticResolver(vault.Factory, vault.Coordinator, references)),
            session, reads, vault.Cursors, vault.StorageRoot, vault.DatabasePath);
        var attached = await images.AttachAsync(new(Guid.NewGuid().ToString(), "Artifact 0500",
            new("image/png", Convert.ToBase64String(png.ToArray())), Title: "Scale portrait"));
        Assert.True(attached.Success, attached.Message);
        var image = Assert.Single((await images.ListAsync(new("Artifact 0500"))).Items);
        timer.Restart();
        var viewedImage = await images.ViewAsync(new(image.Ref));
        timer.Stop();
        Assert.NotEmpty(viewedImage.Content);
        output.WriteLine($"v4 thumbnail view, 800x600 original: {timer.Elapsed.TotalMilliseconds:F0} ms");
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"v4 image view took {timer.Elapsed}.");

        timer.Restart();
        Assert.True((await vault.Integrity.VerifyAsync()).IsValid);
        timer.Stop();
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(10), $"Integrity verification took {timer.Elapsed}.");

        timer.Restart();
        var backup = await new AccessBackupService().CreateAsync(vault.DatabasePath, vault.StorageRoot);
        timer.Stop();
        Assert.True(File.Exists(backup.BackupPath));
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15), $"Backup took {timer.Elapsed}.");
    }
}
