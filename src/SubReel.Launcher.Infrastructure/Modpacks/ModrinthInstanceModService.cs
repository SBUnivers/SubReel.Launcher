using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Text.Json;
using SubReel.Launcher.Abstractions.Modpacks;
using SubReel.Launcher.Core.Instances;
using SubReel.Launcher.Core.Models;

namespace SubReel.Launcher.Infrastructure.Modpacks;

/// <summary>Manages client-side Modrinth mods inside one validated game instance.</summary>
public sealed class ModrinthInstanceModService(HttpClient http, InstanceService instances, ICurseForgeApiClient? curseForge = null) : IInstanceModService
{
    private const long MaxModFileBytes = 512L * 1024 * 1024;
    private const long MaxTotalModFilesBytes = 1024L * 1024 * 1024;
    private const int MaxRequiredModCount = 32;
    private const int MaxCurseForgeModCount = 32;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<InstanceModSearchResult>> SearchAsync(
        MinecraftInstance instance, string query, CancellationToken cancellationToken = default, ModpackProvider provider = ModpackProvider.Modrinth)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ValidateModdedInstance(instance);
        if (provider == ModpackProvider.CurseForge)
        {
            var api = curseForge ?? throw new InvalidOperationException("Прокси CurseForge не настроен.");
            var result = await api.SearchAsync(new ModpackQuery(query.Trim(), instance.MinecraftVersion,
                instance.LoaderType.ToString().ToLowerInvariant(), Limit: 40), cancellationToken, searchMods: true);
            return result.Items.Select(item => new InstanceModSearchResult(item.Id, item.Slug, item.Title,
                item.Description, item.IconUrl, item.Downloads, item.Categories) { Provider = ModpackProvider.CurseForge }).ToArray();
        }
        var facets = new List<string[]> { new[] { "project_type:mod" }, new[] { $"versions:{instance.MinecraftVersion}" } };
        var loader = ToModrinthLoader(instance.LoaderType);
        if (loader is not null) facets.Add(new[] { $"categories:{loader}" });
        var uri = $"search?query={Uri.EscapeDataString(query.Trim())}&facets={Uri.EscapeDataString(JsonSerializer.Serialize(facets, JsonOptions))}&index=downloads&limit=40";
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array)
            return [];

        return hits.EnumerateArray().Select(hit =>
        {
            var id = ReadString(hit, "project_id") ?? ReadString(hit, "id");
            var slug = ReadString(hit, "slug");
            var title = ReadString(hit, "title");
            if (id is null || slug is null || title is null) return null;
            var categories = hit.TryGetProperty("categories", out var categoryArray) && categoryArray.ValueKind == JsonValueKind.Array
                ? categoryArray.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
                : [];
            return new InstanceModSearchResult(id, slug, title, ReadString(hit, "description") ?? string.Empty,
                ReadString(hit, "icon_url"), ReadLong(hit, "downloads"), categories);
        }).Where(item => item is not null).Select(item => item!).ToArray();
    }

    public Task<IReadOnlyList<InstalledInstanceMod>> GetInstalledAsync(
        MinecraftInstance instance, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(instance);
        var modsDirectory = GetModsDirectory(instance);
        if (!Directory.Exists(modsDirectory)) return Task.FromResult<IReadOnlyList<InstalledInstanceMod>>([]);
        EnsureNotReparsePoint(modsDirectory);
        var curseForgeMods = ReadCurseForgeManifest(instance);
        var installed = new List<InstalledInstanceMod>();
        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureNotReparsePoint(path);
            var name = Path.GetFileName(path);
            var enabled = name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
            if (!enabled && !name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) continue;
            var manifestName = enabled ? name : name[..^".disabled".Length];
            if (curseForgeMods.TryGetValue(manifestName, out var tracked))
                installed.Add(new InstalledInstanceMod(name, new FileInfo(path).Length, enabled,
                    CurrentProjectId: tracked.ProjectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    CurrentFileId: tracked.FileId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Provider: ModpackProvider.CurseForge));
            else
                installed.Add(new InstalledInstanceMod(name, new FileInfo(path).Length, enabled));
        }
        return Task.FromResult<IReadOnlyList<InstalledInstanceMod>>(installed.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task InstallAsync(MinecraftInstance instance, InstanceModSearchResult mod,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(mod);
        ValidateModdedInstance(instance);
        if (mod.Provider == ModpackProvider.CurseForge)
        {
            await InstallCurseForgeModAsync(instance, mod, cancellationToken);
            return;
        }
        var versions = await GetCompatibleVersionsAsync(mod.Id, instance, cancellationToken);
        if (versions.Count == 0) throw new InvalidOperationException("Для этой версии Minecraft и загрузчика нет совместимой версии мода.");

        var plan = new Dictionary<string, ModFile>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await AddVersionAndRequiredDependenciesAsync(versions[0], instance, plan, visited, cancellationToken);
        await InstallPlanAsync(instance, plan, replaceFileName: null, cancellationToken);
    }

    private async Task InstallCurseForgeModAsync(MinecraftInstance instance, InstanceModSearchResult mod, CancellationToken ct)
    {
        if (!int.TryParse(mod.Id, out var projectId) || projectId <= 0)
            throw new InvalidDataException("Некорректный идентификатор мода CurseForge.");
        var api = curseForge ?? throw new InvalidOperationException("Прокси CurseForge не настроен.");
        var files = await api.GetFilesAsync(projectId, limit: 50, cancellationToken: ct);
        var file = SelectCurseForgeFile(files, instance);
        if (file is null)
            throw new InvalidOperationException("CurseForge не нашёл доступный JAR для этой версии Minecraft и загрузчика.");

        var plan = new List<CurseForgeInstallEntry>();
        var visited = new HashSet<int>();
        await AddCurseForgeFileAndDependenciesAsync(instance, projectId, file, plan, visited, ct);
        await EnsureNoReverseCurseForgeConflictsAsync(instance, plan, ct);
        await InstallCurseForgePlanAsync(instance, plan, ct);
    }

    private async Task AddCurseForgeFileAndDependenciesAsync(MinecraftInstance instance, int projectId,
        CurseForgeModpackFile file, List<CurseForgeInstallEntry> plan, HashSet<int> visited, CancellationToken ct)
    {
        if (!visited.Add(projectId)) return;
        if (visited.Count > MaxCurseForgeModCount)
            throw new InvalidDataException($"У мода слишком много зависимостей CurseForge (лимит {MaxCurseForgeModCount}).");
        var knownCurseForgeProjects = ReadCurseForgeManifest(instance).Values.Select(item => item.ProjectId).ToHashSet();
        foreach (var conflict in file.Dependencies.Where(item => item.RelationType == 5))
        {
            if (knownCurseForgeProjects.Contains(conflict.ModId) || visited.Contains(conflict.ModId) ||
                plan.Any(item => item.ProjectId == conflict.ModId))
                throw new InvalidOperationException($"CurseForge сообщает о конфликте с уже установленным или выбранным модом {conflict.ModId}. Удали или отключи конфликтующий мод и повтори установку.");
        }
        var api = curseForge ?? throw new InvalidOperationException("Прокси CurseForge не настроен.");
        foreach (var dependency in file.Dependencies.Where(item => item.RelationType is 1 or 3))
        {
            ct.ThrowIfCancellationRequested();
            if (visited.Contains(dependency.ModId)) continue;
            CurseForgeModpackFile? dependencyFile;
            if (dependency.FileId > 0)
            {
                var matches = await api.GetFilesByIdsAsync([dependency.FileId], ct);
                dependencyFile = matches.FirstOrDefault(item => item.Id == dependency.FileId && item.ProjectId == dependency.ModId);
                if (dependencyFile is not null && !IsCompatibleCurseForgeFile(dependencyFile, instance))
                    throw new InvalidDataException($"Закреплённый файл обязательной зависимости CurseForge {dependency.ModId} несовместим с этой сборкой.");
            }
            else
            {
                var candidates = await api.GetFilesAsync(dependency.ModId, limit: 50, cancellationToken: ct);
                dependencyFile = SelectCurseForgeFile(candidates, instance);
            }

            if (dependencyFile is null)
                throw new InvalidDataException($"Не удалось найти совместимую обязательную зависимость CurseForge {dependency.ModId}.");
            await AddCurseForgeFileAndDependenciesAsync(instance, dependency.ModId, dependencyFile, plan, visited, ct);
        }

        if (!IsCompatibleCurseForgeFile(file, instance))
            throw new InvalidDataException($"Файл {file.FileName} несовместим с выбранной сборкой.");
        var existing = plan.FirstOrDefault(item => item.File.FileName.Equals(file.FileName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            var oldHash = existing.File.Hashes.First(hash => hash.Algorithm == 1).Value;
            var newHash = file.Hashes.First(hash => hash.Algorithm == 1).Value;
            if (!oldHash.Equals(newHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Разные файлы CurseForge используют одинаковое имя: {file.FileName}");
            return;
        }
        plan.Add(new CurseForgeInstallEntry(projectId, file));
    }

    private async Task InstallCurseForgePlanAsync(MinecraftInstance instance,
        IReadOnlyList<CurseForgeInstallEntry> plan, CancellationToken ct)
    {
        var modsDirectory = GetModsDirectory(instance);
        Directory.CreateDirectory(modsDirectory);
        EnsureNotReparsePoint(modsDirectory);
        var toDownload = new List<(CurseForgeInstallEntry Entry, string Temp, string Target)>();
        long totalBytes = 0;
        foreach (var entry in plan)
        {
            ct.ThrowIfCancellationRequested();
            var target = GetOwnedModPath(instance, entry.File.FileName);
            var disabled = GetOwnedModPath(instance, entry.File.FileName + ".disabled");
            var existingPath = File.Exists(target) ? target : File.Exists(disabled) ? disabled : null;
            if (existingPath is not null)
            {
                var existingHash = await ComputeSha1Async(existingPath, ct);
                var expectedHash = entry.File.Hashes.First(hash => hash.Algorithm == 1).Value;
                if (existingHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) continue;
                throw new IOException($"Файл {entry.File.FileName} уже занят другой версией. Он не был изменён.");
            }
            totalBytes = checked(totalBytes + entry.File.FileLength);
            if (totalBytes > MaxTotalModFilesBytes)
                throw new InvalidDataException("Совокупный размер мода и обязательных зависимостей превышает 1 ГБ.");
            toDownload.Add((entry, Path.Combine(modsDirectory, $".{Guid.NewGuid():N}.download"), target));
        }

        var api = curseForge ?? throw new InvalidOperationException("Прокси CurseForge не настроен.");
        var committed = new List<(CurseForgeInstallEntry Entry, string Target)>();
        try
        {
            long downloaded = 0;
            foreach (var item in toDownload)
            {
                ct.ThrowIfCancellationRequested();
                long fileLength;
                await using (var output = new FileStream(item.Temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await using var limited = new LimitedWriteStream(output, Math.Min(MaxModFileBytes, MaxTotalModFilesBytes - downloaded));
                    await api.DownloadFileAsync(item.Entry.ProjectId, item.Entry.File.Id, limited, cancellationToken: ct);
                    await output.FlushAsync(ct);
                    fileLength = output.Length;
                }
                if (fileLength != item.Entry.File.FileLength)
                    throw new InvalidDataException($"Размер файла CurseForge {item.Entry.File.FileName} не совпал с каталогом.");
                var expectedHash = item.Entry.File.Hashes.First(hash => hash.Algorithm == 1).Value;
                if (!(await ComputeSha1Async(item.Temp, ct)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Контрольная сумма CurseForge для {item.Entry.File.FileName} не совпала.");
                downloaded = checked(downloaded + fileLength);
            }

            foreach (var item in toDownload)
            {
                ct.ThrowIfCancellationRequested();
                File.Move(item.Temp, item.Target);
                committed.Add((item.Entry, item.Target));
            }
            if (committed.Count > 0)
                UpdateCurseForgeManifest(instance, mods =>
                {
                    foreach (var (entry, _) in committed)
                        mods[entry.File.FileName] = new ManagedCurseForgeMod(entry.ProjectId, entry.File.Id, entry.File.DisplayName);
                });
        }
        catch
        {
            foreach (var (_, target) in committed) TryDelete(target);
            throw;
        }
        finally { foreach (var item in toDownload) TryDelete(item.Temp); }
    }

    private async Task<InstalledInstanceMod> CheckCurseForgeUpdateAsync(MinecraftInstance instance,
        InstalledInstanceMod mod, string installedSha1, CancellationToken ct)
    {
        if (!int.TryParse(mod.CurrentProjectId, out var projectId) || projectId <= 0 ||
            !int.TryParse(mod.CurrentFileId, out var currentFileId) || currentFileId <= 0)
            return mod;
        var api = curseForge ?? throw new InvalidOperationException("Прокси CurseForge не настроен.");
        var files = await api.GetFilesAsync(projectId, limit: 50, cancellationToken: ct);
        var latest = SelectCurseForgeFile(files, instance);
        if (latest is null) return mod;
        var latestHash = latest.Hashes.First(hash => hash.Algorithm == 1).Value;
        if (latest.Id == currentFileId || latestHash.Equals(installedSha1, StringComparison.OrdinalIgnoreCase)) return mod;
        return mod with
        {
            UpdateProjectId = projectId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            UpdateVersionId = latest.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            UpdateVersionNumber = latest.DisplayName,
            Provider = ModpackProvider.CurseForge
        };
    }

    private async Task UpdateCurseForgeModAsync(MinecraftInstance instance, InstalledInstanceMod mod, CancellationToken ct)
    {
        if (!mod.IsEnabled || !int.TryParse(mod.UpdateProjectId, out var projectId) || projectId <= 0 ||
            !int.TryParse(mod.UpdateVersionId, out var fileId) || fileId <= 0)
            throw new InvalidOperationException("Сначала проверь наличие обновления для этого JAR.");
        var oldPath = GetOwnedModPath(instance, mod.FileName);
        if (!File.Exists(oldPath)) throw new FileNotFoundException("Установленный JAR не найден.", mod.FileName);
        var api = curseForge ?? throw new InvalidOperationException("Прокси CurseForge не настроен.");
        var files = await api.GetFilesAsync(projectId, limit: 50, cancellationToken: ct);
        var update = files.FirstOrDefault(candidate => candidate.Id == fileId);
        if (update is null || !ReferenceEquals(SelectCurseForgeFile([update], instance), update))
            throw new InvalidDataException("Обновление CurseForge больше недоступно или несовместимо с этой сборкой.");
        await ReplaceCurseForgeFileAsync(instance, projectId, update, oldPath, mod.FileName, ct);
    }

    private async Task ReplaceCurseForgeFileAsync(MinecraftInstance instance, int projectId,
        CurseForgeModpackFile file, string oldPath, string oldFileName, CancellationToken ct)
    {
        var api = curseForge ?? throw new InvalidOperationException("Прокси CurseForge не настроен.");
        var directory = GetModsDirectory(instance);
        var target = GetOwnedModPath(instance, file.FileName);
        if (!string.Equals(target, oldPath, StringComparison.OrdinalIgnoreCase) && (File.Exists(target) || File.Exists(target + ".disabled")))
            throw new IOException($"Обновление занято другим файлом: {file.FileName}");
        var temp = Path.Combine(directory, $".{Guid.NewGuid():N}.download");
        var backup = Path.Combine(directory, $".{Guid.NewGuid():N}.backup");
        var movedOld = false;
        var movedNew = false;
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await api.DownloadFileAsync(projectId, file.Id, output, cancellationToken: ct);
                await output.FlushAsync(ct);
                if (output.Length != file.FileLength)
                    throw new InvalidDataException("Размер файла CurseForge не совпал с каталогом.");
            }
            var expected = file.Hashes.First(hash => hash.Algorithm == 1).Value;
            if (!(await ComputeSha1Async(temp, ct)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма CurseForge не совпала.");
            ct.ThrowIfCancellationRequested();
            File.Move(oldPath, backup);
            movedOld = true;
            File.Move(temp, target);
            movedNew = true;
            UpdateCurseForgeManifest(instance, mods =>
            {
                mods.Remove(oldFileName);
                mods[file.FileName] = new ManagedCurseForgeMod(projectId, file.Id, file.DisplayName);
            });
            TryDelete(backup);
            movedOld = false;
        }
        catch
        {
            if (movedNew) TryDelete(target);
            if (movedOld && File.Exists(backup) && !File.Exists(oldPath))
            {
                try { File.Move(backup, oldPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static CurseForgeModpackFile? SelectCurseForgeFile(IEnumerable<CurseForgeModpackFile> files, MinecraftInstance instance)
    {
        return files.Where(candidate => IsCompatibleCurseForgeFile(candidate, instance))
            .OrderByDescending(candidate => candidate.PublishedAt)
            .FirstOrDefault();
    }

    private static bool IsCompatibleCurseForgeFile(CurseForgeModpackFile file, MinecraftInstance instance) =>
        file.IsAvailable && IsSafeJarName(file.FileName) && file.FileLength is > 0 and <= MaxModFileBytes &&
        file.GameVersions.Contains(instance.MinecraftVersion, StringComparer.OrdinalIgnoreCase) &&
        file.GameVersions.Contains(instance.LoaderType.ToString(), StringComparer.OrdinalIgnoreCase) &&
        file.Hashes.Any(hash => hash.Algorithm == 1 && IsSha1(hash.Value));

    private async Task EnsureNoReverseCurseForgeConflictsAsync(MinecraftInstance instance,
        IReadOnlyList<CurseForgeInstallEntry> plan, CancellationToken ct)
    {
        var existing = ReadCurseForgeManifest(instance).Values
            .Where(item => item.FileId > 0 && plan.All(entry => entry.ProjectId != item.ProjectId))
            .ToArray();
        if (existing.Length == 0) return;
        var api = curseForge ?? throw new InvalidOperationException("Прокси CurseForge не настроен.");
        var plannedProjectIds = plan.Select(item => item.ProjectId).ToHashSet();
        foreach (var batch in existing.Select(item => item.FileId).Distinct().Chunk(50))
        {
            ct.ThrowIfCancellationRequested();
            var files = await api.GetFilesByIdsAsync(batch, ct);
            foreach (var file in files)
            {
                var conflict = file.Dependencies.FirstOrDefault(item => item.RelationType == 5 && plannedProjectIds.Contains(item.ModId));
                if (conflict is not null)
                    throw new InvalidOperationException($"У установленного CurseForge-мода {file.ProjectId} есть конфликт с модом {conflict.ModId}. Удали или отключи его и повтори установку.");
            }
        }
    }

    private Dictionary<string, ManagedCurseForgeMod> ReadCurseForgeManifest(MinecraftInstance instance)
    {
        var path = GetCurseForgeManifestPath(instance);
        if (!File.Exists(path)) return new Dictionary<string, ManagedCurseForgeMod>(StringComparer.OrdinalIgnoreCase);
        EnsureNotReparsePoint(path);
        try
        {
            var result = JsonSerializer.Deserialize<Dictionary<string, ManagedCurseForgeMod>>(File.ReadAllText(path), JsonOptions);
            return result is null ? new Dictionary<string, ManagedCurseForgeMod>(StringComparer.OrdinalIgnoreCase) :
                new Dictionary<string, ManagedCurseForgeMod>(result, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex) { throw new InvalidDataException("Не удалось прочитать список установленных CurseForge-модов.", ex); }
    }

    private void UpdateCurseForgeManifest(MinecraftInstance instance, Action<Dictionary<string, ManagedCurseForgeMod>> update)
    {
        var path = GetCurseForgeManifestPath(instance);
        var mods = ReadCurseForgeManifest(instance);
        update(mods);
        if (mods.Count == 0 && !File.Exists(path)) return;
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(mods, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally { TryDelete(temp); }
    }

    private string GetCurseForgeManifestPath(MinecraftInstance instance) =>
        Path.Combine(GetModsDirectory(instance), ".subreel-curseforge-mods.json");

    public async Task<IReadOnlyList<InstalledInstanceMod>> CheckUpdatesAsync(
        MinecraftInstance instance, CancellationToken cancellationToken = default)
    {
        ValidateModdedInstance(instance);
        var installed = await GetInstalledAsync(instance, cancellationToken);
        var results = new List<InstalledInstanceMod>(installed.Count);
        foreach (var mod in installed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!mod.IsEnabled || !mod.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                results.Add(mod);
                continue;
            }

            var path = GetOwnedModPath(instance, mod.FileName);
            var hash = await ComputeSha1Async(path, cancellationToken);
            if (mod.Provider == ModpackProvider.CurseForge)
            {
                var update = await CheckCurseForgeUpdateAsync(instance, mod, hash, cancellationToken);
                results.Add(update);
                continue;
            }
            var updateRequest = new
            {
                loaders = new[] { ToModrinthLoader(instance.LoaderType) },
                game_versions = new[] { instance.MinecraftVersion },
                version_types = new[] { "release" }
            };
            using var response = await http.PostAsJsonAsync(
                $"version_file/{hash}/update?algorithm=sha1", updateRequest, JsonOptions, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                results.Add(mod);
                continue;
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var projectId = ReadString(document.RootElement, "project_id");
            var updateVersionId = ReadString(document.RootElement, "id");
            if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(updateVersionId))
            {
                results.Add(mod);
                continue;
            }
            var latestFile = SelectPrimaryFile(document.RootElement);
            if (latestFile is null || latestFile.Sha1.Equals(hash, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(mod);
                continue;
            }

            results.Add(mod with
            {
                UpdateProjectId = projectId,
                UpdateVersionId = updateVersionId,
                UpdateVersionNumber = ReadString(document.RootElement, "version_number"),
                Provider = ModpackProvider.Modrinth
            });
        }
        return results;
    }

    public async Task UpdateAsync(MinecraftInstance instance, InstalledInstanceMod mod,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mod);
        ValidateModdedInstance(instance);
        if (mod.Provider == ModpackProvider.CurseForge)
        {
            await UpdateCurseForgeModAsync(instance, mod, cancellationToken);
            return;
        }
        if (!mod.IsEnabled || string.IsNullOrWhiteSpace(mod.UpdateProjectId) || string.IsNullOrWhiteSpace(mod.UpdateVersionId))
            throw new InvalidOperationException("Сначала проверь наличие обновления для этого JAR.");

        var path = GetOwnedModPath(instance, mod.FileName);
        if (!File.Exists(path)) throw new FileNotFoundException("Установленный JAR не найден.", mod.FileName);
        var version = await GetVersionAsync(mod.UpdateVersionId, cancellationToken);
        if (!CompatibleVersion(version, instance))
            throw new InvalidDataException("Новая версия больше не совместима с этим инстансом.");
        var plan = new Dictionary<string, ModFile>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await AddVersionAndRequiredDependenciesAsync(version, instance, plan, visited, cancellationToken);
        await InstallPlanAsync(instance, plan, mod.FileName, cancellationToken);
    }

    public Task RemoveAsync(MinecraftInstance instance, string fileName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetOwnedModPath(instance, fileName);
        if (File.Exists(path)) File.Delete(path);
        var manifestName = fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".disabled".Length] : fileName;
        UpdateCurseForgeManifest(instance, mods => mods.Remove(manifestName));
        return Task.CompletedTask;
    }

    public Task SetEnabledAsync(MinecraftInstance instance, string fileName, bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = GetOwnedModPath(instance, fileName);
        if (!File.Exists(source)) throw new FileNotFoundException("Файл мода не найден.", fileName);
        var currentlyEnabled = fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);
        if (currentlyEnabled == enabled) return Task.CompletedTask;
        var destinationName = enabled ? fileName[..^".disabled".Length] : fileName + ".disabled";
        var destination = GetOwnedModPath(instance, destinationName);
        if (File.Exists(destination)) throw new IOException($"Нельзя переключить мод: файл {destinationName} уже существует.");
        File.Move(source, destination);
        var sourceManifestName = currentlyEnabled ? fileName : fileName[..^".disabled".Length];
        var destinationManifestName = enabled ? destinationName : destinationName[..^".disabled".Length];
        UpdateCurseForgeManifest(instance, mods =>
        {
            if (mods.Remove(sourceManifestName, out var tracked)) mods[destinationManifestName] = tracked;
        });
        return Task.CompletedTask;
    }

    private async Task InstallPlanAsync(MinecraftInstance instance, Dictionary<string, ModFile> plan,
        string? replaceFileName, CancellationToken cancellationToken)
    {
        var modsDirectory = GetModsDirectory(instance);
        Directory.CreateDirectory(modsDirectory);
        EnsureNotReparsePoint(modsDirectory);
        var oldPath = replaceFileName is null ? null : GetOwnedModPath(instance, replaceFileName);
        if (oldPath is not null && !File.Exists(oldPath)) throw new FileNotFoundException("Исходный JAR для обновления не найден.", replaceFileName);

        var toDownload = new List<ModFile>();
        foreach (var file in plan.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(modsDirectory, file.FileName);
            var disabledTarget = target + ".disabled";
            if (oldPath is not null && string.Equals(target, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                toDownload.Add(file);
                continue;
            }
            if (Directory.Exists(target)) throw new IOException($"Путь файла занят папкой: {file.FileName}");
            var existingPath = File.Exists(target) ? target : File.Exists(disabledTarget) ? disabledTarget : null;
            if (existingPath is null)
            {
                toDownload.Add(file);
                continue;
            }
            EnsureNotReparsePoint(existingPath);
            var existingHash = await ComputeSha1Async(existingPath, cancellationToken);
            if (!existingHash.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Файл {file.FileName} уже установлен с другой версией; обновление остановлено, чтобы не затронуть другой мод.");
        }

        if (toDownload.Count == 0 && oldPath is null) return;
        var staged = new List<(string Temp, string Target)>();
        var committed = new List<string>();
        string? backupPath = null;
        long totalDownloaded = 0;
        try
        {
            foreach (var file in toDownload)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(modsDirectory, file.FileName);
                var temp = Path.Combine(modsDirectory, $".{Guid.NewGuid():N}.download");
                staged.Add((temp, target));
                var remainingBytes = MaxTotalModFilesBytes - totalDownloaded;
                if (remainingBytes <= 0) throw new InvalidDataException("Общий размер модов и обязательных зависимостей превышает лимит 1 ГБ.");
                totalDownloaded += await DownloadAndVerifyAsync(file, temp, remainingBytes, cancellationToken);
            }

            if (oldPath is not null)
            {
                backupPath = Path.Combine(modsDirectory, $".{Guid.NewGuid():N}.backup");
                File.Move(oldPath, backupPath);
            }
            foreach (var item in staged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(item.Temp, item.Target);
                committed.Add(item.Target);
            }
            if (backupPath is not null) File.Delete(backupPath);
        }
        catch
        {
            foreach (var (temp, _) in staged) TryDelete(temp);
            foreach (var path in committed) TryDelete(path);
            if (backupPath is not null && File.Exists(backupPath) && oldPath is not null)
            {
                try { File.Move(backupPath, oldPath, overwrite: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    private async Task<JsonElement> GetVersionAsync(string versionId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"version/{Uri.EscapeDataString(versionId)}", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return document.RootElement.Clone();
    }

    private static async Task<string> ComputeSha1Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA1.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task AddVersionAndRequiredDependenciesAsync(JsonElement version, MinecraftInstance instance,
        Dictionary<string, ModFile> plan, HashSet<string> visited, CancellationToken ct)
    {
        var versionId = ReadString(version, "id");
        if (string.IsNullOrWhiteSpace(versionId) || !visited.Add(versionId)) return;
        if (visited.Count > MaxRequiredModCount)
            throw new InvalidDataException($"У мода слишком много обязательных зависимостей (лимит {MaxRequiredModCount}).");
        if (!CompatibleVersion(version, instance))
            throw new InvalidDataException("Обязательная зависимость не совместима с версией Minecraft и загрузчиком сборки.");
        var file = SelectPrimaryFile(version);
        if (file is null) throw new InvalidDataException("У версии мода нет JAR-файла для загрузки.");
        if (plan.TryGetValue(file.FileName, out var existing) && existing.Url != file.Url)
            throw new InvalidDataException($"Несколько модов требуют файл с одинаковым именем: {file.FileName}");
        plan[file.FileName] = file;

        if (!version.TryGetProperty("dependencies", out var dependencies) || dependencies.ValueKind != JsonValueKind.Array)
            return;
        foreach (var dependency in dependencies.EnumerateArray())
        {
            if (!string.Equals(ReadString(dependency, "dependency_type"), "required", StringComparison.OrdinalIgnoreCase)) continue;
            ct.ThrowIfCancellationRequested();
            var dependencyVersionId = ReadString(dependency, "version_id");
            var projectId = ReadString(dependency, "project_id");
            if (string.IsNullOrWhiteSpace(dependencyVersionId) && string.IsNullOrWhiteSpace(projectId)) continue;
            JsonDocument? dependencyDocument = null;
            try
            {
                if (!string.IsNullOrWhiteSpace(dependencyVersionId))
                {
                    using var response = await http.GetAsync($"version/{Uri.EscapeDataString(dependencyVersionId)}", ct);
                    response.EnsureSuccessStatusCode();
                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    dependencyDocument = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                }
                else
                {
                    var compatible = await GetCompatibleVersionsAsync(projectId!, instance, ct);
                    if (compatible.Count == 0) throw new InvalidDataException($"Не найдена совместимая обязательная зависимость {projectId}.");
                    await AddVersionAndRequiredDependenciesAsync(compatible[0], instance, plan, visited, ct);
                    continue;
                }
                await AddVersionAndRequiredDependenciesAsync(dependencyDocument.RootElement, instance, plan, visited, ct);
            }
            finally { dependencyDocument?.Dispose(); }
        }
    }

    private async Task<IReadOnlyList<JsonElement>> GetCompatibleVersionsAsync(string projectId, MinecraftInstance instance, CancellationToken ct)
    {
        var loader = ToModrinthLoader(instance.LoaderType)!;
        var loaders = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader }));
        var gameVersions = Uri.EscapeDataString(JsonSerializer.Serialize(new[] { instance.MinecraftVersion }));
        using var response = await http.GetAsync($"project/{Uri.EscapeDataString(projectId)}/version?loaders={loaders}&game_versions={gameVersions}", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
        return document.RootElement.EnumerateArray()
            .Where(version => CompatibleVersion(version, instance))
            .Select(version => version.Clone())
            .OrderBy(version => VersionChannelRank(ReadString(version, "version_type")))
            .ThenByDescending(version => ReadString(version, "date_published"), StringComparer.Ordinal)
            .ToArray();
    }

    private static bool CompatibleVersion(JsonElement version, MinecraftInstance instance)
    {
        var gameVersions = ReadStringArray(version, "game_versions");
        var loaders = ReadStringArray(version, "loaders");
        return gameVersions.Contains(instance.MinecraftVersion, StringComparer.OrdinalIgnoreCase) &&
               loaders.Contains(ToModrinthLoader(instance.LoaderType), StringComparer.OrdinalIgnoreCase);
    }

    private static int VersionChannelRank(string? type) => type?.ToLowerInvariant() switch
    {
        "release" => 0,
        "beta" => 1,
        _ => 2
    };

    private async Task<long> DownloadAndVerifyAsync(ModFile file, string target, long remainingBytes, CancellationToken ct)
    {
        var fileLimit = Math.Min(MaxModFileBytes, remainingBytes);
        using var response = await http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > fileLimit)
            throw new InvalidDataException($"Файл {file.FileName} превышает лимит размера загрузки.");
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, ct);
            if (count == 0) break;
            total += count;
            if (total > fileLimit) throw new InvalidDataException($"Файл {file.FileName} превышает лимит размера загрузки.");
            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), ct);
        }
        await output.FlushAsync(ct);
        var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!actual.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Контрольная сумма {file.FileName} не совпала с Modrinth.");
        return total;
    }

    private ModFile? SelectPrimaryFile(JsonElement version)
    {
        if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return null;
        var candidates = files.EnumerateArray().Where(file =>
        {
            var name = ReadString(file, "filename");
            var url = ReadString(file, "url");
            var hashes = file.TryGetProperty("hashes", out var value) ? value : default;
            var sha1 = hashes.ValueKind == JsonValueKind.Object ? ReadString(hashes, "sha1") : null;
            return IsSafeJarName(name) && IsSafeDownloadUrl(url) && !string.IsNullOrWhiteSpace(sha1);
        }).ToArray();
        var selected = candidates.FirstOrDefault(file => file.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True);
        if (selected.ValueKind != JsonValueKind.Object) selected = candidates.FirstOrDefault();
        if (selected.ValueKind != JsonValueKind.Object) return null;
        var selectedHashes = selected.GetProperty("hashes");
        return new ModFile(ReadString(selected, "filename")!, ReadString(selected, "url")!, ReadString(selectedHashes, "sha1")!);
    }

    private string GetModsDirectory(MinecraftInstance instance)
    {
        var gameDirectory = instances.GetValidatedGameDirectory(instance);
        return Path.Combine(gameDirectory, "mods");
    }

    private string GetOwnedModPath(MinecraftInstance instance, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!IsSafeJarName(fileName) && !fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Некорректное имя файла мода.", nameof(fileName));
        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
            throw new ArgumentException("Нельзя указывать путь вместо имени файла.", nameof(fileName));
        var directory = GetModsDirectory(instance);
        if (Directory.Exists(directory)) EnsureNotReparsePoint(directory);
        var path = Path.GetFullPath(Path.Combine(directory, fileName));
        var expectedParent = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Файл находится вне папки модов выбранной сборки.");
        if (File.Exists(path)) EnsureNotReparsePoint(path);
        return path;
    }

    private static void ValidateModdedInstance(MinecraftInstance instance)
    {
        if (instance.LoaderType == MinecraftLoaderType.Vanilla)
            throw new InvalidOperationException("Для установки модов сначала выбери Fabric, Forge, NeoForge или Quilt.");
        if (string.IsNullOrWhiteSpace(instance.MinecraftVersion)) throw new InvalidOperationException("У сборки не указана версия Minecraft.");
    }

    private static string? ToModrinthLoader(MinecraftLoaderType loader) => loader switch
    {
        MinecraftLoaderType.Fabric => "fabric",
        MinecraftLoaderType.Forge => "forge",
        MinecraftLoaderType.NeoForge => "neoforge",
        MinecraftLoaderType.Quilt => "quilt",
        _ => null
    };

    private static bool IsSafeJarName(string? name) => !string.IsNullOrWhiteSpace(name) &&
        string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal) &&
        name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) && name.Length <= 180 &&
        !name.Contains(':') && !name.Contains('/') && !name.Contains('\\');

    private static bool IsSha1(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    private static bool IsSafeDownloadUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase);

    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Операция с символической ссылкой запрещена: {path}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : 0;
    private static string[] ReadStringArray(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];

    private sealed record ManagedCurseForgeMod(int ProjectId, int FileId, string VersionName);
    private sealed record CurseForgeInstallEntry(int ProjectId, CurseForgeModpackFile File);
    private sealed record ModFile(string FileName, string Url, string Sha1);
}
