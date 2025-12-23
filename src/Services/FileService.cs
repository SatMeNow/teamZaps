using System.ComponentModel;
using teamZaps.Utils;

namespace teamZaps.Services;

[AttributeUsage(AttributeTargets.Class)]
public class StorageAttribute(string Folder, string FileFormat) : Attribute
{
    public string Folder { get; } = Folder;
    public string FileFormat { get; } = FileFormat;
}

/// <summary>
/// Manages file operations and storage.
/// </summary>
public class FileService<T>
{
    #region Constants
    private const string DataFolder = "data";
    public static readonly IReadOnlyDictionary<Type, StorageAttribute> StorageTypes = UtilAssembly.GetDefinedTypeMap<StorageAttribute>();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    #endregion


    public FileService(ILogger<FileService<T>> logger)
    {
        this.logger = logger;

        // Ensure storage directories exist:
        foreach (var storage in StorageTypes.Keys)
        {
            var storagePath = GetStoragePath(storage);
            if (!Directory.Exists(storagePath))
            {
                Directory.CreateDirectory(storagePath);
                logger.LogInformation("Created storage folder '{Directory}'", StorageTypes[storage].Folder);
            }
        }
    }


    #region Operation
    public async Task WriteAsync(T dataSet, long id)
    {
        var storageType = ValidateStorygeType();
        var filePath = GetFilePath(storageType, id);
        var json = JsonSerializer.Serialize(dataSet, JsonOptions);
        await File.WriteAllTextAsync(filePath, json).ConfigureAwait(false);
    }
    public Task<T?> ReadAsync(long id)
    {
        var storageType = ValidateStorygeType();
        var filePath = GetFilePath(storageType, id);
        return (ReadAsync(filePath));
    }
    public async Task<T?> ReadAsync(string filePath)
    {
        var storageType = ValidateStorygeType();
        try
        {
            if (!File.Exists(filePath))
                return (default);

            var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
            return (JsonSerializer.Deserialize<T>(json, JsonOptions));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read dataset of type {Type} from file '{File}'", typeof(T).Name, filePath);
            return (default);
        }
    }
    public async Task<ICollection<T>> ReadAllAsync()
    {
        var storageType = ValidateStorygeType();
        var storagePath = GetStoragePath(storageType);
        var records = new List<T>();
        try
        {
            if (!Directory.Exists(storagePath))
                return (records);

            // Process each recovery file:
            foreach (var file in Directory.GetFiles(storagePath, GetFileName(storageType)))
            {
                var dataSet = await ReadAsync(file).ConfigureAwait(false);
                if (dataSet is not null)
                    records.Add(dataSet);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read datasets of type {Type}", typeof(T).Name);
        }

        return (records);
    }
    public void Delete(IEnumerable<long> ids)
    {
        _ = Task.Run(async () => DeleteAsync(ids));
    }
    public async Task DeleteAsync(IEnumerable<long> ids)
    {
        foreach (var id in ids)
            await DeleteAsync(id).ConfigureAwait(false);
    }
    public async Task DeleteAsync(long id)
    {
        var storageType = ValidateStorygeType();
        try
        {
            var filePath = GetFilePath(storageType, id);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
                //logger.LogInformation("Deleted dataset {Id} of type {Type}", id, typeof(T).Name);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete dataset {Id} of type {Type}", id, typeof(T).Name);
        }
    }
    #endregion


    #region Helper
    private Type ValidateStorygeType()
    {
        var storageType = typeof(T);
        if (StorageTypes.ContainsKey(storageType))
            return (storageType);
        else
            throw new NotSupportedException($"Type {storageType.Name} is not a supported storage type!");
    }
    private string GetStoragePath(Type storageType) => Path.Combine(AppContext.BaseDirectory, DataFolder, StorageTypes[storageType].Folder);
    private string GetFilePath(Type storageType, long? id = null) => Path.Combine(GetStoragePath(storageType), GetFileName(storageType, id));
    private string GetFileName(Type storageType, long? id = null) => string.Format(StorageTypes[storageType].FileFormat, (id?.ToString() ?? "*"));
    #endregion


    private readonly ILogger<FileService<T>> logger;
}