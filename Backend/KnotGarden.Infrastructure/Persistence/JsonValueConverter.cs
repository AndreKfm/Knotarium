using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using KnotGarden.Core.Domain;

namespace KnotGarden.Infrastructure.Persistence;

public class JsonValueConverter<T> : ValueConverter<T, string>
{
    public JsonValueConverter()
        : base(
            v => JsonSerializer.Serialize(v, PersistenceJsonOptions.Default),
            s => JsonSerializer.Deserialize<T>(s, PersistenceJsonOptions.Default) ?? default!)
    {
    }
}
