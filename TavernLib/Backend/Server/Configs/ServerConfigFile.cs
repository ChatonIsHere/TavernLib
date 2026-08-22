using System;
using System.IO;
using Newtonsoft.Json;

namespace TavernLib.Backend.Server.Configs;

public abstract class ServerConfigFile<T>(string filePath) where T : class, new()
{
    private string FilePath { get; set; } = filePath;
    public T LastRead { get; private set; } = new();


    public virtual void ReadFromFile()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                LastRead = new T();
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(LastRead, Formatting.Indented));
                return;
            }

            // An empty or truncated file deserializes to null. That's "nothing
            // configured yet", and defaulting it here means no reader has to
            // remember to null-check a config that is never null otherwise.
            LastRead = JsonConvert.DeserializeObject<T>(File.ReadAllText(FilePath)) ?? new T();
        }
        catch (Exception e)
        {
            TavernLogger.Error($"Error when managing file responsible for type {nameof(T)}! {e}");
            throw;
        }
    }

    public virtual void WriteToFile()
    {
        try
        {
            LastRead ??= new T();
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(LastRead, Formatting.Indented));
        }
        catch (Exception e)
        {
            TavernLogger.Error($"Error when managing file responsible for type {nameof(T)}! {e}");
            throw;
        }
    }
}
