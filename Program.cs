using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApplication1.Data;
using WebApplication1.Models;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

/// Railway PORT
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

/// Database connection
var connection =
    Environment.GetEnvironmentVariable("DATABASE_URL") ??
    builder.Configuration.GetConnectionString("DefaultConnection");

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(connection));

var app = builder.Build();

/// Enable WebSockets
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
});

/// Health route
app.MapGet("/", () => "WebSocket server running");

ConcurrentDictionary<Guid, WebSocket> clients = new();

app.Map("/ws", async (HttpContext context, AppDbContext db) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket only");
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();

    var clientId = Guid.NewGuid();
    clients.TryAdd(clientId, socket);

    Console.WriteLine($"Client Connected: {clientId}");

    await SendItems(socket, db);

    var buffer = new byte[1024];

    while (socket.State == WebSocketState.Open)
    {
        try
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                clients.TryRemove(clientId, out _);

                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closed",
                    CancellationToken.None);

                Console.WriteLine($"Client Disconnected: {clientId}");
                break;
            }

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

            Console.WriteLine("Message received: " + message);

            bool changed = false;

            var doc = JsonDocument.Parse(message);

            if (doc.RootElement.TryGetProperty("action", out var action))
            {
                var act = action.GetString();

                if (act == "all_on")
                {
                    var items = await db.Kiran.ToListAsync();

                    foreach (var i in items)
                        i.Status = 1;

                    await db.SaveChangesAsync();
                    changed = true;
                }

                if (act == "all_off")
                {
                    var items = await db.Kiran.ToListAsync();

                    foreach (var i in items)
                        i.Status = 0;

                    await db.SaveChangesAsync();
                    changed = true;
                }
            }
            else
            {
                var data = JsonSerializer.Deserialize<Kiran>(message);

                if (data != null)
                {
                    var item = await db.Kiran
                        .FirstOrDefaultAsync(x => x.ItemId == data.ItemId);

                    if (item != null)
                    {
                        item.Status = data.Status;
                        await db.SaveChangesAsync();
                        changed = true;
                    }
                }
            }

            if (changed)
                await Broadcast(db);
        }
        catch (Exception ex)
        {
            Console.WriteLine("WebSocket error: " + ex.Message);
        }
    }
});

app.Run();


/// Send items to new client
async Task SendItems(WebSocket socket, AppDbContext db)
{
    try
    {
        var items = await db.Kiran.ToListAsync();

        var json = JsonSerializer.Serialize(items);

        Console.WriteLine("Sending items: " + json);

        var bytes = Encoding.UTF8.GetBytes(json);

        await socket.SendAsync(
            bytes,
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine("DB ERROR: " + ex.Message);

        var empty = Encoding.UTF8.GetBytes("[]");

        await socket.SendAsync(
            empty,
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
    }
}

/// Broadcast updates
async Task Broadcast(AppDbContext db)
{
    try
    {
        var items = await db.Kiran.ToListAsync();

        var json = JsonSerializer.Serialize(items);

        var bytes = Encoding.UTF8.GetBytes(json);

        foreach (var client in clients.Values)
        {
            if (client.State == WebSocketState.Open)
            {
                await client.SendAsync(
                    bytes,
                    WebSocketMessageType.Text,
                    true,
                    CancellationToken.None);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("Broadcast error: " + ex.Message);
    }
}