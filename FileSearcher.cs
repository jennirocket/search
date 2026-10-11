using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Compression;

class FileSearcher
{
    private readonly string _indexPath;
    private readonly string _indexDirectory;
    private readonly string _watchLogPath;
    private readonly bool _quiet;
    private readonly object _indexLock = new();
    private Dictionary<string, List<string>> _fileIndex = new();

    private static readonly string[] ExcludedPathFragments =
    {
        @"\$RECYCLE.BIN",
        @"\System Volume Information",
        @"\hiberfil.sys",
        @"\pagefile.sys",
        @"\ProgramData\Temp",
        @"\Windows\Temp",
        @"\Windows\SoftwareDistribution\Download",
        @"\Windows\System32",
        @"\Windows\WinSxS",
        @"\Windows\Logs",
        @"\.git",
        @"\node_modules",
        @"\AppData\Local\Temp",
        @"\AppData\Local\Microsoft\Windows\INetCache",
        @"\AppData\Roaming\FileSearch",
    };

    public FileSearcher(bool quiet = false)
    {
        // Store index in user's AppData
        var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileSearch");
        Directory.CreateDirectory(appDataPath);
        _indexDirectory = appDataPath;
        _indexPath = Path.Combine(appDataPath, "index.json");
        _watchLogPath = Path.Combine(appDataPath, "watch.log");
        _quiet = quiet;

        LoadIndex();
    }

