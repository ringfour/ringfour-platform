using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Ringfour.Api.Tests;

[Collection(PostgresCollection.Name)]
public sealed class RobotReenrollmentProcessTests(PostgresFixture postgres)
{
    private static int Port()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0); listener.Start(); return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }
    private static async Task<JsonObject> Json(HttpResponseMessage response)
    {
        var text=await response.Content.ReadAsStringAsync();Assert.True(response.IsSuccessStatusCode,$"{response.StatusCode}: {text}");return JsonNode.Parse(text)!.AsObject();
    }
    [Fact]
    [Trait("Category", "ProcessIntegration")]
    public async Task Actual_Daemon_archives_released_identity_and_enrolls_in_another_organization_after_restart()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while(repository is not null && !File.Exists(Path.Combine(repository.FullName,"apps/daemon/pyproject.toml"))) repository=repository.Parent;
        Assert.NotNull(repository);
        var daemon = Path.Combine(repository.FullName,"apps/daemon");
        var python = Path.Combine(daemon,".venv/bin/python");
        Assert.True(File.Exists(python),"Prepare the independent Daemon environment with uv sync --locked in apps/daemon before this process test.");
        await using var factory = new ApiFactory(postgres.ConnectionString);
        factory.UseKestrel(0);
        using var owner=factory.CreateAuthenticatedClient("auth0|"+Guid.NewGuid());
        using var local=new HttpClient();
        var folder=Path.Combine(Path.GetTempPath(),"rf-robot-release-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        var port=Port(); var status=Port();
        local.BaseAddress=new Uri($"http://127.0.0.1:{port}");
        Process? process=null;
        async Task Start()
        {
            var info=new ProcessStartInfo(python){WorkingDirectory=daemon,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(var arg in new[]{"-c","from ringfour_daemon.main import main; main()"}) info.ArgumentList.Add(arg);
            foreach(var pair in new Dictionary<string,string>{["DAEMON_DB_PATH"]=Path.Combine(folder,"daemon.db"),["DAEMON_CREDENTIAL_PATH"]=Path.Combine(folder,"credential"),["PLATFORM_API_URL"]=owner.BaseAddress!.ToString().TrimEnd('/'),["RINGFOUR_DAEMON_HOST"]="127.0.0.1",["RINGFOUR_DAEMON_PORT"]=port.ToString(),["RINGFOUR_STATUS_HOST"]="127.0.0.1",["RINGFOUR_STATUS_PORT"]=status.ToString(),["RINGFOUR_AGENT_URI"]="",["RINGFOUR_APPS_ENABLED"]="false",["PYTHONDONTWRITEBYTECODE"]="1"}) info.Environment[pair.Key]=pair.Value;
            process=Process.Start(info)!;
            process.OutputDataReceived+=(_,_)=>{};process.ErrorDataReceived+=(_,_)=>{};process.BeginOutputReadLine();process.BeginErrorReadLine();
            for(var attempt=0;attempt<120;attempt++)
            {
                Assert.False(process.HasExited,"Disposable Daemon exited before readiness");
                try {if((await local.GetAsync("/v1/identity")).IsSuccessStatusCode)return;}catch(HttpRequestException){}
                await Task.Delay(100);
            }
            throw new TimeoutException("Disposable Daemon readiness");
        }
        async Task Stop()
        {
            if(process is null)return;
            if(!process.HasExited)
            {
                using var signal=Process.Start(new ProcessStartInfo("kill"){ArgumentList={"-INT",process.Id.ToString()},UseShellExecute=false});
                if(signal is not null)await signal.WaitForExitAsync();
                try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));}catch(TimeoutException){process.Kill(true);await process.WaitForExitAsync();throw;}
            }
            process.Dispose();process=null;
        }
        try
        {
            var a=await OrganizationTests.CreateOrganization(owner,"Device old organization");
            var b=await OrganizationTests.CreateOrganization(owner,"Device new organization");
            var oldRobot=await RobotTests.CreateRobot(owner,a,"Old binding");
            var newRobot=await RobotTests.CreateRobot(owner,b,"New binding");
            await Start();
            var(_,token)=await RobotTests.IssueToken(owner,oldRobot);
            Assert.Equal(oldRobot.ToString(),(await Json(await local.PostAsJsonAsync("/v1/enroll",new{token})))["robot_id"]!.ToString());
            Assert.Equal(HttpStatusCode.NoContent,(await owner.PostAsync($"/v1/robots/{oldRobot}/unenroll",null)).StatusCode);
            await Stop();
            await Start();
            var identity=await Json(await local.GetAsync("/v1/identity"));
            Assert.False(identity["enrolled"]!.GetValue<bool>());
            Assert.Single(Directory.GetFiles(Path.Combine(folder,"enrollment-history"),"*.sqlite"));
            var(_,nextToken)=await RobotTests.IssueToken(owner,newRobot);
            var enrolled=await Json(await local.PostAsJsonAsync("/v1/enroll",new{token=nextToken}));
            Assert.Equal(newRobot.ToString(),enrolled["robot_id"]!.ToString());
            Assert.Equal(b.ToString(),enrolled["organization_id"]!.ToString());
        }
        finally{await Stop();Directory.Delete(folder,true);}
    }
}
