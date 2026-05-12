using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO.Compression;

class FileSearcher
{
    private readonly string _indexPath;
    private Dictionary<string, List<string>> _fileIndex = new();

    public FileSearcher()
    {
        // Store index in user's AppData
        var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileSearch");
        Directory.CreateDirectory(appDataPath);
        _indexPath = Path.Combine(appDataPath, "index.json");
        
        LoadIndex();
    }

    public async Task<List<string>> SearchAsync(string pattern, bool useRegex = false, string? directory = null)
    {
        var results = new List<string>();
        
        if (_fileIndex.Count == 0)
        {
            Console.WriteLine("Index is empty. Run 'search --update-index' to build it.");
            return results;
        }

        await Task.Run(() =>
        {
            // Flatten all files from all drives
            var allFiles = _fileIndex.SelectMany(kvp => kvp.Value.Select(f => Path.Combine(kvp.Key, f))).ToList();

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
        _fileIndex.Clear();

        var drives = specificDrive != null 
            ? new[] { $"{specificDrive}:\\" } 
            : DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToArray();

        foreach (var drive in drives)
        {
            Console.WriteLine($"Indexing {drive}...");
            var files = new List<string>();
            
            await Task.Run(() =>
            {
                try
                {
                    IndexDirectory(drive, files, drive);
                }
                catch (UnauthorizedAccessException ex)
                {
                    Console.WriteLine($"Access denied: {ex.Message}");
                }
            });

            _fileIndex[drive] = files;
            Console.WriteLine($"  Found {files.Count} files on {drive}");
        }

        SaveIndex();
    }

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
        var systemDirs = new[] 
        { 
            "\\$RECYCLE.BIN",
            "\\System Volume Information",
            "\\hiberfil.sys",
            "\\pagefile.sys",
            "\\ProgramData\\Temp",
            "\\Windows\\System32",
            "\\Windows\\WinSxS",
            "\\.git",
            "\\node_modules",
            "\\AppData\\Local\\Temp",
        };

        return systemDirs.Any(dir => path.Contains(dir, StringComparison.OrdinalIgnoreCase));
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
        try
        {
            var json = JsonSerializer.Serialize(_fileIndex);
            using var fileStream = File.Create(_indexPath);
            using var gzipStream = new GZipStream(fileStream, CompressionMode.Compress);
            using var writer = new StreamWriter(gzipStream);
            writer.Write(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not save index: {ex.Message}");
        }
    }

    private void LoadIndex()
    {
        try
        {
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
            Console.WriteLine($"Warning: Could not load index: {ex.Message}");
            _fileIndex = new();
        }
    }
}
