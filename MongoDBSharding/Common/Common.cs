using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;

namespace MongoDBSharding.Models;

public class MyCountry
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Code { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class MongoDbContext
{
    public IMongoCollection<Order> Orders { get; }
    public IMongoCollection<MyCountry> Countries { get; }

    public MongoDbContext(IMongoDatabase database)
    {
        Orders = database.GetCollection<Order>("Orders");
        Countries = database.GetCollection<MyCountry>("MyCountry");
    }
}

public class Order
{
    [BsonId]
    [BsonRepresentation(BsonType.ObjectId)]
    public string? Id { get; set; }

    public string? OrderId { get; set; }
    public string? CustomerName { get; set; }
    public decimal Amount { get; set; }
    public string? Status { get; set; }
    [BsonRepresentation(BsonType.ObjectId)]
    public string? CountryId { get; set; }
    public DateTime CreatedAt { get; set; }
}
