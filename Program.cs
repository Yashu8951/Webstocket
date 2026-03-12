using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApplication1.Data;
using WebApplication1.Models;

var builder = WebApplication.CreateBuilder(args);

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var connection = Environment.GetEnvironmentVariable("DATABASE_URL");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connection));

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/", () => "WebSocket running");

app.Map("/ws", async (HttpContext context, IServiceScopeFactory scopeFactory) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
        return;

    var socket = await context.WebSockets.AcceptWebSocketAsync();

    Console.WriteLine("Client connected");

    var buffer = new byte[4096];

    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    try
    {
        var items = await db.Kiran.ToListAsync();
        var json = JsonSerializer.Serialize(items);

        await socket.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );

        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closed",
                    CancellationToken.None
                );
                break;
            }

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

            Console.WriteLine("Received: " + message);

            var data = JsonSerializer.Deserialize<Kiran>(message);

            if (data != null)
            {
                var item = await db.Kiran
                    .FirstOrDefaultAsync(x => x.ItemId == data.ItemId);

                if (item != null)
                {
                    item.Status = data.Status;
                    await db.SaveChangesAsync();
                }
            }

            var updated = await db.Kiran.ToListAsync();
            var updatedJson = JsonSerializer.Serialize(updated);

            await socket.SendAsync(
                Encoding.UTF8.GetBytes(updatedJson),
                WebSocketMessageType.Text,
                true,
                CancellationToken.None
            );
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("WebSocket Error: " + ex.Message);
    }

    Console.WriteLine("Client disconnected");
});

app.Run();