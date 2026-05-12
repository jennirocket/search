using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

class Program
{
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return;
        }

        var sw = Stopwatch.StartNew();
        
        // Parse arguments
        string? searchPattern = null;
        string? driveToSearch = null;
        string? directoryToSearch = null;
        bool useRegex = false;
        bool updateIndex = false;
        bool showAll = false;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLower())
            {
                case "--update-index":
                case "-u":
                    updateIndex = true;
                    break;
                case "--regex":
                case "-r":
                    useRegex = true;
                    break;
                case "--all":
                case "-a":
                    showAll = true;
                    break;
                case "--drive":
                case "-d":
                    if (i + 1 < args.Length)
                        driveToSearch = args[++i];
                    break;
                case "--dir":
                case "-p":
                    if (i + 1 < args.Length)
                        directoryToSearch = args[++i];
                    break;
                default:
                    if (!args[i].StartsWith("-"))
                        searchPattern = args[i];
                    break;
            }
        }

        try
        {
            var searcher = new FileSearcher();

            if (updateIndex)
            {
                Console.WriteLine("Updating file index...");
                await searcher.BuildIndexAsync(driveToSearch);
                sw.Stop();
                Console.WriteLine($"Index updated in {sw.ElapsedMilliseconds}ms");
                return;
            }

            if (string.IsNullOrWhiteSpace(searchPattern))
            {
                PrintUsage();
                return;
            }

            if (!string.IsNullOrWhiteSpace(driveToSearch) && !updateIndex)
                directoryToSearch = $"{driveToSearch}:\\";

            // Use current directory if no directory specified
            if (string.IsNullOrWhiteSpace(directoryToSearch))
            {
                directoryToSearch = Directory.GetCurrentDirectory();
            }

            var results = await searcher.SearchAsync(searchPattern, useRegex, directoryToSearch);
            
            sw.Stop();

            if (results.Count == 0)
            {
                Console.WriteLine("No files found.");
            }
            else
            {
                // Limit results to 100 unless --all is specified
                var displayResults = showAll ? results : results.Take(100).ToList();
                
                Console.WriteLine($"\nFound {results.Count} file(s) in {sw.ElapsedMilliseconds}ms:\n");
                
                foreach (var result in displayResults)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine(result);
                    Console.ResetColor();
                }

                if (results.Count > 100 && !showAll)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"\n... and {results.Count - 100} more. Use --all to see all results.");
                    Console.ResetColor();
                }
            }
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine(@"
FileSearch - Fast Windows file search tool

Usage:
  search <pattern> [options]

Options:
  --regex, -r           Use regex pattern matching
  --dir, -p <path>      Search in specific directory (default: current directory)
  --drive, -d <letter>  Search specific drive (e.g., -d C)
  --all, -a             Show all results (default: first 100)
  --update-index, -u    Rebuild file index

Examples:
  search config.json
  search ""*.txt"" --dir C:\Projects
  search ""^test.*\.cs$"" --regex
  search document --all
  search --update-index
");
    }
}
