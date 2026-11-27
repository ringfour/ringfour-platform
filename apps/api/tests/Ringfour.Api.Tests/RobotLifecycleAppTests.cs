using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Ringfour.Api.Apps;

namespace Ringfour.Api.Tests;

public sealed partial class AppInstallationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Robot_release_blocks_work_and_detaches_idle_apps_preserving_history(bool delete)
    {
        await using var s = await Setup(uninstallSupported: true);
        async Task Blocked()
        {
            foreach(var response in new[]{await s.Owner.DeleteAsync($"/v1/robots/{s.Robot}"),await s.Owner.PostAsync($"/v1/robots/{s.Robot}/unenroll",null)})
            {
                Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
                Assert.Equal("robot_has_apps",JsonNode.Parse(await response.Content.ReadAsStringAsync())!["error"]!["code"]!.ToString());
            }
        }
        var install=await Json(await Post(s.Owner,$"/v1/robots/{s.Robot}/app-installations",s.Input));
        await Blocked();
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",Applied(s,await Command(s,install))));
        var resource="/v1/app-installations/"+install["installation_id"];
        var binding=(await Json(await s.Owner.GetAsync(resource)))["installed"]!;
        var start=await Json(await Post(s.Owner,resource+"/start",StartInput(binding)));
        await Command(s,start);
        var running = RunReport(s,start,"running");
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",running));
        await Blocked();
        var terminal=RunReport(s,start,"succeeded",includeOperation:false);
        terminal["runs"]![0]!["started_at"]=(await Json(await s.Owner.GetAsync($"/v1/app-runs/{start["run_id"]}")))["started_at"]!.DeepClone();
        terminal["runs"]![0]!["ended_at"]=terminal["runs"]![0]!["observed_at"]!.DeepClone();
        terminal["runs"]![0]!["exit_code"]=0;
        await Json(await s.Device.PutAsJsonAsync("/v1/device/app-observations",terminal));
        var response = delete ? await s.Owner.DeleteAsync($"/v1/robots/{s.Robot}") : await s.Owner.PostAsync($"/v1/robots/{s.Robot}/unenroll", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(delete ? HttpStatusCode.NotFound : HttpStatusCode.OK,(await s.Owner.GetAsync($"/v1/robots/{s.Robot}")).StatusCode);
        var detached = await Json(await s.Owner.GetAsync(resource));
        Assert.Null(detached["installed"]);
        Assert.Null(detached["candidate"]);
        Assert.NotNull(detached["removed_at"]);
        Assert.Equal(HttpStatusCode.OK,(await s.Owner.GetAsync($"/v1/app-runs/{start["run_id"]}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await s.Owner.GetAsync(resource)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,(await s.Owner.GetAsync(resource+"/configuration-revisions/"+binding["configuration_revision_id"])).StatusCode);
    }
}
