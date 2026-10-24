using SubReel.Launcher.Core.Models;
using SubReel.Launcher.Abstractions.Paths;

namespace SubReel.Launcher.Core.Instances;

public sealed class InstanceService(IInstanceRepository repository, IAppPaths paths)
{
    public Task<IReadOnlyList<MinecraftInstance>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        return repository.GetAllAsync(cancellationToken);
    }

    public async Task<MinecraftInstance> CreateAsync(
        string name,
        string minecraftVersion,
        MinecraftLoaderType loaderType,
        string? loaderVersion,
        int memoryMb,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        minecraftVersion = minecraftVersion.Trim();

        if (name.Length is < 1 or > 60)
            throw new ArgumentException(
                "Название сборки должно содержать от 1 до 60 символов.",
                nameof(name));

        if (string.IsNullOrWhiteSpace(minecraftVersion))
            throw new ArgumentException(
                "Укажи версию Minecraft.",
                nameof(minecraftVersion));

        if (minecraftVersion.Length > 32)
            throw new ArgumentException(
                "Слишком длинная версия Minecraft.",
                nameof(minecraftVersion));

        if (!Enum.IsDefined(loaderType))
            throw new ArgumentOutOfRangeException(nameof(loaderType));

        if (memoryMb is < 512 or > 65536)
            throw new ArgumentOutOfRangeException(
                nameof(memoryMb),
                "Память должна быть в диапазоне 512–65536 МБ.");

        if (loaderType == MinecraftLoaderType.Vanilla)
            loaderVersion = null;
        else if (string.IsNullOrWhiteSpace(loaderVersion))
            loaderVersion = null;
        else
            loaderVersion = loaderVersion.Trim();

        paths.EnsureCreated();
        var id = Guid.NewGuid();
        var instanceRoot = Path.Combine(paths.Instances, id.ToString("N"));
        var instance = new MinecraftInstance
        {
            Id = id,
            Name = name,
            MinecraftVersion = minecraftVersion,
            LoaderType = loaderType,
            LoaderVersion = loaderVersion,
            MemoryMb = memoryMb,
            GameDirectory = Path.Combine(instanceRoot, "game")
        };

        Directory.CreateDirectory(instance.GameDirectory);

        try
        {
            await repository.SaveAsync(instance, cancellationToken);
            return instance;
        }
        catch
        {
            // Удаляем только созданную нами пустую папку.
            if (Directory.Exists(instance.GameDirectory) && !Directory.EnumerateFileSystemEntries(instance.GameDirectory).Any())
                Directory.Delete(instance.GameDirectory);
            if (Directory.Exists(instanceRoot) && !Directory.EnumerateFileSystemEntries(instanceRoot).Any())
                Directory.Delete(instanceRoot);

            throw;
        }
    }

    public async Task UpdateAsync(
        MinecraftInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var existing = await repository.GetByIdAsync(
            instance.Id,
            cancellationToken);

        if (existing is null)
            throw new InvalidOperationException(
                "Сборка не найдена.");

        if (string.IsNullOrWhiteSpace(instance.Name) ||
            instance.Name.Trim().Length > 60)
            throw new ArgumentException(
                "Название должно содержать от 1 до 60 символов.");

        if (string.IsNullOrWhiteSpace(instance.MinecraftVersion) ||
            instance.MinecraftVersion.Trim().Length > 32)
            throw new ArgumentException(
                "Укажи корректную версию Minecraft.");

        if (instance.MemoryMb is < 512 or > 65536)
            throw new ArgumentOutOfRangeException(
                nameof(instance.MemoryMb));

        if (!Enum.IsDefined(instance.LoaderType))
            throw new ArgumentOutOfRangeException(
                nameof(instance.LoaderType));

        instance.Name = instance.Name.Trim();
        instance.MinecraftVersion = instance.MinecraftVersion.Trim();

        if (instance.LoaderType == MinecraftLoaderType.Vanilla)
            instance.LoaderVersion = null;

        await repository.SaveAsync(instance, cancellationToken);
    }

    public async Task DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var instance = await repository.GetByIdAsync(
            id,
            cancellationToken);

        if (instance is null)
            return;

        // Пока удаляем запись из каталога сборок, но сохраняем
        // игровые файлы, чтобы случайно не потерять данные.
        await repository.DeleteAsync(id, cancellationToken);
    }
}
