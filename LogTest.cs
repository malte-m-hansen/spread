using System;
using System.IO;
using System.Linq;

namespace Spread;

/// <summary>`--log-test`: levels and rotation of spread.log, in a temp folder (your real log is not touched).</summary>
internal static class LogTest
{
    static int _fail;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(ok ? "" : "  " + detail)}"); if (!ok) _fail++; }

    public static int Run()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spread-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "spread.log");
        string oldPath = Log.FilePath, oldLevel = Log.Level; int oldMax = Log.MaxKb, oldKeep = Log.Keep;
        try
        {
            Log.FilePath = file; Log.ResetSize(); Log.MaxKb = 1024; Log.Keep = 3;
            Console.WriteLine("Log checks:");
            Log.Level = "normal"; Log.Write("always"); Log.Debug("detail");
            Check("normal: Write is kept, Debug is dropped", File.ReadAllText(file).Contains("always") && !File.ReadAllText(file).Contains("detail"));
            Log.Level = "verbose"; Log.Debug("detail2");
            Check("verbose: Debug is written too", File.ReadAllText(file).Contains("detail2"));
            Log.Level = "off"; Log.Write("silent"); Log.Debug("silent2");
            Check("off: nothing is written", !File.ReadAllText(file).Contains("silent"));
            var cfg = new Config { LogLevel = "Chatty", LogMaxKb = 1, LogKeep = 99 };
            Log.Configure(cfg);
            Check("config: unknown level -> normal, size has a floor, keep is capped", Log.Level == "normal" && Log.MaxKb == 64 && Log.Keep == 20);

            // rotation: 64 KB limit, 3 archives
            Log.FilePath = file; Log.Level = "normal"; Log.MaxKb = 64; Log.Keep = 3; Log.ResetSize();
            string line = new string('x', 200);
            for (int i = 0; i < 2500; i++) Log.Write(line + i);   // ~500 KB
            var files = Directory.GetFiles(dir, "spread*.log").Select(Path.GetFileName).OrderBy(x => x).ToList();
            Check("rotation: current file plus at most 3 archives", files.SequenceEqual(new[] { "spread.1.log", "spread.2.log", "spread.3.log", "spread.log" }), string.Join(",", files));
            Check("rotation: every file stays near the limit", Directory.GetFiles(dir, "spread*.log").All(f => new FileInfo(f).Length <= 64 * 1024 + 400));
            string newest = File.ReadAllText(file);
            Check("rotation: the newest line is in the current file", newest.Contains(line + 2499));
        }
        finally
        {
            Log.FilePath = oldPath; Log.Level = oldLevel; Log.MaxKb = oldMax; Log.Keep = oldKeep; Log.ResetSize();
            try { Directory.Delete(dir, true); } catch { }
        }
        Console.WriteLine(_fail == 0 ? "LOG TEST PASSED" : $"LOG TEST FAILED ({_fail})");
        return _fail == 0 ? 0 : 1;
    }
}
