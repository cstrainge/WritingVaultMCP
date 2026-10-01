using System.Data.OleDb;
using System.Security.Cryptography;
using System.Text;
using WritingVaultMcp.Infrastructure.Access;
using WritingVaultMcp.Mcp.V4;
using WritingVault.Client;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase9SourceSnapshotTests
{
    [Fact]
    public async Task DeletedSnapshotTextRemainsAvailableOnlyWhenRequestedExplicitly()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Deleted snapshot reading", "UTC"))).ResourceKey!);
        var source = int.Parse((await vault.Service.CreateSourceAsync(
            new(Guid.NewGuid().ToString(), "Old source"))).ResourceKey!);
        var snapshot = int.Parse((await vault.Service.AddSourceSnapshotAsync(
            new(Guid.NewGuid().ToString(), source, "An archived claim remains readable."))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Deleted snapshot reading");
        var reference = await references.ReferenceAsync("SourceSnapshot", snapshot);
        var records = new AccessV4RecordService(vault.Coordinator, references, session, vault.Service);
        Assert.True((await records.LifecycleAsync(new(Guid.NewGuid().ToString(), reference, 1), false)).Success);
        var service = new AccessV4SourceSnapshotService(vault.Factory, references, session,
            vault.Cursors, new(vault.StorageRoot), reads);
        var blocked = await Assert.ThrowsAsync<WritingVaultMcp.Application.V4.V4ResolutionException>(() =>
            service.ViewAsync(new(reference)));
        Assert.Equal("record.deleted", blocked.Code);
        Assert.Equal("An archived claim remains readable.", (await service.ViewAsync(new(reference, IncludeDeleted: true))).Text);
        var overview = await reads.GetAsync(new(reference, IncludeDeleted: true));
        Assert.True(overview.Summary.IsDeleted);
        Assert.DoesNotContain(overview.Fields.Keys, key => key.EndsWith("Path", StringComparison.OrdinalIgnoreCase));

        var factory = new McpVaultReadClientFactory(new(TestServer.AssemblyPath, vault.DatabasePath, vault.StorageRoot));
        await using var client = await factory.ConnectAsync("Deleted snapshot reader");
        await client.SetSessionAsync(new("Deleted snapshot reading"));
        Assert.Equal("An archived claim remains readable.",
            (await client.SourceSnapshotViewAsync(reference, includeDeleted: true)).Text);
    }

    [Fact]
    public void ObservedRevisionIsStableAcrossIndependentReadsOfTheSamePosition()
    {
        var cursors=new V4CursorCodec();
        var first=cursors.EncodeRevision("changes",42,"c:7");
        var second=cursors.EncodeRevision("changes",42,"c:7");
        Assert.Equal(first,second);
        Assert.NotEqual(first,cursors.EncodeRevision("changes",43,"c:7"));
        Assert.Equal(42,cursors.Decode(first,"changes","c:7").Position);
    }

    [Fact]
    public async Task CachedTextPagesSafelyAcrossUnicodeAndRejectsCursorReuse()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Snapshot Reading", "UTC"))).ResourceKey!);
        var source = int.Parse((await vault.Service.CreateSourceAsync(
            new(Guid.NewGuid().ToString(), "Reference Source"))).ResourceKey!);
        var content = "A😀B" + new string('x', 20_000);
        var firstId = int.Parse((await vault.Service.AddSourceSnapshotAsync(
            new(Guid.NewGuid().ToString(), source, content, "text/plain"))).ResourceKey!);
        var secondId = int.Parse((await vault.Service.AddSourceSnapshotAsync(
            new(Guid.NewGuid().ToString(), source, "another snapshot", "text/plain"))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Snapshot Reading");
        var service = new AccessV4SourceSnapshotService(vault.Factory, references, session,
            vault.Cursors, new(vault.StorageRoot), reads);
        var firstRef = await references.ReferenceAsync("SourceSnapshot", firstId);
        var secondRef = await references.ReferenceAsync("SourceSnapshot", secondId);

        var first = await service.ViewAsync(new(firstRef, Limit: 2));
        Assert.Equal("A", first.Text);
        Assert.True(first.HasMore);
        Assert.DoesNotContain("source-cache", first.ToString(), StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAsync<V4CursorException>(() =>
            service.ViewAsync(new(secondRef, first.NextCursor, 2)));

        var text = new StringBuilder(first.Text);
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await service.ViewAsync(new(firstRef, cursor, 8_192));
            text.Append(page.Text);
            cursor = page.NextCursor;
        }
        Assert.Equal(content, text.ToString());
        Assert.Equal(Encoding.UTF8.GetByteCount(content), first.ByteSize);
        var factory=new McpVaultReadClientFactory(new(TestServer.AssemblyPath,vault.DatabasePath,vault.StorageRoot));
        await using var client=await factory.ConnectAsync("Phase 9 source reader");
        await client.SetSessionAsync(new("Snapshot Reading"));
        var typed=await client.SourceSnapshotViewAsync(firstRef,limit:1024);
        Assert.StartsWith(first.Text,typed.Text,StringComparison.Ordinal);
        Assert.True(typed.HasMore);
    }

    [Fact]
    public async Task CachedTextRejectsPathSubstitutionAndHashMismatch()
    {
        await using var vault = await TestVault.CreateAsync();
        var continuity = int.Parse((await vault.Service.CreateContinuityAsync(
            new(Guid.NewGuid().ToString(), "Snapshot Safety", "UTC"))).ResourceKey!);
        var source = int.Parse((await vault.Service.CreateSourceAsync(
            new(Guid.NewGuid().ToString(), "Reference Source"))).ResourceKey!);
        const string content = "cached important fact";
        var snapshot = int.Parse((await vault.Service.AddSourceSnapshotAsync(
            new(Guid.NewGuid().ToString(), source, content))).ResourceKey!);
        var (reads, session, references) = vault.V4();
        session.SelectContinuity(continuity, "Snapshot Safety");
        var service = new AccessV4SourceSnapshotService(vault.Factory, references, session,
            vault.Cursors, new(vault.StorageRoot), reads);
        var reference = await references.ReferenceAsync("SourceSnapshot", snapshot);

        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            using var command = new AccessCommand(connection,
                "UPDATE [SourceSnapshots] SET [RelativeCachePath]=? WHERE [Id]=?")
                .Add(OleDbType.VarWChar, "..\\unrelated.bin", 255)
                .Add(OleDbType.Integer, snapshot);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var invalid = await Assert.ThrowsAsync<WritingVaultMcp.Application.V4.V4ResolutionException>(() =>
            service.ViewAsync(new(reference)));
        Assert.Equal("storage.snapshot_path", invalid.Code);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var relative = Path.Combine("source-cache", hash[..2], hash + ".bin");
        await using (var connection = vault.Factory.Create())
        {
            await connection.OpenAsync();
            using var command = new AccessCommand(connection,
                "UPDATE [SourceSnapshots] SET [RelativeCachePath]=? WHERE [Id]=?")
                .Add(OleDbType.VarWChar, relative, 255)
                .Add(OleDbType.Integer, snapshot);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        await File.WriteAllTextAsync(Path.Combine(vault.StorageRoot, relative),
            new string('z', Encoding.UTF8.GetByteCount(content)));
        var corrupt = await Assert.ThrowsAsync<WritingVaultMcp.Application.V4.V4ResolutionException>(() =>
            service.ViewAsync(new(reference)));
        Assert.Equal("storage.snapshot_hash", corrupt.Code);
    }
}
