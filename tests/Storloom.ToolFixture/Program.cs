// Test-only subprocess. No production cache or user directory is accessed.
string marker = Path.Combine(AppContext.BaseDirectory, "fixture-mode.txt");
if (!File.Exists(marker)) return 99;
if (File.ReadAllText(marker) == "pip-info-fixture" && args.SequenceEqual(new[] { "-m", "pip", "cache", "info" })) {
    Console.WriteLine("Package index page cache size: 2.7 GB\nLocally built wheels size: 2.0 kB"); return 0;
}
if (!args.SequenceEqual(new[] { "nuget", "locals", "http-cache", "--clear" })) return 99;
Console.WriteLine("isolated-tool-fixture");
string mode = File.ReadAllText(marker);
if (mode == "clean-cache") File.Delete(Path.Combine(AppContext.BaseDirectory, "cache", "removable.cache"));
if (mode == "wait") await Task.Delay(Timeout.Infinite);
if (mode == "output") for (int i = 0; i < 768; i++) Console.WriteLine(new string('x', 4096));
return mode == "fail" ? 37 : 0;
