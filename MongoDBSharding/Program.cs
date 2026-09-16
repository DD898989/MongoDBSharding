using MongoDB.Bson;
using MongoDB.Driver;
using MongoDBSharding.Endpoints;
using MongoDBSharding.Models;

var builder = WebApplication.CreateBuilder(args);

const string DBName = "ShardingDb";

builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Critical);
builder.Logging.AddFilter("Microsoft", LogLevel.Critical);

builder.Services.AddOpenApi();

string connectionString = "mongodb://mongos:15564";

var mongoSettings = MongoClientSettings.FromConnectionString(connectionString);

builder.Services.AddSingleton<IMongoClient>(new MongoClient(mongoSettings));
builder.Services.AddSingleton<IMongoDatabase>(sp => 
    sp.GetRequiredService<IMongoClient>().GetDatabase(DBName));

// Register the unified MongoDbContext
builder.Services.AddSingleton<MongoDbContext>();

var app = builder.Build();

app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "MongoDBSharding API v1");
});

app.UseHttpsRedirection();

app.MapGet("/", () => Results.Ok(new
{
    Message = "MongoDB Sharding Demonstration API is online and healthy!",
    SwaggerUrl = "/swagger",
    StatusUrl = "/api/sharding/status"
}));

// Register the demonstration endpoints
app.MapShardingEndpoints();

var client = app.Services.GetRequiredService<IMongoClient>();
var adminDb = client.GetDatabase("admin");
await adminDb.RunCommandAsync<BsonDocument>(new BsonDocument { { "enableSharding", DBName } });
await adminDb.RunCommandAsync<BsonDocument>(new BsonDocument 
{ 
    { "movePrimary", DBName }, 
    { "to", "mydefaultReplSet" } 
});
await adminDb.RunCommandAsync<BsonDocument>(new BsonDocument
{
    { "shardCollection", $"{DBName}.{nameof(MongoDbContext.Orders)}" },
    { "key", new BsonDocument { { nameof(Order.OrderId), "hashed" } } }
});
await adminDb.RunCommandAsync<BsonDocument>(new BsonDocument
{
    { "updateZoneKeyRange", $"{DBName}.{nameof(MongoDbContext.Orders)}" },
    { "min", new BsonDocument { { nameof(Order.OrderId), BsonMinKey.Value } } },
    { "max", new BsonDocument { { nameof(Order.OrderId), BsonMaxKey.Value } } },
    { "zone", "my_zone" }
});

app.Run();
