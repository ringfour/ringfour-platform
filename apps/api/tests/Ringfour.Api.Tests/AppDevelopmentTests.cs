using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ringfour.Api.Development;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Tests;

public sealed partial class AppInstallationTests
{
    private static async Task<JsonObject> DevelopmentInstall(Scenario s)
    {
        var install=await Json(await Post(s.Owner,$"/v1/robots/{s.Robot}/app-installations",s.Input));
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,await Command(s,install))));
        Assert.Equal(HttpStatusCode.NoContent,(await s.Device.PutAsJsonAsync("/v1/device/app-development/endpoint",new {daemon_url="https://127.0.0.1:8085",certificate_sha256=new string('a',64)})).StatusCode);
        return install;
    }
    [Fact]
    public async Task Development_session_source_and_run_preserve_real_app_provenance()
    {
        await using var s=await Setup();var installation=await DevelopmentInstall(s);
        var path=$"/v1/robots/{s.Robot}/app-development";
        var input=new JsonObject{["installation_id"]=installation["installation_id"]!.DeepClone(),["ttl_seconds"]=3600};
        var key=Guid.NewGuid().ToString();var session=await Json(await Post(s.Owner,path,input,key));
        Assert.True(JsonNode.DeepEquals(session,await Json(await Post(s.Owner,path,input,key))));
        Assert.Equal(HttpStatusCode.Conflict,(await Post(s.Owner,path,input)).StatusCode);
        var resource="/v1/app-installations/"+installation["installation_id"];
        var binding=(await Json(await s.Owner.GetAsync(resource)))["installed"]!;
        Assert.Equal(HttpStatusCode.Conflict,(await Post(s.Owner,resource+"/start",StartInput(binding))).StatusCode);
        var grant=await Json(await Post(s.Owner,path+"/grants",new JsonObject{["session_id"]=session["session_id"]!.DeepClone(),["token_sha256"]=new string('b',64)}));
        Assert.Equal("https://127.0.0.1:8085",grant["daemon_url"]!.ToString());Assert.Null(grant["session"]!["grant_hash"]);
        var poll=await Json(await s.Device.GetAsync("/v1/device/app-development"));Assert.Equal(new string('b',64),poll["session"]!["grant_hash"]!.ToString());
        var source=new {operation_id=Guid.NewGuid(),base_generation=0,generation=1,archive_sha256="sha256:"+new string('c',64)};
        var report=$"/v1/device/app-development/{session["session_id"]}/source";
        Assert.Equal(HttpStatusCode.NoContent,(await s.Device.PostAsJsonAsync(report,source)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await s.Device.PostAsJsonAsync(report,source)).StatusCode);
        var start=StartInput(binding);start["development_session_id"]=session["session_id"]!.DeepClone();
        var accepted=await Json(await Post(s.Owner,resource+"/start",start));var run=await Json(await s.Owner.GetAsync("/v1/app-runs/"+accepted["run_id"]));
        Assert.Equal(1,run["snapshot"]!["development"]!["generation"]!.GetValue<int>());
        Assert.Equal(source.archive_sha256,run["snapshot"]!["development"]!["archive_sha256"]!.ToString());
        Assert.Equal(binding["image"]!.ToString(),run["snapshot"]!["image"]!.ToString());
        Assert.Equal(HttpStatusCode.Conflict,(await s.Owner.DeleteAsync(path+"?expected_session_id="+Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await Post(s.Owner,path+"/grants",new JsonObject{["session_id"]=Guid.NewGuid().ToString(),["token_sha256"]=new string('b',64)})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await s.Owner.DeleteAsync(path+"?expected_session_id="+Guid.NewGuid())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await Post(s.Owner,path+"/grants",new JsonObject{["session_id"]=Guid.NewGuid().ToString(),["token_sha256"]=new string('d',64)})).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await s.Owner.DeleteAsync(path+"?expected_session_id="+session["session_id"])).StatusCode);
        Assert.Null((await Json(await s.Device.GetAsync("/v1/device/app-development")))["session"]);
    }
    [Fact]
    public async Task Development_rejects_foreign_session_source_replays_and_expired_grants()
    {
        await using var s=await Setup();var installation=await DevelopmentInstall(s);var path=$"/v1/robots/{s.Robot}/app-development";
        var session=await Json(await Post(s.Owner,path,new JsonObject{["installation_id"]=installation["installation_id"]!.DeepClone(),["ttl_seconds"]=300}));
        using var foreign=s.Factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NotFound,(await foreign.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await Post(foreign,path+"/grants",new JsonObject{["session_id"]=session["session_id"]!.DeepClone(),["token_sha256"]=new string('b',64)})).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await s.Device.PostAsJsonAsync($"/v1/device/app-development/{Guid.NewGuid()}/source",new {operation_id=Guid.NewGuid(),base_generation=0,generation=1,archive_sha256="sha256:"+new string('c',64)})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await s.Device.PostAsJsonAsync($"/v1/device/app-development/{session["session_id"]}/source",new {operation_id=Guid.NewGuid(),base_generation=1,generation=2,archive_sha256="sha256:"+new string('c',64)})).StatusCode);
        using var scope=s.Factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
        await db.AppDevelopmentSessions.ExecuteUpdateAsync(u=>u.SetProperty(x=>x.ExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));
        Assert.Null((await Json(await s.Device.GetAsync("/v1/device/app-development")))["session"]);
        Assert.Equal(HttpStatusCode.NotFound,(await Post(s.Owner,path+"/grants",new JsonObject{["session_id"]=session["session_id"]!.DeepClone(),["token_sha256"]=new string('b',64)})).StatusCode);
    }
    [Fact]
    public async Task Development_operator_cannot_reserve_a_session_and_demotion_revokes_local_grants()
    {
        await using var s=await Setup();var installation=await DevelopmentInstall(s);var path=$"/v1/robots/{s.Robot}/app-development";
        using var account=s.Factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());
        var identity=await Json(await account.GetAsync("/v1/me"));var userId=identity["user"]!["id"]!.ToString();
        Assert.Equal(HttpStatusCode.Created,(await s.Owner.PostAsJsonAsync($"/v1/organizations/{s.Organization}/members",new {user_id=userId,role="operator"})).StatusCode);
        var input=new JsonObject{["installation_id"]=installation["installation_id"]!.DeepClone(),["ttl_seconds"]=3600};
        Assert.Equal(HttpStatusCode.Forbidden,(await Post(account,path,input)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await s.Owner.PatchAsJsonAsync($"/v1/organizations/{s.Organization}/members/{userId}",new {role="developer"})).StatusCode);
        var session=await Json(await Post(account,path,input));
        Assert.Equal(HttpStatusCode.OK,(await s.Owner.PatchAsJsonAsync($"/v1/organizations/{s.Organization}/members/{userId}",new {role="operator"})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await Post(account,path+"/grants",new JsonObject{["session_id"]=session["session_id"]!.DeepClone(),["token_sha256"]=new string('a',64)})).StatusCode);
        Assert.Null((await Json(await s.Device.GetAsync("/v1/device/app-development")))["session"]);
    }

}
