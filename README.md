# FileSearch - Fast Windows File Search Tool

A blazing-fast terminal file search tool for Windows that uses indexed searching to find files instantly.

**Why I made this:** Windows built-in search is painfully slow, unreliable, and frustrating to use. This tool provides instant, accurate file searching from the terminal - exactly what developers and power users need.

## Features

✅ **Directory-based searching** - Search current directory by default (like `find`)  
✅ **Indexed searching** - Fast results after initial index build  
✅ **Live index maintenance** - Keep the index current using `--watch` and automatic rescan logic  
✅ **Fuzzy matching** - Find files even if you don't remember exact names  
✅ **Regex support** - Advanced pattern matching  
✅ **Multi-drive support** - Search all drives or specific ones  
✅ **Automatic caching** - Compressed index stored locally for quick startup  

## Quick Start

### Automated Setup (Recommended)

```powershell
# Clone/download the repository
cd path\to\FileSearch

# Run the automated setup script
.\setup.ps1
```

This will:
- Install .NET dependencies
- Build the application
- Publish to `%LOCALAPPDATA%\FileSearch`
- Add to system PATH
- Build initial file index

### Manual Setup

#### Prerequisites
- .NET 8 SDK or later ([Download](https://dotnet.microsoft.com/download))

#### Build Steps

```powershell
# Restore dependencies
dotnet restore

# Build the project
dotnet build -c Release

# Publish as standalone executable
dotnet publish -c Release -o $env:LOCALAPPDATA\FileSearch
```

#### Add to PATH

**Option 1: Automatic (Recommended)**
```powershell
# Add to current PowerShell session
$env:Path += ";$env:LOCALAPPDATA\FileSearch"

# Add to PowerShell profile (permanent)
Add-Content -Path $PROFILE -Value "`nSet-Alias -Name search -Value `"$env:LOCALAPPDATA\FileSearch\FileSearch.exe`" -Scope Global"
```

**Option 2: System Environment Variables**
1. Press `Win + X` → System → Advanced system settings
2. Click "Environment Variables"
3. Under "User variables", edit `PATH`
4. Add: `%LOCALAPPDATA%\FileSearch`
5. Restart terminal

## Usage

```powershell
# Search current directory (default behavior)
search config.json
search "*.txt"
search "test*.cs"

# Search specific directory
search "*.md" --dir C:\Projects
search config.json -p "D:\Work\MyApp"

# Search entire drive (global search)
search screenshot --drive C
search document -d D

# Advanced options
search "^test.*\.cs$" --regex    # Regex search
search temp --all               # Show all results
search --update-index           # Rebuild file index
search --update-index -d C      # Update index for specific drive
search --watch                  # Keep the index updated while running
search --watch -d C             # Watch a specific drive
```

## How It Works

1. **Indexing** - Scans your drives recursively and builds a compressed JSON index stored in `%APPDATA%\FileSearch\index.json`
2. **Live refresh** - When `--watch` is enabled, the app monitors file system changes and updates the saved index incrementally or rescans the affected drive when notifications are lost
3. **Searching** - Loads the cached index and searches in memory (instant)
4. **Directory Filtering** - By default, only searches within current directory and subdirectories
5. **Matching** - Supports multiple search modes:
   - **Exact match** - File name exactly matches
   - **Wildcard match** - Using `*` patterns
   - **Fuzzy match** - Smart fuzzy matching with quality thresholds
   - **Regex** - Full regex support with `--regex` flag

## Performance

- **Initial index build**: 2-5 minutes (one-time setup)
- **Subsequent searches**: < 500ms for 200K+ files
- **Index size**: ~1-5MB per 1M files (compressed)
- **Memory usage**: Minimal (loads index on-demand)

## Advanced

### Excluded Directories
The following are automatically skipped during indexing:
- `$RECYCLE.BIN`, `System Volume Information`
- `Windows\System32`, `Windows\WinSxS`
- `.git`, `node_modules`
- Temp directories (`AppData\Local\Temp`, etc.)

### Automatic Updates
Run the watcher to keep the index current as files and folders are created, renamed, or deleted:

```powershell
search --watch
```

The watcher performs a full scan when it starts, then updates the saved index incrementally. It remains active until stopped, so to have it start automatically, create a Windows Task Scheduler task triggered **At log on** with `search` as the program and `--watch` as its argument. Use `--watch -d C` to watch only one drive. A full scan is also triggered automatically if Windows reports that change notifications were lost.

## Command Reference

```
Usage: search <pattern> [options]

Options:
  --regex, -r           Use regex pattern matching
  --dir, -p <path>      Search in specific directory (default: current directory)
  --drive, -d <letter>  Search entire drive (global search)
  --all, -a             Show all results (default: first 100)
  --update-index, -u    Rebuild file index
  --watch, -w           Keep index updated while this process is running

Examples:
  search config.json                    # Search current directory
  search "*.txt" --dir C:\Projects     # Search specific directory
  search screenshot --drive C          # Global drive search
  search "^test.*\.cs$" --regex        # Regex search
  search document --all                # Show all results
  search --update-index                # Update index
```

## Troubleshooting

**"search : The term 'search' is not recognized"**
```powershell
# Add to current session
$env:Path += ";$env:LOCALAPPDATA\FileSearch"

# Or restart PowerShell (if PATH was updated)
```

**"Index is empty" error:**
```powershell
search --update-index
```

**Search is slow:**
- Check if index is outdated: `search --update-index`
- For large drives, initial indexing takes 2-5 minutes

**Access denied during indexing:**
Normal - some system directories can't be accessed. Run as admin for more complete indexing.

**Permission issues:**
- Ensure `%LOCALAPPDATA%\FileSearch` exists and is writable
- Run PowerShell as administrator for system-wide PATH changes

## Future Enhancements

- [ ] Use Windows MFT (Master File Table) for even faster indexing
- [ ] Everything API integration for Windows Search compatibility
- [ ] File content search (grep-like functionality)
- [ ] Exclude patterns configuration file
- [ ] Improved ranking and relevance tuning for fuzzy matches

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Test thoroughly
5. Submit a pull request

## License

MIT License - feel free to use and modify!

---

Built with .NET 8 | Fast | Reliable | Open Source
