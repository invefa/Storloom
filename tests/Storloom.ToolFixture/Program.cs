// Test-only subprocess. No production cache or user directory is accessed.
string marker = Path.Combine(AppContext.BaseDirectory, "fixture-mode.txt");
if (!File.Exists(marker) || !args.SequenceEqual(new[] { "nuget", "locals", "http-cache", "--clear" })) return 99;
Console.WriteLine("isolated-tool-fixture");
string mode = File.ReadAllText(marker);
if (mode == "wait") await Task.Delay(Timeout.Infinite);
if (mode == "output") for (int i = 0; i < 768; i++) Console.WriteLine(new string('x', 4096));
return mode == "fail" ? 37 : 0;
