using System.Text.Json;
using Kintsugi.Application.Hosts;
using Kintsugi.Application.Hosts.Commands.CreateHost;
using Kintsugi.Domain.Entities;

namespace Kintsugi.Tests.Application;

public class HostWindowsUpdateStateTests
{
    private const string State = """{"rebootPending":true,"lastScanUtc":"2026-11-20T01:02:03Z","policy":{"wuServer":"http://wsus.example.com:8530","useWUServer":true}}""";

    [Fact]
    public void TheAgentsCheckInBindsTheStateAsJson()
    {
        // What the Windows agent's RegisterHostRequest sends, read the way the API reads it.
        var body = $$"""{"hostname":"pc","serialNumber":"S1","checkInMinute":5,"windowsUpdateState":{{State}}}""";

        var command = JsonSerializer.Deserialize<CreateHostCommand>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(State, command.WindowsUpdateState!.Value.GetRawText());
        Assert.Null(JsonSerializer.Deserialize<CreateHostCommand>("""{"hostname":"pc","serialNumber":"S1"}""", new JsonSerializerOptions(JsonSerializerDefaults.Web))!.WindowsUpdateState);
    }

    [Fact]
    public void TheStateIsKeptAndReachesTheHostDto()
    {
        var host = new Host("pc", "S1", "Windows 11 Pro");

        host.RecordWindowsUpdateState(State);

        var dto = HostDto.FromEntity(host);
        Assert.True(dto.WindowsUpdateState!.Value.GetProperty("rebootPending").GetBoolean());
        Assert.Equal("http://wsus.example.com:8530", dto.WindowsUpdateState.Value.GetProperty("policy").GetProperty("wuServer").GetString());
    }

    [Fact]
    public void NothingReportedKeepsWhatIsThere_AndATooLongOneIsIgnoredNotRefused()
    {
        var host = new Host("pc", "S1", "Windows 11 Pro");
        host.RecordWindowsUpdateState(State);

        host.RecordWindowsUpdateState(null);
        host.RecordWindowsUpdateState("  ");
        host.RecordWindowsUpdateState(new string('x', Host.MaxWindowsUpdateStateLength + 1));

        Assert.Equal(State, host.WindowsUpdateStateJson);
    }
}
