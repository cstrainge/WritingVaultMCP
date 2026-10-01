using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using WritingVault.Client;
using WritingVault.Web;
using WritingVaultMcp.Application;
using WritingVaultMcp.Domain;

namespace WritingVaultMcp.Tests;

public sealed class V4Phase8WebTests
{
    [Fact]
    public async Task Phase8PreviewFixtureUsesOnlyDisposableData()
    {
        await using var vault=await TestVault.CreateAsync();
        var lostville=int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(),"Lostville Preview","America/Vancouver","A small town where impossible things leave very ordinary paperwork."))).ResourceKey!);
        var aurora=int.Parse((await vault.Service.CreateContinuityAsync(new(Guid.NewGuid().ToString(),"Aurora Preview","UTC","A distant science-fiction continuity used to prove session isolation."))).ResourceKey!);
        foreach(var (type,name) in new[]{
            (CanonEntityType.Character,"Chloë Bell"),(CanonEntityType.Character,"Mara Vale"),
            (CanonEntityType.Location,"The Lantern District"),(CanonEntityType.Project,"The Long September"),
            (CanonEntityType.Organization,"Lostville Historical Society"),(CanonEntityType.Object,"The Brass Key"),
            (CanonEntityType.WorldEvent,"The Night the Clocks Paused")})
            Assert.True((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),lostville,type,name))).Success);
        Assert.True((await vault.Service.CreateEntityAsync(new(Guid.NewGuid().ToString(),aurora,CanonEntityType.Character,"Commander Ilyan"))).Success);
        var output=Path.Combine(RepositoryRoot(),"artifacts","phase8-preview");Directory.CreateDirectory(output);
        File.Copy(vault.DatabasePath,Path.Combine(output,"WritingVault.Phase8Preview.accdb"),true);
        Directory.CreateDirectory(Path.Combine(output,"backup"));
    }

    [Fact]
    public void WebDependencyBoundaryHasNoDatabaseOrServerReference()
    {
        var root=RepositoryRoot();
        var project=XDocument.Load(Path.Combine(root,"WritingVault.Web","WritingVault.Web.csproj"));
        var references=project.Descendants("ProjectReference").Select(node=>node.Attribute("Include")?.Value).ToArray();
        Assert.Single(references);
        Assert.Equal("..\\WritingVault.Client\\WritingVault.Client.csproj",references[0]);
        var webSource=string.Join('\n',Directory.GetFiles(Path.Combine(root,"WritingVault.Web"),"*.cs",SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("System.Data.OleDb",webSource,StringComparison.Ordinal);
        Assert.DoesNotContain("Infrastructure.Access",webSource,StringComparison.Ordinal);
        Assert.DoesNotContain("VaultWrite",webSource,StringComparison.Ordinal);
        var clientSource=string.Join('\n',Directory.GetFiles(Path.Combine(root,"WritingVault.Client"),"*.cs",SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.Contains("--read-only",clientSource,StringComparison.Ordinal);
        Assert.Contains("--tool-surface",clientSource,StringComparison.Ordinal);
        Assert.DoesNotContain("System.Data.OleDb",clientSource,StringComparison.Ordinal);
    }

    [Fact]
    public void ViewerBindingIsLiteralLoopbackAndRejectsOverrides()
    {
        var root=RepositoryRoot();
        var server=TestServer.AssemblyPath;
        var database=Path.Combine(root,"WritingVault.accdb");
        Assert.Throws<ArgumentException>(()=>ViewerOptions.Parse([
            "--server",server,"--database",database,"--backup-root",root,"--listen","http://0.0.0.0:5284"]));
        Assert.Throws<ArgumentException>(()=>ViewerOptions.Parse([
            "--server",server,"--database",database,"--backup-root",root,"--listen","http://127.0.0.1:5286"]));
        Assert.Equal(ViewerOptions.Address,$"http://127.0.0.1:{ViewerOptions.Port}");
#if DEBUG
        Assert.Equal(5285,ViewerOptions.Port);
#else
        Assert.Equal(5284,ViewerOptions.Port);
#endif
    }

    [Fact]
    public void BrowserAssetsUseSafeDomAndHaveNoExternalFetchSurface()
    {
        var root=RepositoryRoot();var web=Path.Combine(root,"WritingVault.Web");
        var html=File.ReadAllText(Path.Combine(web,"wwwroot","index.html"));
        var js=string.Join('\n',Directory.GetFiles(Path.Combine(web,"wwwroot"),"*.js")
            .OrderBy(path=>path,StringComparer.Ordinal).Select(File.ReadAllText));
        var css=File.ReadAllText(Path.Combine(web,"wwwroot","app.css"));
        Assert.DoesNotContain("http://",html,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://",html,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("innerHTML",js,StringComparison.Ordinal);
        Assert.DoesNotContain("eval(",js,StringComparison.Ordinal);
        Assert.Contains("/markdown.js",html,StringComparison.Ordinal);
        Assert.Contains("textContent",js,StringComparison.Ordinal);
        Assert.Contains("BroadcastChannel",js,StringComparison.Ordinal);
        Assert.Contains("requestedCursor !== state.cursor",js,StringComparison.Ordinal);
        Assert.Contains("currentLocalTime",js,StringComparison.Ordinal);
        Assert.Contains("[hidden]",css,StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion",css,StringComparison.Ordinal);
        Assert.Contains(":focus-visible",css,StringComparison.Ordinal);
        var host=File.ReadAllText(Path.Combine(web,"Program.cs"));
        Assert.Contains("default-src 'self'",host,StringComparison.Ordinal);
        Assert.Contains("img-src 'self' data:",host,StringComparison.Ordinal);
        Assert.Contains("Origin",host,StringComparison.Ordinal);
        Assert.Contains("X-WritingVault-Session",host,StringComparison.Ordinal);
        Assert.Contains("64 * 1024",host,StringComparison.Ordinal);
        Assert.Contains("route.not_found",host,StringComparison.Ordinal);
    }

    [Fact]
    public void LocalStoryTimeConversionIsExplicitAndRejectsDaylightSavingGaps()
    {
        var wholeDay = new ViewerSessionUpdateRequest("Lostville", "Set",
            CurrentLocalDate: new DateOnly(2030, 3, 10),
            ReferenceTimeZoneId: "America/Vancouver").ToVaultUpdate();
        Assert.Equal(new DateOnly(2030, 3, 10), wholeDay.CurrentDate);
        Assert.Null(wholeDay.CurrentTime);
        var valid=new ViewerSessionUpdateRequest("Lostville","Set",CurrentLocalTime:"2030-01-12T21:15:00",ReferenceTimeZoneId:"America/Vancouver").ToVaultUpdate();
        Assert.Equal("Set",valid.TimeAction);Assert.Equal("America/Vancouver",valid.ReferenceTimeZoneId);Assert.NotNull(valid.CurrentTime);
        Assert.Equal(21,TimeZoneInfo.ConvertTime(valid.CurrentTime!.Value,TimeZoneInfo.FindSystemTimeZoneById("America/Vancouver")).Hour);
        var gap=new ViewerSessionUpdateRequest("Lostville","Set",CurrentLocalTime:"2030-03-10T02:30:00",ReferenceTimeZoneId:"America/Vancouver");
        var failure=Assert.Throws<ViewerRequestException>(()=>gap.ToVaultUpdate());
        Assert.Equal("session.time_invalid",failure.Code);Assert.Contains("does not exist",failure.Message,StringComparison.Ordinal);
        var invalidSavedZone=new ViewerSessionUpdateRequest("Lostville","Set",
            CurrentTime:new DateTimeOffset(2030,1,12,21,15,0,TimeSpan.FromHours(-8)),
            ReferenceTimeZoneId:"No/Such_Zone");
        var invalidZoneFailure=Assert.Throws<ViewerRequestException>(()=>invalidSavedZone.ToVaultUpdate());
        Assert.Equal("session.time_invalid",invalidZoneFailure.Code);
        Assert.Contains("not available",invalidZoneFailure.Message,StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrowserTabsGetIndependentInteractiveAndWatcherConnections()
    {
        var factory=new FakeFactory();await using var store=new ViewerSessionStore(factory);
        var first=await store.GetAsync(new string('a',32),CancellationToken.None);
        var second=await store.GetAsync(new string('b',32),CancellationToken.None);
        Assert.NotSame(first,second);Assert.NotSame(first.Interactive,first.Watcher);
        await first.SelectAsync(new("Lostville"),CancellationToken.None);
        await second.SelectAsync(new("Aurora", "Set", new DateTimeOffset(2030,1,2,3,4,5,TimeSpan.Zero), "UTC"),CancellationToken.None);
        Assert.Equal("Lostville",(await first.Interactive.GetSessionAsync()).ContinuityName);
        Assert.Equal("Aurora",(await second.Interactive.GetSessionAsync()).ContinuityName);
        Assert.Equal("Lostville",(await first.Watcher.GetSessionAsync()).ContinuityName);
        Assert.Equal("Aurora",(await second.Watcher.GetSessionAsync()).ContinuityName);
    }

    [Fact]
    public void StartupPlanHasIndependentHiddenRestartingTasksAndNoCredential()
    {
        var root=RepositoryRoot();var script=Path.Combine(root,"tools","Configure-WritingVaultStartup.ps1");
        var start=new ProcessStartInfo("powershell.exe") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
        foreach(var arg in new[]{"-NoLogo","-NoProfile","-ExecutionPolicy","Bypass","-File",script,"plan"})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEnd();var stderr=process.StandardError.ReadToEnd();process.WaitForExit();
        Assert.True(process.ExitCode==0,stderr);
        using var json=JsonDocument.Parse(stdout);var tasks=json.RootElement.EnumerateArray().ToArray();Assert.Equal(3,tasks.Length);
        Assert.NotEqual(tasks[0].GetProperty("name").GetString(),tasks[1].GetProperty("name").GetString());
        foreach(var task in tasks){Assert.True(task.GetProperty("hidden").GetBoolean());Assert.True(task.GetProperty("restartCount").GetInt32()>0);Assert.Equal(60,task.GetProperty("restartIntervalSeconds").GetInt32());}
        Assert.Equal("--service viewer",tasks[0].GetProperty("arguments").GetString());
        Assert.Equal("--service tunnel",tasks[1].GetProperty("arguments").GetString());
        foreach(var task in tasks){Assert.Contains("WritingVault.Tray.exe",task.GetProperty("executable").GetString(),StringComparison.Ordinal);}
        Assert.Contains("WritingVault.Tray.exe",tasks[2].GetProperty("executable").GetString(),StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:5284",stdout,StringComparison.Ordinal);
        Assert.DoesNotContain("CONTROL_PLANE_API_KEY",stdout,StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("openai-tunnel-api-key",stdout,StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BackgroundLoggingRotatesAndRedactsActualChildOutput()
    {
        var directory=Path.Combine(Path.GetTempPath(),"writing-vault-log-probe-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var child=Path.Combine(directory,"child.ps1");var wrapper=Path.Combine(directory,"wrapper.ps1");var log=Path.Combine(directory,"probe.log");
        static string Quoted(string path)=>path.Replace("'","''");
        try
        {
            File.WriteAllText(child,"Write-Output 'api_key=secret-for-test'\nWrite-Output 'Authorization: secret-for-test'\nWrite-Output ('x' * 200)\nexit 0\n");
            File.WriteAllText(wrapper,$". '{Quoted(Path.Combine(RepositoryRoot(),"tools","BoundedProcess.ps1"))}'\nexit (Invoke-WritingVaultBoundedProcess -FilePath 'powershell.exe' -ArgumentList @('-NoLogo','-NoProfile','-File','{Quoted(child)}') -LogPath '{Quoted(log)}' -MaximumBytes 180 -RetainedFiles 2)\n");
            var start=new ProcessStartInfo("powershell.exe") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden };
            foreach(var argument in new[]{"-NoLogo","-NoProfile","-ExecutionPolicy","Bypass","-File",wrapper})start.ArgumentList.Add(argument);
            using var process=Process.Start(start)!;
            if(!process.WaitForExit(15_000)) { process.Kill(true); Assert.Fail("The bounded-process probe did not exit."); }
            var stderr=process.StandardError.ReadToEnd();
            Assert.True(process.ExitCode==0,stderr);
            Assert.True(File.Exists(log+".1"),"The log did not rotate.");
            var combined=File.ReadAllText(log)+File.ReadAllText(log+".1");
            Assert.Contains("[redacted]",combined,StringComparison.Ordinal);
            Assert.DoesNotContain("secret-for-test",combined,StringComparison.Ordinal);
        }
        finally
        {
            var temporaryRoot=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!Path.GetFullPath(directory).StartsWith(temporaryRoot,StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to remove a log probe outside the temporary directory.");
            if(Directory.Exists(directory))Directory.Delete(directory,true);
        }
    }

    [Fact]
    public void BackgroundLaunchersUseTheBoundedProcessHelper()
    {
        var tunnel=File.ReadAllText(Path.Combine(RepositoryRoot(),"tools","Run-WritingVaultTunnel.ps1"));
        var viewer=File.ReadAllText(Path.Combine(RepositoryRoot(),"tools","Run-WritingVaultWeb.ps1"));
        Assert.Contains("Invoke-WritingVaultBoundedProcess",tunnel,StringComparison.Ordinal);
        Assert.Contains("Invoke-WritingVaultBoundedProcess",viewer,StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackgroundSupervisorCrashCannotOrphanItsChildProcess()
    {
        var root=RepositoryRoot();
        var directory=Path.Combine(Path.GetTempPath(),"writing-vault-phase8-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var pidFile=Path.Combine(directory,"child.pid");
        var childScript=Path.Combine(directory,"child.ps1");
        var wrapperScript=Path.Combine(directory,"wrapper.ps1");
        File.WriteAllText(childScript,"param([string] $PidFile)\n$PID | Set-Content -LiteralPath $PidFile -Encoding Ascii\nwhile($true){Start-Sleep -Seconds 1}\n");
        File.WriteAllText(wrapperScript,$". '{Path.Combine(root,"tools","BoundedProcess.ps1").Replace("'","''")}'\nInvoke-WritingVaultBoundedProcess -FilePath 'powershell.exe' -ArgumentList @('-NoLogo','-NoProfile','-File','{childScript.Replace("'","''")}','-PidFile','{pidFile.Replace("'","''")}') -LogPath '{Path.Combine(directory,"probe.log").Replace("'","''")}'\n");
        Process? wrapper=null;int childId=0;
        try
        {
            var start=new ProcessStartInfo("powershell.exe") { UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden };
            foreach(var argument in new[]{"-NoLogo","-NoProfile","-ExecutionPolicy","Bypass","-File",wrapperScript})start.ArgumentList.Add(argument);
            wrapper=Process.Start(start)??throw new InvalidOperationException("Could not start containment probe.");
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(!File.Exists(pidFile)&&DateTime.UtcNow<deadline)await Task.Delay(100);
            Assert.True(File.Exists(pidFile),"The containment probe child did not start.");
            childId=int.Parse((await File.ReadAllTextAsync(pidFile)).Trim(),System.Globalization.CultureInfo.InvariantCulture);
            Assert.False(Process.GetProcessById(childId).HasExited);
            wrapper.Kill();await wrapper.WaitForExitAsync();
            deadline=DateTime.UtcNow.AddSeconds(5);
            while(IsRunning(childId)&&DateTime.UtcNow<deadline)await Task.Delay(100);
            Assert.False(IsRunning(childId),"The child process survived its supervisor and would hold resources after maintenance stop.");
        }
        finally
        {
            if(wrapper is not null&&!wrapper.HasExited)wrapper.Kill();
            if(childId>0&&IsRunning(childId))Process.GetProcessById(childId).Kill();
            if(Directory.Exists(directory))Directory.Delete(directory,true);
            wrapper?.Dispose();
        }
    }

    private static string RepositoryRoot()
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"V4_API_PLAN.md")))directory=directory.Parent;
        return directory?.FullName??throw new InvalidOperationException("Could not locate repository root.");
    }

    private static bool IsRunning(int processId)
    {
        try{return !Process.GetProcessById(processId).HasExited;}
        catch(ArgumentException){return false;}
    }

    private sealed class FakeFactory:IVaultReadClientFactory
    {
        public Task<IVaultReadClient> ConnectAsync(string clientLabel,CancellationToken cancellationToken=default)=>Task.FromResult<IVaultReadClient>(new FakeClient());
    }
    private sealed class FakeClient:IVaultReadClient
    {
        private VaultSession session=new(null,new("Unset",null,null,"none"),"4.0");
        public Task<VaultHealth> HealthAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new VaultHealth(true,"4.0","test",0,"Healthy",0,null,[],"cursor"));
        public Task<VaultPage<VaultContinuity>> ListContinuitiesAsync(CancellationToken cancellationToken=default)=>Task.FromResult(new VaultPage<VaultContinuity>([],null,false,"cursor"));
        public Task<VaultPage<VaultContinuity>> ListContinuitiesPageAsync(string? cursor,int limit=100,CancellationToken cancellationToken=default)=>ListContinuitiesAsync(cancellationToken);
        public Task<VaultSession> GetSessionAsync(CancellationToken cancellationToken=default)=>Task.FromResult(session);
        public Task<VaultSession> SetSessionAsync(VaultSessionUpdate update,CancellationToken cancellationToken=default)
        {
            var clock=update.TimeAction=="Set"?new VaultClock("Set",update.CurrentTime,update.ReferenceTimeZoneId,"session"):new VaultClock("Unset",null,null,"none");
            session=new(update.ContinuityName??session.ContinuityName,clock,"4.0");return Task.FromResult(session);
        }
        public Task<VaultPage<VaultReference>> SearchAsync(VaultSearchRequest request,CancellationToken cancellationToken=default)=>Task.FromResult(new VaultPage<VaultReference>([],null,false,"cursor"));
        public Task<VaultRecord> GetAsync(string? reference=null,CancellationToken cancellationToken=default)=>Task.FromResult(new VaultRecord(new("continuity:test","Continuity",session.ContinuityName??"None"),new Dictionary<string,JsonElement>(),new Dictionary<string,VaultSection<VaultReference>>(),"cursor"));
        public Task<VaultRecord> GetRecordAsync(string reference,bool includeDeleted=false,CancellationToken cancellationToken=default)=>GetAsync(reference,cancellationToken);
        public Task<VaultRecordSnapshot> GetRecordSnapshotAsync(string reference,int snapshotVersion,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<VaultReference> LocateAsync(string reference,CancellationToken cancellationToken=default)=>Task.FromResult(new VaultReference(reference,"Character","Test record",ContinuityName:session.ContinuityName));
        public Task<VaultReference> LocateAsync(string reference,bool includeDeleted,CancellationToken cancellationToken=default)=>LocateAsync(reference,cancellationToken);
        public Task<VaultPage<VaultReference>> RelatedAsync(VaultRelatedRequest request,CancellationToken cancellationToken=default)=>Task.FromResult(new VaultPage<VaultReference>([],null,false,"cursor"));
        public Task<VaultPage<VaultHistoryEntry>> HistoryAsync(string? reference,string? cursor=null,int limit=50,CancellationToken cancellationToken=default)=>Task.FromResult(new VaultPage<VaultHistoryEntry>([],null,false,"cursor"));
        public Task<VaultTimelinePage> TimelineAsync(VaultTimelineRequest request,CancellationToken cancellationToken=default)=>Task.FromResult(new VaultTimelinePage([],null,false,[],session.Clock,"cursor"));
        public Task<VaultSourceSnapshotTextPage> SourceSnapshotViewAsync(string snapshotRef,string? cursor=null,int limit=8192,bool includeDeleted=false,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<VaultPage<VaultImageMetadata>> ImageListAsync(string target,string? cursor=null,int limit=50,bool includeDeleted=false,CancellationToken cancellationToken=default)=>Task.FromResult(new VaultPage<VaultImageMetadata>([],null,false,"cursor"));
        public Task<VaultPage<VaultImageMetadata>> ImageSearchAsync(string? text=null,string? cursor=null,int limit=50,bool acrossContinuities=false,CancellationToken cancellationToken=default)=>Task.FromResult(new VaultPage<VaultImageMetadata>([],null,false,"cursor"));
        public Task<VaultImageView> ImageViewAsync(string imageRef,string size="Thumbnail",int? revision=null,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<VaultImageRevisionHistory> ImageRevisionHistoryAsync(string imageRef,int? beforeRevision=null,int limit=50,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
        public Task<VaultChanges> ChangesSinceAsync(string? cursor,int waitSeconds,CancellationToken cancellationToken=default)=>Task.FromResult(new VaultChanges("cursor",[],true,false,"cursor"));
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
