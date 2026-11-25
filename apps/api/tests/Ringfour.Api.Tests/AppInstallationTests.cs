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
public sealed partial class AppInstallationTests(PostgresFixture postgres)
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
    private async Task<Scenario> Setup(bool supported=true, bool manifestV2=false, bool? negotiatedManifestV2=null, bool liveSettings=false, bool automaticDispatch=false)
    {
        var registry=new RegistryFixture();var options=registry.Configuration().ToDictionary(x=>x.Key,x=>x.Value);options["Apps:InstallationsEnabled"]="true";options["Apps:LiveSettingsEnabled"]=liveSettings.ToString();options["Apps:InstallationSweepMilliseconds"]="3600000";
        var factory=new ApiFactory(postgres.ConnectionString,extraConfiguration:options,automaticAppDispatch:automaticDispatch);var owner=factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());
        var org=await OrganizationTests.CreateOrganization(owner,"installation owner");var robot=await RobotTests.CreateRobot(owner,org,"installation robot");
        var(_,token)=await RobotTests.IssueToken(owner,robot);var(credential,secret)=await DeviceEnrollmentTests.Enroll(factory,token);
        var socket=await DeviceSocket.ConnectAsync(factory,robot,credential,secret);using var welcome=await DeviceSocket.ReceiveAsync(socket);var session=welcome.Payload().GetProperty("session_id").GetGuid();
        var machine=factory.CreateClient();machine.DefaultRequestHeaders.Add("x-ringfour-credential-id",credential.ToString());machine.DefaultRequestHeaders.Add("x-ringfour-credential-secret",secret);machine.DefaultRequestHeaders.Add("x-ringfour-device-session",session.ToString());var ns=Guid.NewGuid();
        await Json(await machine.PostAsJsonAsync("/v1/device/app-protocol/negotiate",new {session_id=session,deployment_namespace=ns,supported_app_protocol_versions=new[]{1},supports_app_installations=supported,supports_app_runs=supported,supports_manifest_v2=negotiatedManifestV2??manifestV2,supports_live_settings=liveSettings}));
        var report=Fixture("base101_inventory");report["deployment_namespace"]=ns.ToString();report["report_id"]=Guid.NewGuid().ToString();await Json(await machine.PutAsJsonAsync("/v1/device/app-inventory",report));
        var manifestPath=manifestV2?"contracts/fixtures/apps/v2/manifest.json":"contracts/fixtures/apps/v1/manifest.json";
        var manifest=JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,manifestPath)))!.AsObject();manifest["app_key"]="check-"+Guid.NewGuid().ToString("N");manifest["targets"]![0]!["image"]=registry.Image;
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
    public async Task Committed_work_wakes_dispatch_with_an_hour_long_recovery_interval()
    {
        await using var s = await Setup(automaticDispatch: true);
        var accepted = await Json(await Post(s.Owner, $"/v1/robots/{s.Robot}/app-installations", s.Input));
        // No manual Dispatch and no timer acceleration: the real hosted worker is waiting for an hour.
        using var frame = await DeviceSocket.ReceiveAsync(s.Socket).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("command", frame.RootElement.GetProperty("type").GetString());
        Assert.Equal(accepted["operation_id"]!.ToString(), frame.Payload().GetProperty("operation_id").GetString());
        using var scope = s.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<RingfourDbContext>();
        var id = Guid.Parse(accepted["operation_id"]!.ToString());
        Assert.NotNull(await db.AppOperations.FindAsync(id));
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
        Assert.Equal(0L,(long)(await postgres.QueryAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name='script_executions'"))[0][0]!);
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
    public async Task Manifest_v2_start_freezes_and_resolves_declared_environment_mapping()
    {
        await using var s=await Setup(manifestV2:true,liveSettings:true);
        var install=await Json(await Post(s.Owner,$"/v1/robots/{s.Robot}/app-installations",s.Input));
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,await Command(s,install))));
        var path=$"/v1/app-installations/{install["installation_id"]}";
        var binding=(await Json(await s.Owner.GetAsync(path)))["installed"]!;
        var accepted=await Json(await Post(s.Owner,path+"/start",StartInput(binding)));
        var snapshot=(await Json(await s.Owner.GetAsync($"/v1/app-runs/{accepted["run_id"]}")))["snapshot"]!;
        Assert.Equal("3",snapshot["configuration_environment"]!["SAMPLE_COUNT"]!.GetValue<string>());
        Assert.Equal(250,snapshot["runtime_settings"]!["interval_ms"]!.GetValue<int>());
        Assert.NotNull(snapshot["initial_settings_application_id"]);
        Assert.True(JsonNode.DeepEquals(snapshot,(await Json(await s.Owner.GetAsync($"/v1/app-runs/{accepted["run_id"]}")))["snapshot"]));
    }

    [Fact]
    public async Task Manifest_v2_install_is_refused_when_current_daemon_did_not_negotiate_it()
    {
        await using var s=await Setup(manifestV2:true,negotiatedManifestV2:false);
        await Error(await Post(s.Owner,$"/v1/robots/{s.Robot}/app-installations",s.Input),409,"daemon_upgrade_required");
    }

    [Fact]
    public async Task Settings_are_versioned_and_idle_save_is_selected_for_the_next_run()
    {
        await using var s=await Setup(manifestV2:true,liveSettings:true);
        var install=await Json(await Post(s.Owner,$"/v1/robots/{s.Robot}/app-installations",s.Input));
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,await Command(s,install))));
        var path=$"/v1/app-installations/{install["installation_id"]}";
        var settingsPath=path+"/settings";
        var state=await Json(await s.Owner.GetAsync(settingsPath));
        Assert.Equal("supported",state["support"]!.GetValue<string>());
        Assert.Null(state["selected_for_next_start"]);
        await Error(await Post(s.Owner,settingsPath,new JsonObject
        {
            ["values"]=new JsonObject { ["interval_ms"]=0 },["expected_selected_revision_id"]=null,
            ["expected_applied_revision_id"]=null,["expected_run_id"]=null
        }),400,"invalid_input");
        var body=new JsonObject { ["values"]=new JsonObject { ["interval_ms"]=500 }, ["expected_selected_revision_id"]=null, ["expected_applied_revision_id"]=null, ["expected_run_id"]=null };
        var saved=await Json(await Post(s.Owner,settingsPath,body,"settings-idle-once"));
        Assert.Equal("saved_for_next_start",saved["state"]!.GetValue<string>());
        await Error(await Post(s.Owner,settingsPath,new JsonObject
        {
            ["values"]=new JsonObject { ["interval_ms"]=750 },["expected_selected_revision_id"]=null,
            ["expected_applied_revision_id"]=null,["expected_run_id"]=null
        }),409,"stale_settings_selection");
        var selected=(await Json(await s.Owner.GetAsync(settingsPath)))["selected_for_next_start"]!;
        Assert.Equal(saved["selected_revision_id"]!.ToString(),selected["id"]!.ToString());
        Assert.Equal(500,selected["values"]!["interval_ms"]!.GetValue<int>());
        var revision=await Json(await s.Owner.GetAsync(settingsPath+"/revisions/"+saved["settings_revision_id"]));
        Assert.True(JsonNode.DeepEquals(selected,revision));
        var history=await Json(await s.Owner.GetAsync($"/v1/organizations/{s.Organization}/app-logbook?installation_id={install["installation_id"]}"));
        Assert.Contains(history["items"]!.AsArray(),x=>x!["type"]!.GetValue<string>()=="app.settings.selected" && x["payload"]!["settings_revision_id"]!.GetValue<string>()==saved["settings_revision_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Active_settings_edit_is_an_exact_run_application_and_keeps_selection_until_confirmation()
    {
        await using var s=await Setup(manifestV2:true,liveSettings:true);
        var install=await Json(await Post(s.Owner,$"/v1/robots/{s.Robot}/app-installations",s.Input));
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,await Command(s,install))));
        var path=$"/v1/app-installations/{install["installation_id"]}";var settingsPath=path+"/settings";
        var saved=await Json(await Post(s.Owner,settingsPath,new JsonObject { ["values"]=new JsonObject { ["interval_ms"]=500 },["expected_selected_revision_id"]=null,["expected_applied_revision_id"]=null,["expected_run_id"]=null }));
        var binding=(await Json(await s.Owner.GetAsync(path)))["installed"]!;
        var accepted=await Json(await Post(s.Owner,path+"/start",StartInput(binding)));
        var snapshot=(await Json(await s.Owner.GetAsync($"/v1/app-runs/{accepted["run_id"]}")))["snapshot"]!;
        var initialId=snapshot["initial_settings_application_id"]!.GetValue<string>();
        var initialRevision=snapshot["runtime_settings_revision_id"]!.GetValue<string>();
        await postgres.ExecuteAsync("UPDATE app_settings_applications SET state='applied',delivery_state='reported',outcome='applied',reported_at=now(),completed_at=now() WHERE application_id=@id",("id",Guid.Parse(initialId)));
        await postgres.ExecuteAsync("UPDATE app_runs SET applied_settings_revision_id=@revision WHERE id=@run",("revision",Guid.Parse(initialRevision)),("run",Guid.Parse(accepted["run_id"]!.GetValue<string>())));
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",RunReport(s,accepted,"running")));
        var changeInput=new JsonObject
        {
            ["values"]=new JsonObject { ["interval_ms"]=750 },["expected_selected_revision_id"]=saved["selected_revision_id"]!.DeepClone(),
            ["expected_applied_revision_id"]=initialRevision,["expected_run_id"]=accepted["run_id"]!.DeepClone()
        };
        var changeKey=Guid.NewGuid().ToString();
        var changed=await Json(await Post(s.Owner,settingsPath,changeInput,changeKey));
        var replay=await Json(await Post(s.Owner,settingsPath,changeInput,changeKey));
        Assert.True(JsonNode.DeepEquals(changed,replay));
        Assert.Equal("pending",changed["state"]!.GetValue<string>());
        Assert.Equal(accepted["run_id"]!.ToString(),changed["run_id"]!.ToString());
        var state=await Json(await s.Owner.GetAsync(settingsPath));
        Assert.Equal(saved["selected_revision_id"]!.ToString(),state["selected_for_next_start"]!["id"]!.ToString());
        Assert.Equal(initialRevision,state["last_applied"]!["id"]!.ToString());
        Assert.Equal(changed["application_id"]!.ToString(),state["pending_application"]!["application_id"]!.ToString());
        Assert.Equal("queued",state["pending_application"]!["delivery_state"]!.GetValue<string>());
        var history=await Json(await s.Owner.GetAsync($"/v1/organizations/{s.Organization}/app-logbook?run_id={accepted["run_id"]}"));
        Assert.Contains(history["items"]!.AsArray(),x=>x!["type"]!.GetValue<string>()=="app.settings.application_requested" && x["payload"]!["application_id"]!.GetValue<string>()==changed["application_id"]!.GetValue<string>());
        await Error(await Post(s.Owner,settingsPath,changeInput),409,"settings_pending");
        var dispatchIdentity=new JsonObject
        {
            ["operation_id"]=changed["application_id"]!.DeepClone(),["installation_id"]=accepted["installation_id"]!.DeepClone(),
            ["run_id"]=accepted["run_id"]!.DeepClone()
        };
        var delivered=await Command(s,dispatchIdentity);
        Assert.Equal("app.settings.apply",delivered["kind"]!.GetValue<string>());
        Assert.Equal(changed["application_id"]!.ToString(),delivered["operation_id"]!.GetValue<string>());
        state=await Json(await s.Owner.GetAsync(settingsPath));
        Assert.Equal("delivered",state["pending_application"]!["delivery_state"]!.GetValue<string>());
        var runState=await Json(await s.Owner.GetAsync($"/v1/app-runs/{accepted["run_id"]}"));
        var observedAt=AppProtocol.Timestamp(DateTimeOffset.UtcNow.AddSeconds(1));
        var resultReport=new JsonObject
        {
            ["app_protocol_version"]=1,["deployment_namespace"]=s.Namespace.ToString(),["report_id"]=Guid.NewGuid().ToString(),["report_sequence"]=++s.Sequence,
            ["installations"]=new JsonArray(),
            ["operations"]=new JsonArray(new JsonObject
            {
                ["operation_id"]=changed["application_id"]!.DeepClone(),["installation_id"]=accepted["installation_id"]!.DeepClone(),
                ["run_id"]=accepted["run_id"]!.DeepClone(),["kind"]="settings.apply",["state"]="succeeded",["phase"]="applied",
                ["observed_at"]=observedAt,["failure"]=null,
                ["settings_application"]=new JsonObject { ["application_id"]=changed["application_id"]!.DeepClone(),
                    ["settings_revision_id"]=changed["settings_revision_id"]!.DeepClone(),["outcome"]="applied",["reason"]=null }
            }),
            ["runs"]=new JsonArray(new JsonObject
            {
                ["run_id"]=accepted["run_id"]!.DeepClone(),["installation_id"]=accepted["installation_id"]!.DeepClone(),["state"]="running",
                ["started_at"]=runState["started_at"]!.DeepClone(),["ended_at"]=null,["observed_at"]=observedAt,["exit_code"]=null,["failure"]=null
            })
        };
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",resultReport));
        state=await Json(await s.Owner.GetAsync(settingsPath));
        Assert.Equal(changed["settings_revision_id"]!.ToString(),state["selected_for_next_start"]!["id"]!.ToString());
        Assert.Equal(changed["settings_revision_id"]!.ToString(),state["last_applied"]!["id"]!.ToString());
        history=await Json(await s.Owner.GetAsync($"/v1/organizations/{s.Organization}/app-logbook?run_id={accepted["run_id"]}"));
        Assert.Contains(history["items"]!.AsArray(),x=>x!["type"]!.GetValue<string>()=="app.settings.applied" && x["payload"]!["application_id"]!.GetValue<string>()==changed["application_id"]!.GetValue<string>());
        var rejectedRequest=new JsonObject
        {
            ["values"]=new JsonObject { ["interval_ms"]=1000 },["expected_selected_revision_id"]=changed["settings_revision_id"]!.DeepClone(),
            ["expected_applied_revision_id"]=changed["settings_revision_id"]!.DeepClone(),["expected_run_id"]=accepted["run_id"]!.DeepClone()
        };
        var rejected=await Json(await Post(s.Owner,settingsPath,rejectedRequest));
        var rejectedDispatch=new JsonObject { ["operation_id"]=rejected["application_id"]!.DeepClone(),
            ["installation_id"]=accepted["installation_id"]!.DeepClone(),["run_id"]=accepted["run_id"]!.DeepClone() };
        await Command(s,rejectedDispatch);
        var rejectReason="App cannot use this interval while sampling.";
        var rejectedAt=AppProtocol.Timestamp(DateTimeOffset.UtcNow.AddSeconds(2));
        var rejectionReport=new JsonObject
        {
            ["app_protocol_version"]=1,["deployment_namespace"]=s.Namespace.ToString(),["report_id"]=Guid.NewGuid().ToString(),["report_sequence"]=++s.Sequence,
            ["installations"]=new JsonArray(),
            ["operations"]=new JsonArray(new JsonObject
            {
                ["operation_id"]=rejected["application_id"]!.DeepClone(),["installation_id"]=accepted["installation_id"]!.DeepClone(),
                ["run_id"]=accepted["run_id"]!.DeepClone(),["kind"]="settings.apply",["state"]="failed",["phase"]="failed",
                ["observed_at"]=rejectedAt,["failure"]=new JsonObject { ["code"]="settings_rejected",["message"]=rejectReason,["details"]=new JsonObject { ["retryable"]=false } },
                ["settings_application"]=new JsonObject { ["application_id"]=rejected["application_id"]!.DeepClone(),
                    ["settings_revision_id"]=rejected["settings_revision_id"]!.DeepClone(),["outcome"]="rejected",["reason"]=rejectReason }
            }),
            ["runs"]=new JsonArray(new JsonObject
            {
                ["run_id"]=accepted["run_id"]!.DeepClone(),["installation_id"]=accepted["installation_id"]!.DeepClone(),["state"]="running",
                ["started_at"]=runState["started_at"]!.DeepClone(),["ended_at"]=null,["observed_at"]=rejectedAt,["exit_code"]=null,["failure"]=null
            })
        };
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",rejectionReport));
        state=await Json(await s.Owner.GetAsync(settingsPath));
        Assert.Equal(changed["settings_revision_id"]!.ToString(),state["selected_for_next_start"]!["id"]!.ToString());
        Assert.Equal(changed["settings_revision_id"]!.ToString(),state["last_applied"]!["id"]!.ToString());
        Assert.Null(state["pending_application"]);
        history=await Json(await s.Owner.GetAsync($"/v1/organizations/{s.Organization}/app-logbook?run_id={accepted["run_id"]}"));
        Assert.Contains(history["items"]!.AsArray(),x=>x!["type"]!.GetValue<string>()=="app.settings.rejected" && x["payload"]!["application_id"]!.GetValue<string>()==rejected["application_id"]!.GetValue<string>());
    }
    [Fact]
    public async Task Run_ending_with_pending_initial_settings_marks_exact_application_unconfirmed()
    {
        await using var s=await Setup(manifestV2:true,liveSettings:true);
        var install=await Json(await Post(s.Owner,$"/v1/robots/{s.Robot}/app-installations",s.Input));
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,await Command(s,install))));
        var path=$"/v1/app-installations/{install["installation_id"]}";
        var binding=(await Json(await s.Owner.GetAsync(path)))["installed"]!;
        var accepted=await Json(await Post(s.Owner,path+"/start",StartInput(binding)));
        var starting=RunReport(s,accepted,"running");
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",starting));
        var terminal=RunReport(s,accepted,"stopped",false);
        terminal["runs"]![0]!["started_at"]=starting["runs"]![0]!["started_at"]!.DeepClone();
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",terminal));
        var state=await Json(await s.Owner.GetAsync(path+"/settings"));
        Assert.Null(state["active_run_id"]);Assert.Null(state["pending_application"]);
        var run=await Json(await s.Owner.GetAsync($"/v1/app-runs/{accepted["run_id"]}"));
        var applicationId=Guid.Parse(run["snapshot"]!["initial_settings_application_id"]!.GetValue<string>());
        Assert.Equal("ended_before_confirmation",(string)(await postgres.QueryAsync("SELECT state FROM app_settings_applications WHERE application_id=@id",("id",applicationId)))[0][0]!);
        var history=await Json(await s.Owner.GetAsync($"/v1/organizations/{s.Organization}/app-logbook?run_id={accepted["run_id"]}"));
        Assert.Contains(history["items"]!.AsArray(),x=>x!["type"]!.GetValue<string>()=="app.settings.ended_before_confirmation" && x["payload"]!["application_id"]!.GetValue<string>()==applicationId.ToString());
    }
    [Fact]
    public async Task Release_update_requires_valid_settings_and_rollback_restores_target_release_values()
    {
        await using var s=await Setup(manifestV2:true,liveSettings:true);
        var install=await Json(await Post(s.Owner,$"/v1/robots/{s.Robot}/app-installations",s.Input));
        var installCommand=await Command(s,install);
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,installCommand)));
        var path=$"/v1/app-installations/{install["installation_id"]}";
        var originalBinding=(await Json(await s.Owner.GetAsync(path)))["installed"]!;
        var originalConfiguration=installCommand["payload"]!["release"]!["configuration_revision_id"]!.GetValue<string>();
        var originalSettings=await Json(await Post(s.Owner,path+"/settings",new JsonObject
        {
            ["values"]=new JsonObject { ["interval_ms"]=750 },["expected_selected_revision_id"]=null,
            ["expected_applied_revision_id"]=null,["expected_run_id"]=null
        }));
        var targetManifest=s.Revision["manifest"]!.DeepClone().AsObject();
        targetManifest["release"]="0.2.0";
        targetManifest["runtime_settings"]!["schema"]=JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"period_seconds\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10}},\"required\":[\"period_seconds\"]}");
        targetManifest["runtime_settings"]!["defaults"]=JsonNode.Parse("{\"period_seconds\":1}");
        var nextRevision=await Json(await Post(s.Owner,$"/v1/organizations/{s.Organization}/app-revisions",new JsonObject { ["manifest"]=targetManifest,["visibility"]="listed" }));
        var updateBody=new JsonObject { ["expected_revision_id"]=originalBinding["revision_id"]!.DeepClone(),
            ["expected_configuration_revision_id"]=originalBinding["configuration_revision_id"]!.DeepClone(),
            ["revision_id"]=nextRevision["id"]!.DeepClone(),["share_id"]=null,
            ["configuration"]=targetManifest["configuration"]!["defaults"]!.DeepClone() };
        await Error(await Post(s.Owner,path+"/update",updateBody),409,"stale_settings_selection");
        updateBody["runtime_settings"]=new JsonObject { ["period_seconds"]=5 };
        var updated=await Json(await Post(s.Owner,path+"/update",updateBody));
        var updateCommand=await Command(s,updated);
        var updatedSettingsId=updateCommand["payload"]!["release"]!["runtime_settings_revision_id"]!.GetValue<string>();
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,updateCommand)));
        var changedBinding=(await Json(await s.Owner.GetAsync(path)))["installed"]!;
        Assert.Equal(nextRevision["id"]!.ToString(),changedBinding["revision_id"]!.ToString());
        var changedSettings=await Json(await s.Owner.GetAsync(path+"/settings"));
        Assert.Equal(updatedSettingsId,changedSettings["selected_for_next_start"]!["id"]!.ToString());
        Assert.Equal(5,changedSettings["selected_for_next_start"]!["values"]!["period_seconds"]!.GetValue<int>());
        var rollback=await Json(await Post(s.Owner,path+"/rollback",new JsonObject
        {
            ["expected_revision_id"]=changedBinding["revision_id"]!.DeepClone(),
            ["expected_configuration_revision_id"]=changedBinding["configuration_revision_id"]!.DeepClone(),
            ["revision_id"]=originalBinding["revision_id"]!.DeepClone(),
            ["configuration_revision_id"]=originalConfiguration
        }));
        var rollbackCommand=await Command(s,rollback);
        Assert.Equal(originalSettings["settings_revision_id"]!.ToString(),rollbackCommand["payload"]!["release"]!["runtime_settings_revision_id"]!.ToString());
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,rollbackCommand)));
        var restoredSettings=await Json(await s.Owner.GetAsync(path+"/settings"));
        Assert.Equal(originalSettings["settings_revision_id"]!.ToString(),restoredSettings["selected_for_next_start"]!["id"]!.ToString());
        Assert.Equal(750,restoredSettings["selected_for_next_start"]!["values"]!["interval_ms"]!.GetValue<int>());
        var noSettingsManifest=s.Revision["manifest"]!.DeepClone().AsObject();
        noSettingsManifest["release"]="0.3.0";noSettingsManifest.Remove("runtime_settings");
        var noSettingsRevision=await Json(await Post(s.Owner,$"/v1/organizations/{s.Organization}/app-revisions",new JsonObject { ["manifest"]=noSettingsManifest,["visibility"]="listed" }));
        var restoredBinding=(await Json(await s.Owner.GetAsync(path)))["installed"]!;
        var unsupportedUpdate=new JsonObject { ["expected_revision_id"]=restoredBinding["revision_id"]!.DeepClone(),
            ["expected_configuration_revision_id"]=restoredBinding["configuration_revision_id"]!.DeepClone(),
            ["revision_id"]=noSettingsRevision["id"]!.DeepClone(),["share_id"]=null,
            ["configuration"]=noSettingsManifest["configuration"]!["defaults"]!.DeepClone() };
        await Error(await Post(s.Owner,path+"/update",unsupportedUpdate),409,"stale_settings_selection");
        unsupportedUpdate["runtime_settings"]=null;
        var unsupported=await Json(await Post(s.Owner,path+"/update",unsupportedUpdate));
        var unsupportedCommand=await Command(s,unsupported);
        Assert.Null(unsupportedCommand["payload"]!["release"]!["runtime_settings_revision_id"]);
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,unsupportedCommand)));
        var unsupportedState=await Json(await s.Owner.GetAsync(path+"/settings"));
        Assert.Equal("unsupported",unsupportedState["support"]!.GetValue<string>());Assert.Null(unsupportedState["selected_for_next_start"]);
        var unsupportedBinding=(await Json(await s.Owner.GetAsync(path)))["installed"]!;
        var restore=await Json(await Post(s.Owner,path+"/rollback",new JsonObject
        {
            ["expected_revision_id"]=unsupportedBinding["revision_id"]!.DeepClone(),
            ["expected_configuration_revision_id"]=unsupportedBinding["configuration_revision_id"]!.DeepClone(),
            ["revision_id"]=originalBinding["revision_id"]!.DeepClone(),["configuration_revision_id"]=originalConfiguration
        }));
        var restoreCommand=await Command(s,restore);
        Assert.Equal(originalSettings["settings_revision_id"]!.ToString(),restoreCommand["payload"]!["release"]!["runtime_settings_revision_id"]!.ToString());
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,restoreCommand)));
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

    [Fact]
    public async Task Completed_App_run_accepts_deduplicated_late_output_and_immutable_artifacts_with_workspace_scope()
    {
        await using var s=await Setup();var install=await Json(await Post(s.Owner,$"/v1/robots/{s.Robot}/app-installations",s.Input));await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,await Command(s,install))));
        var path=$"/v1/app-installations/{install["installation_id"]}";var accepted=await Json(await Post(s.Owner,path+"/start",StartInput((await Json(await s.Owner.GetAsync(path)))["installed"]!)));await Command(s,accepted);
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",RunReport(s,accepted,"failed")));
        var runPath=$"/v1/app-runs/{accepted["run_id"]}";var machinePath=$"/v1/device/app-runs/{accepted["run_id"]}";
        var upload=machinePath+"/output/stdout?offset=0&sealed_output=true&truncated=true";
        Assert.Equal(HttpStatusCode.NoContent,(await s.Device.PutAsync(upload,new ByteArrayContent(Encoding.UTF8.GetBytes("hello 🦾")))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await s.Device.PutAsync(upload,new ByteArrayContent(Encoding.UTF8.GetBytes("hello 🦾")))).StatusCode);
        await Error(await s.Device.PutAsync(upload,new ByteArrayContent("changed"u8.ToArray())),409,"invalid_observation");
        var output=await Json(await s.Owner.GetAsync(runPath+"/output?stream=stdout"));Assert.Equal("hello 🦾",Encoding.UTF8.GetString(Convert.FromBase64String(output["data_base64"]!.ToString())));Assert.True(output["sealed"]!.GetValue<bool>());Assert.True(output["truncated"]!.GetValue<bool>());
        var bytes="frozen bytes"u8.ToArray();var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));var id=Guid.NewGuid();var artifact=machinePath+$"/artifacts/{id}?path=proof.bin&sha256={hash}&content_type=application/octet-stream";
        Assert.Equal(HttpStatusCode.NoContent,(await s.Device.PutAsync(artifact,new ByteArrayContent(bytes))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await s.Device.PutAsync(artifact,new ByteArrayContent(bytes))).StatusCode);
        await Error(await s.Device.PutAsync(artifact,new ByteArrayContent("tampered"u8.ToArray())),400,"invalid_input");
        var summary=await Json(await s.Owner.GetAsync(runPath+$"/artifacts/{id}"));Assert.Equal("available",summary["state"]!.ToString());Assert.Equal(bytes,await s.Owner.GetByteArrayAsync(runPath+$"/artifacts/{id}/content"));
        using var stranger=s.Factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());await Error(await stranger.GetAsync(runPath+$"/artifacts/{id}/content"),404,"not_found");
        var global=await Json(await s.Owner.GetAsync($"/v1/organizations/{s.Organization}/app-logbook"));var scoped=await Json(await s.Owner.GetAsync($"/v1/organizations/{s.Organization}/app-logbook?run_id={accepted["run_id"]}"));
        Assert.Contains(scoped["items"]!.AsArray(),x=>x!["type"]!.ToString()=="app.run.failed");Assert.All(scoped["items"]!.AsArray(),x=>Assert.Contains(global["items"]!.AsArray(),g=>g!["event_id"]!.ToString()==x!["event_id"]!.ToString()));
    }
    [Fact]
    public async Task App_artifacts_are_workspace_scoped_filter_bound_paginated_and_keep_repeated_paths_across_runs()
    {
        await using var s=await Setup();
        var install=await Json(await Post(s.Owner,$"/v1/robots/{s.Robot}/app-installations",s.Input));
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,await Command(s,install))));
        var installationPath=$"/v1/app-installations/{install["installation_id"]}";
        var binding=(await Json(await s.Owner.GetAsync(installationPath)))["installed"]!;
        var runIds=new List<Guid>();
        foreach(var content in new[]{"first proof"u8.ToArray(),"second proof"u8.ToArray()})
        {
            var run=await Json(await Post(s.Owner,installationPath+"/start",StartInput(binding)));
            await Command(s,run);
            await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",RunReport(s,run,"failed")));
            var hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
            var machinePath=$"/v1/device/app-runs/{run["run_id"]}";
            var artifact=machinePath+$"/artifacts/{Guid.NewGuid()}?path=proof.bin&sha256={hash}&content_type=application/octet-stream";
            Assert.Equal(HttpStatusCode.NoContent,(await s.Device.PutAsync(artifact,new ByteArrayContent(content))).StatusCode);
            runIds.Add(Guid.Parse(run["run_id"]!.GetValue<string>()));
            await Task.Delay(5);
        }

        var basePath=$"/v1/apps/{s.Revision["app_id"]}/artifacts?organization_id={s.Organization}";
        var first=await Json(await s.Owner.GetAsync(basePath+"&limit=1"));
        ContractSchema.AssertValid("artifacts/v3/app-artifact-page.schema.json",first.ToJsonString());
        Assert.Single(first["items"]!.AsArray());
        Assert.Equal(runIds[1].ToString(),first["items"]![0]!["run_id"]!.ToString());
        Assert.Equal("proof.bin",first["items"]![0]!["path"]!.ToString());
        Assert.NotNull(first["items"]![0]!["published_at"]);
        var cursor=first["next_cursor"]!.GetValue<string>();
        var second=await Json(await s.Owner.GetAsync(basePath+"&limit=1&cursor="+Uri.EscapeDataString(cursor)));
        ContractSchema.AssertValid("artifacts/v3/app-artifact-page.schema.json",second.ToJsonString());
        Assert.Equal(runIds[0].ToString(),second["items"]![0]!["run_id"]!.ToString());
        Assert.Null(second["next_cursor"]);
        await Error(await s.Owner.GetAsync(basePath+"&limit=1&cursor="+Uri.EscapeDataString(cursor)+"&path_prefix=proof"),400,"invalid_input");

        var byRun=await Json(await s.Owner.GetAsync(basePath+"&run_id="+runIds[0]));
        Assert.Single(byRun["items"]!.AsArray());
        Assert.Equal(install["installation_id"]!.ToString(),byRun["items"]![0]!["installation_id"]!.ToString());
        Assert.Equal(s.Robot.ToString(),byRun["items"]![0]!["robot_id"]!.ToString());
        Assert.Equal(2,(await Json(await s.Owner.GetAsync(basePath+"&path_prefix=proof")))["items"]!.AsArray().Count);
        await Error(await s.Owner.GetAsync($"/v1/apps/{Guid.NewGuid()}/artifacts?organization_id={s.Organization}"),404,"not_found");
        using var stranger=s.Factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());
        await Error(await stranger.GetAsync(basePath),404,"not_found");
    }
    [Fact]
    public async Task App_HITL_is_run_bound_and_submitted_response_requires_device_confirmation()
    {
        await using var s=await Setup();var install=await Json(await Post(s.Owner,$"/v1/robots/{s.Robot}/app-installations",s.Input));await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,await Command(s,install))));
        var path=$"/v1/app-installations/{install["installation_id"]}";var accepted=await Json(await Post(s.Owner,path+"/start",StartInput((await Json(await s.Owner.GetAsync(path)))["installed"]!)));await Command(s,accepted);
        var request=Guid.NewGuid();var now=AppProtocol.Timestamp(DateTimeOffset.UtcNow);var sync=new JsonObject { ["request_id"]=request.ToString(),["execution_id"]=accepted["run_id"]!.DeepClone(),["prompt"]="Continue?",["choices"]=JsonNode.Parse("[{\"value\":\"yes\",\"label\":\"Yes\"},{\"value\":\"no\",\"label\":\"No\"}]"),["deadline"]=null,["timeout_outcome"]=null,["state"]="pending",["outcome"]=null,["applied_response_id"]=null,["created_at"]=now,["updated_at"]=now };
        await Json(await s.Device.PutAsJsonAsync($"/v1/device/interactions/{request}",sync));var interaction=$"/v1/app-runs/{accepted["run_id"]}/interactions/{request}";
        var submitted=await Json(await Post(s.Owner,interaction+"/responses",new JsonObject { ["chosen_value"]="yes" }));Assert.Equal("submitted",submitted["response"]!["status"]!.ToString());
        await Error(await s.Owner.GetAsync($"/v1/app-runs/{Guid.NewGuid()}/interactions/{request}"),404,"not_found");
        sync["state"]="answered";sync["outcome"]="yes";sync["applied_response_id"]=submitted["response"]!["response_id"]!.DeepClone();sync["updated_at"]=AppProtocol.Timestamp(DateTimeOffset.UtcNow);
        await Json(await s.Device.PutAsJsonAsync($"/v1/device/interactions/{request}",sync));var applied=await Json(await s.Owner.GetAsync(interaction));Assert.Equal("applied",applied["response"]!["status"]!.ToString());
        var events=await Json(await s.Owner.GetAsync($"/v1/organizations/{s.Organization}/app-logbook?run_id={accepted["run_id"]}"));Assert.Contains(events["items"]!.AsArray(),x=>x!["type"]!.ToString()=="human.interaction.answered");
    }

}
