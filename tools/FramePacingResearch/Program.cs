using FramePacingResearch;

try
{
    if (args.Length == 0 || args is ["--help"])
    {
        Console.WriteLine("FramePacingResearch (local/read-only research, no game control)\n" +
            "  self-test\n" +
            "  analyze --csv file --target 144 --output fresh-directory [--pid N] [--swapchain 0x...]\n" +
            "  capture --presentmon verified.exe --exe full-game-path --pid N --seconds 60 --target 144\n" +
            "          --condition uncapped|native|nvidia --fg off|on|unknown --scene label --output fresh-directory\n" +
            "  capture ... --lab --condition lab   (only own FramePacingLab process)\n" +
            "  compare --manifest capture-directories.json --output fresh-directory\n" +
            "Capture only observes an already running, identity-verified process. No game launch/settings write/injection.");
        return;
    }
    var options = Options.Parse(args.Skip(1).ToArray());
    switch (args[0])
    {
        case "self-test": Checks.Run(); break;
        case "analyze":
            var report = Analysis.Analyze(options.Required("csv"), options.Integer("target", 30, 1000, 144),
                options.Has("pid") ? options.Integer("pid", 1, int.MaxValue) : null, options.Optional("swapchain"));
            Analysis.Save(report, options.FreshDirectory("output")); Console.WriteLine(report.Conclusion); break;
        case "capture":
            using (var cancel = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
                Console.CancelKeyPress += handler;
                try { await Capture.RunAsync(options, cancel.Token); }
                finally { Console.CancelKeyPress -= handler; }
            }
            break;
        case "compare": Comparison.Run(options.Required("manifest"), options.FreshDirectory("output")); break;
        default: throw new ArgumentException("Unknown command. Use --help.");
    }
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }

namespace FramePacingResearch
{
    internal sealed class Options(Dictionary<string, string?> values)
    {
        public static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Expected named option.");
                var name = args[i][2..];
                string? value = name == "lab" ? null : ++i < args.Length ? args[i] : throw new ArgumentException("Missing option value.");
                if (!values.TryAdd(name, value)) throw new ArgumentException("Duplicate option: " + name);
                if (name is not ("csv" or "target" or "output" or "pid" or "swapchain" or "presentmon" or "exe" or "seconds" or "condition" or "fg" or "scene" or "lab" or "manifest"))
                    throw new ArgumentException("Unknown option: " + name);
            }
            return new(values);
        }
        public bool Has(string key) => values.ContainsKey(key);
        public bool Flag(string key) => values.TryGetValue(key, out var value) && value is null;
        public string? Optional(string key) => values.TryGetValue(key, out var value) ? value : null;
        public string Required(string key) => Optional(key) is { Length: > 0 } value ? value : throw new ArgumentException("Required --" + key);
        public int Integer(string key, int min, int max, int? fallback = null)
        {
            if (!Has(key) && fallback.HasValue) return fallback.Value;
            if (!int.TryParse(Required(key), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) || value < min || value > max)
                throw new ArgumentException("Out of range --" + key);
            return value;
        }
        public string FreshDirectory(string key)
        {
            var path = Path.GetFullPath(Required(key));
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Output must be a new directory: " + path);
            return path;
        }
    }
}
