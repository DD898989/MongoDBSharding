using MongoDB.Bson;
using MongoDB.Driver;
using MongoDBSharding.Endpoints;
using MongoDBSharding.Models;

var builder = WebApplication.CreateBuilder(args);

const string DBName = "ShardingDb";

builder.Services.AddOpenApi();

string connectionString = "mongodb://mongos:15564/?readPreference=secondaryPreferred";

var mongoSettings = MongoClientSettings.FromConnectionString(connectionString);

builder.Services.AddSingleton<IMongoClient>(new MongoClient(mongoSettings));
builder.Services.AddSingleton<IMongoDatabase>(sp => 
    sp.GetRequiredService<IMongoClient>().GetDatabase(DBName));

builder.Services.AddSingleton<MongoDbContext>();

var app = builder.Build();

app.MapOpenApi();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "MongoDBSharding API v1");
});

app.UseHttpsRedirection();


app.MapShardingEndpoints();

var client = app.Services.GetRequiredService<IMongoClient>();
var adminDb = client.GetDatabase("admin");


RunAdminCommandIgnoreError(adminDb,
    new BsonDocument
    {
                { "shardCollection", $"{DBName}.{nameof(MongoDbContext.Orders)}" },
                { "key", new BsonDocument { { nameof(Order.OrderId), "hashed" } } }
    },
    "already sharded");
RunAdminCommandIgnoreError(adminDb,
    new BsonDocument
    {
                { "updateZoneKeyRange", $"{DBName}.{nameof(MongoDbContext.Orders)}" },
                { "min", new BsonDocument { { nameof(Order.OrderId), BsonMinKey.Value } } },
                { "max", new BsonDocument { { nameof(Order.OrderId), BsonMaxKey.Value } } },
                { "zone", "my_zone" }
    },
    "already exists", "overlapping");

app.Run();

static void RunAdminCommandIgnoreError(IMongoDatabase adminDb, BsonDocument command, params string[] ignorableErrors)
{
    try
    {
        adminDb.RunCommand<BsonDocument>(command);
    }
    catch (MongoCommandException ex)
    {
        bool ignored = ignorableErrors.Any(err => ex.Message.Contains(err, StringComparison.OrdinalIgnoreCase));
        if (!ignored)
            throw;
    }
}
