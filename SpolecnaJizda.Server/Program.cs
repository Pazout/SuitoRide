using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

var builder=WebApplication.CreateBuilder(args);
builder.Services.AddSignalR(options=>{options.MaximumReceiveMessageSize=4096;options.EnableDetailedErrors=false;});
builder.Services.AddSingleton<RideRooms>();
var app=builder.Build();
app.MapGet("/",()=>Results.Text("SuitoRide: server pro společnou jízdu"));
app.MapHub<RideHub>("/ride");
app.Run();

public sealed record RiderState(string Id,string Name,double Distance,bool Riding);
public sealed class Room(string routeHash)
{
    public string RouteHash { get; }=routeHash;
    public Dictionary<string,RiderState> Riders { get; }=[];
}
public sealed class RideRooms
{
    public object Sync { get; }=new();
    public Dictionary<string,Room> Rooms { get; }=[];
    public Dictionary<string,string> Membership { get; }=[];
}
public sealed class RideHub(RideRooms state) : Hub
{
    public async Task Join(string roomCode,string routeHash,string name,bool create)
    {
        roomCode=(roomCode??"").Trim().ToUpperInvariant();name=(name??"").Trim();
        if(roomCode.Length!=32 || roomCode.Any(c=>!Uri.IsHexDigit(c)) ||
            routeHash?.Length!=64 || routeHash.Any(c=>!Uri.IsHexDigit(c)) ||
            name.Length<1 || name.Length>30 || name.Any(char.IsControl))
            throw new HubException("Neplatný kód, trasa nebo jméno.");
        RiderState[] existing;
        lock(state.Sync)
        {
            if(state.Membership.ContainsKey(Context.ConnectionId)) throw new HubException("Už jsi připojený.");
            if(create)
            {
                if(state.Rooms.ContainsKey(roomCode))throw new HubException("Místnost už existuje.");
                if(state.Rooms.Count>=1000)throw new HubException("Server je plný.");
                state.Rooms.Add(roomCode,new Room(routeHash));
            }
            else if(!state.Rooms.ContainsKey(roomCode))throw new HubException("Místnost nebyla nalezena.");
            var room=state.Rooms[roomCode];
            if(room.RouteHash!=routeHash)throw new HubException("Trasy nejsou shodné. Načtěte stejný GPX ve stejném směru.");
            if(room.Riders.Count>=8)throw new HubException("Místnost je plná.");
            existing=room.Riders.Values.ToArray();
            room.Riders[Context.ConnectionId]=new(Context.ConnectionId,name,0,false);
            state.Membership[Context.ConnectionId]=roomCode;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId,roomCode);
        foreach(var rider in existing)await Clients.Caller.SendAsync("RiderUpdated",rider);
        await Clients.OthersInGroup(roomCode).SendAsync("RiderUpdated",new RiderState(Context.ConnectionId,name,0,false));
    }
    public async Task Update(double distance,bool riding)
    {
        if(!double.IsFinite(distance)||distance<0||distance>10000000)throw new HubException("Neplatná vzdálenost.");
        string? code; RiderState rider;
        lock(state.Sync)
        {
            if(!state.Membership.TryGetValue(Context.ConnectionId,out code))throw new HubException("Nejsi v místnosti.");
            var room=state.Rooms[code];
            rider=room.Riders[Context.ConnectionId] with { Distance=distance,Riding=riding };
            room.Riders[Context.ConnectionId]=rider;
        }
        await Clients.OthersInGroup(code).SendAsync("RiderUpdated",rider);
    }
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        string? code=null;
        lock(state.Sync)
        {
            if(state.Membership.Remove(Context.ConnectionId,out code) && state.Rooms.TryGetValue(code,out var room))
            {
                room.Riders.Remove(Context.ConnectionId);
                if(room.Riders.Count==0)state.Rooms.Remove(code);
            }
        }
        if(code!=null)await Clients.OthersInGroup(code).SendAsync("RiderLeft",Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }
}