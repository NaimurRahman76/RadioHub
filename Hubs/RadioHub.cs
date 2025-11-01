using Microsoft.AspNetCore.SignalR;

namespace RadioStation.Hubs
{
    public class RadioHub : Hub
    {
        public Task Ping() => Task.CompletedTask;
    }
}
