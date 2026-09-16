using MongoDB.Bson;
using MongoDB.Driver;
using MongoDBSharding.Endpoints;
using MongoDBSharding.Models;

var builder = WebApplication.CreateBuilder(args);
var dbName = "MyShardingDb";

builder.Services.AddSingleton<IMongoClient>(new MongoClient("mongodb://mymongos-service:15564/?readPreference=secondaryPreferred"));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(dbName));
builder.Services.AddSingleton<MongoDbContext>();

var app = builder.Build();
app.MapShardingEndpoints();

var adminDb = app.Services.GetRequiredService<IMongoClient>().GetDatabase("admin");

RunAdminCommandIgnoreError(adminDb, new()
{
    { "shardCollection", $"{dbName}.{nameof(MongoDbContext.Orders)}" },
    { "key", new BsonDocument(nameof(Order.OrderId), "hashed") }
}, "already sharded");

RunAdminCommandIgnoreError(adminDb, new()
{
    { "updateZoneKeyRange", $"{dbName}.{nameof(MongoDbContext.Orders)}" },
    { "min", new BsonDocument(nameof(Order.OrderId), BsonMinKey.Value) },
    { "max", new BsonDocument(nameof(Order.OrderId), BsonMaxKey.Value) },
    { "zone", "my_zone" }
}, "already exists", "overlapping");

static void RunAdminCommandIgnoreError(IMongoDatabase adminDb, BsonDocument command, params string[] ignorableErrors)
{
    try
    {
        adminDb.RunCommand<BsonDocument>(command);
    }
    catch (MongoCommandException ex) when (ignorableErrors.Any(err => ex.Message.Contains(err, StringComparison.OrdinalIgnoreCase)))
    {
        // Ignored exception
    }
}

app.Run("http://0.0.0.0:8080");