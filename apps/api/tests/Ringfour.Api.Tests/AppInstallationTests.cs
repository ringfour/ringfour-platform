using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Ringfour.Api.Apps;
using Ringfour.Api.Connectivity;
using Ringfour.Api.Persistence;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AppInstallationTests(PostgresFixture postgres)
{
    private static Task<HttpResponseMessage> Post(HttpClient c,string path,JsonNode body,string? key=null) => c.SendAsync(new HttpRequestMessage(HttpMethod.Post,path) { Headers={{"Idempotency-Key",key??Guid.NewGuid().ToString()}}, Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json") });
    private static async Task<JsonObject> Json(HttpResponseMessage r)
    { var text=await r.Content.ReadAsStringAsync(); Assert.True(r.IsSuccessStatusCode,$"{r.StatusCode}: {text}");return JsonNode.Parse(text)!.AsObject(); }
    private static async Task Error(HttpResponseMessage r,int status,string code)
    { var text=await r.Content.ReadAsStringAsync();Assert.Equal(status,(int)r.StatusCode);ContractSchema.AssertValid("errors/v2/app-error.schema.json",text);Assert.Equal(code,JsonNode.Parse(text)!["error"]!["code"]!.GetValue<string>()); }
    private static JsonObject Fixture(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"contracts/fixtures/device/apps/v1/positive.json")))!.AsArray().Single(x=>x!["name"]!.GetValue<string>()==name)!["value"]!.DeepClone().AsObject();
    private sealed class Scenario : IAsyncDisposable
    {
        public required ApiFactory Factory;public required RegistryFixture Registry;public required HttpClient Owner;public required HttpClient Device;public required WebSocket Socket;
        public Guid Robot;public Guid Organization;public Guid Namespace;public long Sequence;public int ClientSequence=1;public required JsonObject Revision;
        public JsonObject Input => new(){["revision_id"]=Revision["id"]!.DeepClone(),["share_id"]=null,["configuration"]=Revision["manifest"]!["configuration"]!["defaults"]!.DeepClone()};
        public async ValueTask DisposeAsync(){Socket.Abort();Socket.Dispose();Owner.Dispose();Device.Dispose();await Factory.DisposeAsync();await Registry.DisposeAsync();}
    }
    private async Task<Scenario> Setup(bool supported=true)
    {
        var registry=new RegistryFixture();var options=registry.Configuration().ToDictionary(x=>x.Key,x=>x.Value);options["Apps:InstallationsEnabled"]="true";options["Apps:InstallationSweepMilliseconds"]="3600000";
        var factory=new ApiFactory(postgres.ConnectionString,extraConfiguration:options);var owner=factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());
        var org=await OrganizationTests.CreateOrganization(owner,"installation owner");var robot=await RobotTests.CreateRobot(owner,org,"installation robot");
        var(_,token)=await RobotTests.IssueToken(owner,robot);var(credential,secret)=await DeviceEnrollmentTests.Enroll(factory,token);
        var socket=await DeviceSocket.ConnectAsync(factory,robot,credential,secret);using var welcome=await DeviceSocket.ReceiveAsync(socket);var session=welcome.Payload().GetProperty("session_id").GetGuid();
        var machine=factory.CreateClient();machine.DefaultRequestHeaders.Add("x-ringfour-credential-id",credential.ToString());machine.DefaultRequestHeaders.Add("x-ringfour-credential-secret",secret);machine.DefaultRequestHeaders.Add("x-ringfour-device-session",session.ToString());var ns=Guid.NewGuid();
        await Json(await machine.PostAsJsonAsync("/v1/device/app-protocol/negotiate",new {session_id=session,deployment_namespace=ns,supported_app_protocol_versions=new[]{1},supports_app_installations=supported,supports_app_runs=supported}));
        var report=Fixture("base101_inventory");report["deployment_namespace"]=ns.ToString();report["report_id"]=Guid.NewGuid().ToString();await Json(await machine.PutAsJsonAsync("/v1/device/app-inventory",report));
        var manifest=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"contracts/fixtures/apps/v1/manifest.json")))!.AsObject();manifest["app_key"]="check-"+Guid.NewGuid().ToString("N");manifest["targets"]![0]!["image"]=registry.Image;
        var revision=await Json(await Post(owner,$"/v1/organizations/{org}/app-revisions",new JsonObject{["manifest"]=manifest,["visibility"]="listed"}));
        return new Scenario{Factory=factory,Registry=registry,Owner=owner,Device=machine,Socket=socket,Robot=robot,Organization=org,Namespace=ns,Revision=revision};
    }
    private static async Task<JsonObject> Command(Scenario s,JsonObject accepted)
    {
        using var scope=s.Factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<RingfourDbContext>();var manager=scope.ServiceProvider.GetRequiredService<DeviceConnectionManager>();
        await AppInstallationWorker.Dispatch(db,Guid.Parse(accepted["operation_id"]!.GetValue<string>()),manager,CancellationToken.None);
        using var frame=await DeviceSocket.ReceiveAsync(s.Socket);var command=JsonNode.Parse(frame.Payload().GetRawText())!.AsObject();
        Assert.Equal(accepted["operation_id"]!.ToString(),command["operation_id"]!.ToString());
        await DeviceSocket.SendAsync(s.Socket,s.Robot,++s.ClientSequence,"ack",new JsonObject{["operation_id"]=accepted["operation_id"]!.DeepClone()});return command;
    }
    private static JsonObject Applied(Scenario s,JsonObject command)
    {
        var payload=command["payload"]!;var release=payload["release"]!;var now=AppProtocol.Timestamp(DateTimeOffset.UtcNow);
        return new JsonObject{["app_protocol_version"]=1,["deployment_namespace"]=s.Namespace.ToString(),["report_id"]=Guid.NewGuid().ToString(),["report_sequence"]=++s.Sequence,["runs"]=new JsonArray(),
            ["operations"]=new JsonArray(new JsonObject{["operation_id"]=command["operation_id"]!.DeepClone(),["installation_id"]=payload["installation_id"]!.DeepClone(),["run_id"]=null,["kind"]=command["kind"]!.GetValue<string>().Replace("app.",""),["state"]="succeeded",["phase"]="applied",["observed_at"]=now,["failure"]=null}),
            ["installations"]=new JsonArray(new JsonObject{["installation_id"]=payload["installation_id"]!.DeepClone(),["observed_at"]=now,["candidate"]=null,
                ["installed"]=new JsonObject{["revision_id"]=release["revision_id"]!.DeepClone(),["configuration_revision_id"]=release["configuration_revision_id"]!.DeepClone(),["image"]=release["target"]!["image"]!.DeepClone(),["applied_operation_id"]=command["operation_id"]!.DeepClone(),["installed_at"]=now}})};
    }
    [Fact]
    public void Canonical_utf8_and_configuration_byte_limits_match_independent_unicode_fixture()
    {
        var fixture=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"contracts/fixtures/apps/v1/canonical-unicode.json")))!;
        var manifest=AppManifest.Validate(fixture["manifest"]!.ToJsonString());
        Assert.Equal(fixture["digest"]!.GetValue<string>(),"sha256:"+AppProtocol.Digest(manifest));
        Assert.Equal(fixture["canonical"]!.GetValue<string>(),Encoding.UTF8.GetString(AppProtocol.Canonical(manifest)));
        var stringManifest=manifest.DeepClone().AsObject();
        stringManifest["configuration"]=JsonNode.Parse("{\"schema\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"label\":{\"type\":\"string\",\"maxLength\":4096}},\"required\":[\"label\"]},\"defaults\":{\"label\":\"ok\"}}");
        var value=new JsonObject{["label"]=string.Concat(Enumerable.Repeat("🚀",2048))};
        Assert.True(AppProtocol.Canonical(value).Length<16384);
        AppInstallationEndpoints.ValidateConfiguration(stringManifest,value);
    }

    [Fact]
    public async Task Install_configure_and_rollback_are_durable_versioned_idempotent_and_do_not_create_runs()
    {
        await using var s=await Setup();var path=$"/v1/robots/{s.Robot}/app-installations";var key=Guid.NewGuid().ToString();
        var accepted=await Json(await Post(s.Owner,path,s.Input,key));Assert.Null(accepted["run_id"]);
        Assert.True(JsonNode.DeepEquals(accepted,await Json(await Post(s.Owner,path,s.Input,key))));
        var changed=s.Input;changed["configuration"]!["sample_count"]=2;await Error(await Post(s.Owner,path,changed,key),409,"idempotency_conflict");
        var installationPath=$"/v1/app-installations/{accepted["installation_id"]}";var before=await Json(await s.Owner.GetAsync(installationPath));Assert.Null(before["installed"]);Assert.NotNull(before["candidate"]);
        var command=await Command(s,accepted);var report=Applied(s,command);var receipt=await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",report));
        Assert.True(JsonNode.DeepEquals(receipt,await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",report))));
        var tamperedReport=report.DeepClone();tamperedReport["installations"]![0]!["installed"]!["image"]="example.com/other@sha256:"+new string('f',64);
        await Error(await s.Device.PutAsJsonAsync("/v1/device/app-observations",tamperedReport),409,"conflicting_report");
        var first=await Json(await s.Owner.GetAsync(installationPath));Assert.Null(first["candidate"]);var binding=first["installed"]!;
        await Json(await s.Owner.GetAsync($"/v1/app-operations/{accepted["operation_id"]}"));
        var configure=new JsonObject{["expected_configuration_revision_id"]=binding["configuration_revision_id"]!.DeepClone(),["configuration"]=new JsonObject{["sample_count"]=2}};
        var configured=await Json(await Post(s.Owner,installationPath+"/configure",configure));
        var pending=await Json(await s.Owner.GetAsync(installationPath));Assert.True(JsonNode.DeepEquals(binding,pending["installed"]));
        await Error(await Post(s.Owner,installationPath+"/configure",configure),409,"stale_installation");
        var configCommand=await Command(s,configured);await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,configCommand)));
        var second=await Json(await s.Owner.GetAsync(installationPath));var newBinding=second["installed"]!;
        Assert.NotEqual(binding["configuration_revision_id"]!.ToString(),newBinding["configuration_revision_id"]!.ToString());
        await Error(await Post(s.Owner,installationPath+"/configure",configure),409,"stale_installation");
        var rollback=new JsonObject{["expected_revision_id"]=newBinding["revision_id"]!.DeepClone(),["expected_configuration_revision_id"]=newBinding["configuration_revision_id"]!.DeepClone(),["revision_id"]=binding["revision_id"]!.DeepClone(),["configuration_revision_id"]=binding["configuration_revision_id"]!.DeepClone()};
        var rolled=await Json(await Post(s.Owner,installationPath+"/rollback",rollback));await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,await Command(s,rolled))));
        var final=await Json(await s.Owner.GetAsync(installationPath));Assert.Equal(binding["configuration_revision_id"]!.ToString(),final["installed"]!["configuration_revision_id"]!.ToString());
        using var stranger=s.Factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());
        await Error(await stranger.GetAsync(installationPath),404,"not_found");
        await Error(await stranger.GetAsync($"/v1/app-operations/{accepted["operation_id"]}"),404,"not_found");
        var configuration=await Json(await s.Owner.GetAsync(installationPath+$"/configuration-revisions/{binding["configuration_revision_id"]}"));Assert.Equal(1,configuration["values"]!["sample_count"]!.GetValue<int>());
        Assert.Equal(0L,(long)(await postgres.QueryAsync("SELECT count(*) FROM app_runs WHERE robot_id=@robot",("robot",s.Robot)))[0][0]!);
        Assert.Equal(0L,(long)(await postgres.QueryAsync("SELECT count(*) FROM script_executions WHERE robot_id=@robot",("robot",s.Robot)))[0][0]!);
    }
    [Fact]
    public async Task Invalid_config_wrong_actor_fabricated_application_and_inventory_only_daemon_are_refused()
    {
        await using var s=await Setup();var path=$"/v1/robots/{s.Robot}/app-installations";
        foreach(var config in new[]{"{\"sample_count\":true}","{\"sample_count\":0}","{\"sample_count\":1,\"extra\":1}","{}"})
        {var input=s.Input;input["configuration"]=JsonNode.Parse(config);await Error(await Post(s.Owner,path,input),400,"invalid_config");}
        using var stranger=s.Factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());await Error(await Post(stranger,path,s.Input),404,"not_found");
        var accepted=await Json(await Post(s.Owner,path,s.Input));var command=await Command(s,accepted);var report=Applied(s,command);
        report["installations"]![0]!["installed"]!["image"]="example.com/forged@sha256:"+new string('f',64);
        await Error(await s.Device.PutAsJsonAsync("/v1/device/app-observations",report),409,"invalid_observation");
        var unchanged=await Json(await s.Owner.GetAsync($"/v1/app-installations/{accepted["installation_id"]}"));Assert.Null(unchanged["installed"]);
        using var scope=s.Factory.Services.CreateScope();var db=scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
        await postgres.ExecuteAsync("UPDATE robot_app_inventory SET installations_supported=false WHERE robot_id=@robot",("robot",s.Robot));
        await Error(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,command)),409,"daemon_upgrade_required");
        await using var old=await Setup(false);await Error(await Post(old.Owner,$"/v1/robots/{old.Robot}/app-installations",old.Input),409,"daemon_upgrade_required");
    }
    [Fact]
    public async Task Offline_unsent_expiry_and_failed_initial_retry_keep_installation_identity()
    {
        await using var s=await Setup();s.Socket.Abort();var path=$"/v1/robots/{s.Robot}/app-installations";var accepted=await Json(await Post(s.Owner,path,s.Input));
        await postgres.ExecuteAsync("UPDATE app_operations SET deadline_at=requested_at WHERE id=@id",("id",Guid.Parse(accepted["operation_id"]!.ToString())));
        using var scope=s.Factory.Services.CreateScope();await AppInstallationWorker.Dispatch(scope.ServiceProvider.GetRequiredService<RingfourDbContext>(),Guid.Parse(accepted["operation_id"]!.ToString()),scope.ServiceProvider.GetRequiredService<DeviceConnectionManager>(),CancellationToken.None);
        var operation=await Json(await s.Owner.GetAsync($"/v1/app-operations/{accepted["operation_id"]}"));Assert.Equal("failed",operation["state"]!.ToString());Assert.Null(operation["observed_at"]);
        var retried=await Json(await Post(s.Owner,path,s.Input));Assert.Equal(accepted["installation_id"]!.ToString(),retried["installation_id"]!.ToString());Assert.NotEqual(accepted["operation_id"]!.ToString(),retried["operation_id"]!.ToString());
    }
    private static JsonObject StartInput(JsonNode binding) => new() { ["expected_revision_id"]=binding["revision_id"]!.DeepClone(), ["expected_configuration_revision_id"]=binding["configuration_revision_id"]!.DeepClone() };
    private static JsonObject RunReport(Scenario s, JsonObject accepted, string state, bool includeOperation=true)
    {
        var now=AppProtocol.Timestamp(DateTimeOffset.UtcNow);var failed=state=="failed";
        var failure=failed ? new JsonObject { ["code"]="execution_conflict", ["message"]="Stopped before start.", ["details"]=new JsonObject { ["retryable"]=false } } : null;
        return new JsonObject { ["app_protocol_version"]=1, ["deployment_namespace"]=s.Namespace.ToString(), ["report_id"]=Guid.NewGuid().ToString(), ["report_sequence"]=++s.Sequence,
            ["installations"]=new JsonArray(), ["operations"]=includeOperation ? new JsonArray(new JsonObject { ["operation_id"]=accepted["operation_id"]!.DeepClone(), ["installation_id"]=accepted["installation_id"]!.DeepClone(), ["run_id"]=accepted["run_id"]!.DeepClone(), ["kind"]="start", ["state"]=failed ? "failed" : "succeeded", ["phase"]=failed ? "failed" : "observed_running", ["observed_at"]=now, ["failure"]=failure?.DeepClone() }) : new JsonArray(),
            ["runs"]=new JsonArray(new JsonObject { ["run_id"]=accepted["run_id"]!.DeepClone(), ["installation_id"]=accepted["installation_id"]!.DeepClone(), ["state"]=state, ["started_at"]=failed ? null : now, ["ended_at"]=null, ["observed_at"]=now, ["exit_code"]=null, ["failure"]=failure }) };
    }
    [Fact]
    public async Task App_start_races_idempotency_observed_state_and_exact_stop_preserve_snapshot_and_slot()
    {
        await using var s=await Setup();var install=await Json(await Post(s.Owner,$"/v1/robots/{s.Robot}/app-installations",s.Input));
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,await Command(s,install))));
        var path=$"/v1/app-installations/{install["installation_id"]}";var binding=(await Json(await s.Owner.GetAsync(path)))["installed"]!;
        var input=StartInput(binding);var key=Guid.NewGuid().ToString();
        var requests=await Task.WhenAll(Post(s.Owner,path+"/start",input,key),Post(s.Owner,path+"/start",input));
        Assert.Single(requests,x=>x.StatusCode==HttpStatusCode.Accepted);Assert.Single(requests,x=>x.StatusCode==HttpStatusCode.Conflict);
        var accepted=await Json(requests.Single(x=>x.StatusCode==HttpStatusCode.Accepted));
        // Use the winning key for replay regardless of request scheduling.
        var replayKey=requests[0].StatusCode==HttpStatusCode.Accepted ? key : null;
        if(replayKey is not null) Assert.True(JsonNode.DeepEquals(accepted,await Json(await Post(s.Owner,path+"/start",input,replayKey))));
        var runPath=$"/v1/app-runs/{accepted["run_id"]}";var before=await Json(await s.Owner.GetAsync(runPath));Assert.Equal("accepted",before["state"]!.ToString());
        await Command(s,accepted);Assert.Equal("accepted",(await Json(await s.Owner.GetAsync(runPath)))["state"]!.ToString());
        var observed=RunReport(s,accepted,"running");await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",observed));
        var running=await Json(await s.Owner.GetAsync(runPath));
        Assert.Equal("running",running["state"]!.ToString());Assert.True(JsonNode.DeepEquals(before["snapshot"],running["snapshot"]));
        await Error(await Post(s.Owner,path+"/start",input),409,"execution_conflict");
        var stopping=await Json(await Post(s.Owner,path+"/stop",new JsonObject { ["expected_run_id"]=accepted["run_id"]!.DeepClone() }));await Command(s,stopping);
        var terminal=RunReport(s,accepted,"stopped",false);terminal["runs"]![0]!["started_at"]=running["started_at"]!.DeepClone();
        terminal["operations"]=new JsonArray(new JsonObject { ["operation_id"]=stopping["operation_id"]!.DeepClone(),["installation_id"]=accepted["installation_id"]!.DeepClone(),["run_id"]=accepted["run_id"]!.DeepClone(),["kind"]="stop",["state"]="succeeded",["phase"]="stopped",["observed_at"]=terminal["runs"]![0]!["observed_at"]!.DeepClone(),["failure"]=null });
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",terminal));
        var next=await Json(await Post(s.Owner,path+"/start",input));Assert.NotEqual(accepted["run_id"]!.ToString(),next["run_id"]!.ToString());
        var oldStop=await Json(await Post(s.Owner,path+"/stop",new JsonObject { ["expected_run_id"]=accepted["run_id"]!.DeepClone() }));
        Assert.Equal("succeeded",(await Json(await s.Owner.GetAsync($"/v1/app-operations/{oldStop["operation_id"]}")))["state"]!.ToString());
        Assert.Equal("accepted",(await Json(await s.Owner.GetAsync($"/v1/app-runs/{next["run_id"]}")))["state"]!.ToString());
        using var stranger=s.Factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());await Error(await stranger.GetAsync(runPath),404,"not_found");
        var list=await Json(await s.Owner.GetAsync($"/v1/organizations/{s.Organization}/app-runs?installation_id={install["installation_id"]}"));Assert.Equal(2,list["items"]!.AsArray().Count);
    }
    [Fact]
    public async Task Stop_can_queue_before_start_observation_and_undispatched_start_expiry_releases_slot()
    {
        await using var s=await Setup();var install=await Json(await Post(s.Owner,$"/v1/robots/{s.Robot}/app-installations",s.Input));await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,await Command(s,install))));
        var path=$"/v1/app-installations/{install["installation_id"]}";var input=StartInput((await Json(await s.Owner.GetAsync(path)))["installed"]!);
        var accepted=await Json(await Post(s.Owner,path+"/start",input));await Json(await Post(s.Owner,path+"/stop",new JsonObject { ["expected_run_id"]=accepted["run_id"]!.DeepClone() }));
        await postgres.ExecuteAsync("UPDATE app_operations SET deadline_at=requested_at WHERE id=@id",("id",Guid.Parse(accepted["operation_id"]!.ToString())));
        using var scope=s.Factory.Services.CreateScope();await AppInstallationWorker.Dispatch(scope.ServiceProvider.GetRequiredService<RingfourDbContext>(),Guid.Parse(accepted["operation_id"]!.ToString()),scope.ServiceProvider.GetRequiredService<DeviceConnectionManager>(),CancellationToken.None);
        Assert.Equal("failed",(await Json(await s.Owner.GetAsync($"/v1/app-runs/{accepted["run_id"]}")))["state"]!.ToString());
        await Json(await Post(s.Owner,path+"/start",input));
    }

}