    public async Task<List<string>> SearchAsync(string pattern, bool useRegex = false, string? directory = null)
    {
        var results = new List<string>();

        lock (_indexLock)
        {
            if (_fileIndex.Count == 0)
            {
                WriteStatus("Index is empty. Run 'search --update-index' to build it.");
                return results;
            }
        }

        await Task.Run(() =>
        {
            // Flatten all files from all drives
            List<string> allFiles;
            lock (_indexLock)
            {
                allFiles = _fileIndex
                    .SelectMany(kvp => kvp.Value.Select(f => Path.Combine(kvp.Key, f)))
                    .ToList();
            }

            // Filter by directory if specified
            if (!string.IsNullOrWhiteSpace(directory))
            {
                // Normalize directory path for comparison
                var normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                allFiles = allFiles.Where(f => f.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (useRegex)
            {
                try
                {
                    var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                    results = allFiles.Where(f => regex.IsMatch(Path.GetFileName(f))).ToList();
                }
                catch (RegexParseException ex)
                {
                    throw new Exception($"Invalid regex pattern: {ex.Message}");
                }
            }
            else
            {
                // Fuzzy search - convert pattern to lowercase for case-insensitive matching
                var lowerPattern = pattern.ToLower();
                
                // Exact filename matches first
                var exactMatches = allFiles
                    .Where(f => Path.GetFileName(f).Equals(pattern, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f)
                    .ToList();

                // Wildcard matches
                var wildcardMatches = allFiles
                    .Where(f => PathMatches(Path.GetFileName(f), lowerPattern))
                    .Except(exactMatches)
                    .OrderBy(f => f)
                    .ToList();

                // Fuzzy matches (partial)
                var fuzzyMatches = allFiles
                    .Where(f => FuzzyMatch(Path.GetFileName(f), lowerPattern))
                    .Except(exactMatches)
                    .Except(wildcardMatches)
                    .OrderBy(f => f)
                    .ToList();

                results = exactMatches.Concat(wildcardMatches).Concat(fuzzyMatches).ToList();
            }
        });

        return results;
    }

    public async Task BuildIndexAsync(string? specificDrive = null)
    {
        var drives = specificDrive != null
            ? new[] { NormalizeDrive(specificDrive) }
            : DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToArray();

        var indexedDrives = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in drives)
        {
            WriteStatus($"Indexing {drive}...");
            var files = new List<string>();
            
            await Task.Run(() =>
            {
                try
                {
                    IndexDirectory(drive, files, drive);
                }
                catch (UnauthorizedAccessException ex)
                {
                    WriteWarning($"Access denied: {ex.Message}");
                }
            });

            indexedDrives[drive] = files;
            WriteStatus($"  Found {files.Count} files on {drive}");
        }

        Dictionary<string, List<string>> snapshot;
        lock (_indexLock)
        {
            if (specificDrive == null)
            {
                _fileIndex = indexedDrives;
            }
            else
            {
                foreach (var (drive, files) in indexedDrives)
                    _fileIndex[drive] = files;
            }

            snapshot = _fileIndex.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToList(),
                StringComparer.OrdinalIgnoreCase);
        }

        SaveIndex(snapshot);
    }

    public async Task WatchAsync(string? specificDrive = null)
    {
        using var watcherLock = new Mutex(false, "Global\\FileSearch_Watcher");
        if (!watcherLock.WaitOne(0))
        {
            WriteStatus("A watcher is already running for this install.");
            return;
        }

        var drives = specificDrive == null
            ? DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToArray()
            : new[] { NormalizeDrive(specificDrive) };

        var changes = new ConcurrentQueue<FileChange>();
        using var signal = new SemaphoreSlim(0);
        var watchers = new List<FileSystemWatcher>();

        void Enqueue(FileChange change)
        {
            changes.Enqueue(change);
            signal.Release();
        }

        try
        {
            foreach (var drive in drives)
            {
                var watcher = new FileSystemWatcher(drive)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                    EnableRaisingEvents = false
                };
                watcher.Created += (_, e) => Enqueue(new FileChange(drive, WatcherChangeTypes.Created, e.FullPath, null));
                watcher.Deleted += (_, e) => Enqueue(new FileChange(drive, WatcherChangeTypes.Deleted, e.FullPath, null));
                watcher.Renamed += (_, e) => Enqueue(new FileChange(drive, WatcherChangeTypes.Renamed, e.FullPath, e.OldFullPath));
                watcher.Error += (_, _) => Enqueue(new FileChange(drive, 0, drive, null));
                watchers.Add(watcher);
            }

            await BuildIndexAsync(specificDrive);

            foreach (var watcher in watchers)
            {
                watcher.EnableRaisingEvents = true;
                WriteStatus($"Watching {watcher.Path} for file changes...");
            }

            while (true)
            {
                await signal.WaitAsync();
                await Task.Delay(250);
                while (signal.Wait(0)) { }

                var batch = new List<FileChange>();
                while (changes.TryDequeue(out var change))
                    batch.Add(change);

                var rescanDrives = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var change in batch)
                {
                    if (change.ChangeType == 0)
                    {
                        rescanDrives.Add(change.Drive);
                        continue;
                    }

                    ApplyChange(change);
                }

                foreach (var drive in rescanDrives)
                    await BuildIndexAsync(drive);

                if (batch.Count > 0)
                {
                    if (rescanDrives.Count == 0)
                        SaveIndex();
                    WriteStatus($"Applied {batch.Count} file change(s).");
                }
            }
        }
        finally
        {
            foreach (var watcher in watchers)
                watcher.Dispose();
        }
    }

    private void ApplyChange(FileChange change)
    {
        var relativePath = Path.GetRelativePath(change.Drive, change.FullPath);
        if (relativePath == "." || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            return;

        lock (_indexLock)
        {
            if (!_fileIndex.TryGetValue(change.Drive, out var files))
                return;

            if (change.ChangeType == WatcherChangeTypes.Renamed && change.OldFullPath != null)
            {
                ApplyRename(change.Drive, files, change.OldFullPath, change.FullPath);
            }
            else if (change.ChangeType == WatcherChangeTypes.Deleted)
            {
                RemovePathAndDescendants(files, relativePath);
            }
            else if (Directory.Exists(change.FullPath))
            {
                if (!IsSystemDirectory(change.FullPath))
                    AddDirectoryToIndex(change.FullPath, files, change.Drive);
            }
            else if (File.Exists(change.FullPath))
            {
                if (!IsSystemDirectory(change.FullPath) && !files.Contains(relativePath, StringComparer.OrdinalIgnoreCase))
                    files.Add(relativePath);
            }
            else
            {
                RemovePathAndDescendants(files, relativePath);
            }
        }
    }

    private void ApplyRename(string drive, List<string> files, string oldFullPath, string newFullPath)
    {
        var oldRelativePath = Path.GetRelativePath(drive, oldFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var newRelativePath = Path.GetRelativePath(drive, newFullPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var oldPrefix = oldRelativePath + Path.DirectorySeparatorChar;
        var hasIndexedDescendants = files.Any(file => file.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase));

        if (Directory.Exists(newFullPath))
        {
            if (IsSystemDirectory(newFullPath))
            {
                RemovePathAndDescendants(files, oldRelativePath);
                return;
            }

            if (hasIndexedDescendants)
            {
                for (var i = 0; i < files.Count; i++)
                {
                    if (files[i].StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var suffix = files[i].Length > oldRelativePath.Length
                            ? files[i][oldRelativePath.Length..]
                            : string.Empty;
                        files[i] = string.Concat(newRelativePath, suffix);
                    }
                }
            }
            else
            {
                AddDirectoryToIndex(newFullPath, files, drive);
            }
            RemovePathAndDescendants(files, oldRelativePath);
        }
        else
        {
            RemovePathAndDescendants(files, oldRelativePath);
            if (File.Exists(newFullPath) &&
                !IsSystemDirectory(newFullPath) &&
                !files.Contains(newRelativePath, StringComparer.OrdinalIgnoreCase))
            {
                files.Add(newRelativePath);
            }
        }
    }

    private static void RemovePathAndDescendants(List<string> files, string relativePath)
    {
        var prefix = relativePath + Path.DirectorySeparatorChar;
        files.RemoveAll(file =>
            file.Equals(relativePath, StringComparison.OrdinalIgnoreCase) ||
            file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private void AddDirectoryToIndex(string path, List<string> files, string driveRoot)
    {
        var discoveredFiles = new List<string>();
        IndexDirectory(path, discoveredFiles, driveRoot);

        var knownFiles = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        files.AddRange(discoveredFiles.Where(knownFiles.Add));
    }

    private static string NormalizeDrive(string drive)
    {
        drive = drive.Trim();
        if (drive.Length == 1 && char.IsLetter(drive[0]))
            return $"{drive}:\\";
        if (drive.Length == 2 && char.IsLetter(drive[0]) && drive[1] == ':')
            return $"{drive}\\";

        var root = Path.GetPathRoot(drive);
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException($"Invalid drive path: {drive}", nameof(drive));

        return root;
    }

    private readonly record struct FileChange(string Drive, WatcherChangeTypes ChangeType, string FullPath, string? OldFullPath);

    private void IndexDirectory(string path, List<string> files, string driveRoot)
    {
        try
        {
            var directory = new DirectoryInfo(path);
            
            // Skip certain system directories
            if (IsSystemDirectory(path))
                return;

            // Index files
            foreach (var file in directory.EnumerateFiles())
            {
                files.Add(file.FullName.Replace(driveRoot, "").TrimStart(Path.DirectorySeparatorChar));
            }

            // Recursively index subdirectories
            foreach (var subDir in directory.EnumerateDirectories())
            {
                try
                {
                    IndexDirectory(subDir.FullName, files, driveRoot);
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip directories we can't access
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip
        }
    }

    private bool IsSystemDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var indexRoot = Path.GetFullPath(_indexDirectory);
        if (IsSameOrDescendant(fullPath, indexRoot))
            return true;

        return ExcludedPathFragments.Any(fragment => ContainsPathFragment(fullPath, fragment));
    }

    private static bool IsSameOrDescendant(string path, string directory)
    {
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(directory);
        return path.Equals(normalizedDirectory, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(
                normalizedDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPathFragment(string path, string fragment)
    {
        var index = 0;
        while ((index = path.IndexOf(fragment, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = index + fragment.Length;
            var endsAtSegmentBoundary =
                end == path.Length ||
                path[end] == Path.DirectorySeparatorChar ||
                path[end] == Path.AltDirectorySeparatorChar;

            if (endsAtSegmentBoundary)
                return true;

            index = end;
        }

        return false;
    }

    private bool PathMatches(string filename, string pattern)
    {
        // Convert * to regex
        var regexPattern = Regex.Escape(pattern).Replace("\\*", ".*");
        return Regex.IsMatch(filename, regexPattern, RegexOptions.IgnoreCase);
    }

    private bool FuzzyMatch(string text, string pattern)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(pattern))
            return false;

        text = text.ToLower();
        pattern = pattern.ToLower();

        // First, check for substring match (highest relevance)
        if (text.Contains(pattern))
            return true;

        // For fuzzy matching, require at least 70% of pattern characters to match consecutively
        // and the pattern should cover a reasonable portion of the text
        int consecutiveMatches = 0;
        int maxConsecutive = 0;
        int patternIndex = 0;
        int textIndex = 0;

        // Find longest consecutive match
        while (textIndex < text.Length && patternIndex < pattern.Length)
        {
            if (text[textIndex] == pattern[patternIndex])
            {
                consecutiveMatches++;
                patternIndex++;
                if (consecutiveMatches > maxConsecutive)
                    maxConsecutive = consecutiveMatches;
            }
            else
            {
                consecutiveMatches = 0;
                // Allow skipping some characters in text, but not too many
                if (patternIndex == 0)
                    textIndex++; // Skip this character in text
                else
                    patternIndex = 0; // Reset pattern matching
            }
            textIndex++;
        }

        // Calculate match quality
        double consecutiveRatio = (double)maxConsecutive / pattern.Length;
        double coverageRatio = (double)pattern.Length / Math.Max(text.Length, pattern.Length);

        // Require at least 60% consecutive match AND reasonable coverage
        return consecutiveRatio >= 0.6 && coverageRatio >= 0.3;
    }

    private void SaveIndex()
    {
        Dictionary<string, List<string>> snapshot;
        lock (_indexLock)
            snapshot = _fileIndex.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToList(),
                StringComparer.OrdinalIgnoreCase);

        SaveIndex(snapshot);
    }

    private void SaveIndex(Dictionary<string, List<string>> snapshot)
    {
        try
        {
            using var saveLock = new Mutex(false, "Global\\FileSearch_IndexSave");
            if (!saveLock.WaitOne(5000))
            {
                WriteWarning("Could not acquire index-save lock. Skipping this update.");
                return;
            }

            var json = JsonSerializer.Serialize(snapshot);
            var tempPath = _indexPath + ".tmp";

            using (var fileStream = File.Create(tempPath))
            using (var gzipStream = new GZipStream(fileStream, CompressionMode.Compress))
            using (var writer = new StreamWriter(gzipStream))
            {
                writer.Write(json);
                writer.Flush();
            }

            if (File.Exists(_indexPath))
                File.Replace(tempPath, _indexPath, null);
            else
                File.Move(tempPath, _indexPath);
        }
        catch (Exception ex)
        {
            WriteWarning($"Could not save index: {ex.Message}");
        }
    }

    private void LoadIndex()
    {
        try
        {
            using var saveLock = new Mutex(false, "Global\\FileSearch_IndexSave");
            if (!saveLock.WaitOne(5000))
                return;

            if (File.Exists(_indexPath))
            {
                using var fileStream = File.OpenRead(_indexPath);
                using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
                using var reader = new StreamReader(gzipStream);
                var json = reader.ReadToEnd();
                _fileIndex = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? new();
            }
        }
        catch (Exception ex)
        {
            WriteWarning($"Could not load index: {ex.Message}");
            _fileIndex = new();
        }
    }

    private void WriteStatus(string message)
    {
        if (!_quiet)
            Console.WriteLine(message);
    }

    private void WriteWarning(string message)
    {
        if (!_quiet)
        {
            Console.WriteLine($"Warning: {message}");
            return;
        }

        File.AppendAllText(_watchLogPath, $"[{DateTimeOffset.Now:O}] Warning: {message}{Environment.NewLine}");
    }
}
