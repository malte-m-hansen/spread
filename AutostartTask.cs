using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Spread;

/// <summary>
/// "Start with Windows" through a per-user Task Scheduler task (at logon, after a short delay) next to the Run-key entry.
/// Explorer starts the Run entries one after another, each waiting for the one before: on a busy PC with six startup apps Spread came last,
/// three minutes after the desktop appeared. A logon task runs next to that queue. No admin rights are needed (the task runs as you, not
/// elevated). Where tasks are blocked the Run entry still starts Spread; whichever fires first wins (the second start sees the single-instance
/// lock and ends). Task Manager's Startup toggle for Spread (which only affects the Run entry) is honoured through <see cref="FromTaskArg"/>.
/// </summary>
internal static class AutostartTask
{
    internal static string Name { get; set; } = "Spread";   // only the local self-test uses another name
    /// <summary>Added to the task's command line: a start by the task checks Task Manager's Startup toggle first.</summary>
    public const string FromTaskArg = "--from-task";
    const int DelaySeconds = 10;   // the shell has to be there: the bar registers with Explorer

    /// <summary>"cmd.exe /c start ..." -> ("cmd.exe", "/c start ..."); a quoted program path with spaces is one piece.</summary>
    internal static (string Command, string Arguments) Split(string launchCommand)
    {
        string s = launchCommand.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return end < 0 ? (s.Trim('"'), "") : (s[1..end], s[(end + 1)..].Trim());
        }
        int sp = s.IndexOf(' ');
        return sp < 0 ? (s, "") : (s[..sp], s[(sp + 1)..].Trim());
    }

    /// <summary>The task definition (Task Scheduler 1.2 XML, UTF-16 as schtasks wants it).</summary>
    internal static string BuildXml(string userId, string command, string arguments, int delaySeconds)
    {
        static string E(string t) => SecurityElement.Escape(t) ?? "";
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo><Description>Starts Spread when you log on (the Run entry is the fallback).</Description></RegistrationInfo>
              <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{E(userId)}</UserId><Delay>PT{delaySeconds}S</Delay></LogonTrigger></Triggers>
              <Principals><Principal id="Author"><UserId>{E(userId)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author"><Exec><Command>{E(command)}</Command><Arguments>{E(arguments)}</Arguments></Exec></Actions>
            </Task>
            """;
    }

    /// <summary>The command-line arguments stored in a task definition, or null.</summary>
    internal static string? ArgumentsOf(string xml)
    {
        try
        {
            var ns = XNamespace.Get("http://schemas.microsoft.com/windows/2004/02/mit/task");
            return XDocument.Parse(xml).Descendants(ns + "Arguments").FirstOrDefault()?.Value;
        }
        catch { return null; }
    }

    /// <summary>Create or rewrite the task when it is missing or its command points to another build.</summary>
    internal static bool NeedsUpdate(string? existingArguments, string wantedArguments) => existingArguments == null || !string.Equals(existingArguments, wantedArguments, StringComparison.Ordinal);

    /// <summary>
    /// Task Manager's Startup tab only switches the Run entry (HKCU\...\Explorer\StartupApproved\Run). Its value starts with 02 or 06 when enabled and
    /// 03 or 07 when disabled; no value at all means enabled. A start by the task honours it, so "disable" there still means "do not start".
    /// </summary>
    internal static bool StartApproved(byte[]? value) => value == null || value.Length == 0 || (value[0] & 1) == 0;

    public static bool StartApprovedInRegistry()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
            return StartApproved(k?.GetValue("Spread") as byte[]);
        }
        catch { return true; }
    }

    static string UserId => System.Security.Principal.WindowsIdentity.GetCurrent().Name;

    /// <summary>The task's current arguments (null: no such task or it cannot be read).</summary>
    internal static string? ReadArguments()
    {
        var (code, output) = RunSchtasks(new[] { "/query", "/tn", Name, "/xml" });
        return code == 0 ? ArgumentsOf(output) : null;
    }

    /// <summary>Make sure the task exists and runs <paramref name="launchCommand"/>. Blocks for a moment (schtasks.exe): call from a worker.</summary>
    public static bool Sync(string launchCommand)
    {
        try
        {
            var (command, args) = Split(launchCommand);
            string wanted = args.Length == 0 ? FromTaskArg : args + " " + FromTaskArg;
            if (!NeedsUpdate(ReadArguments(), wanted)) return true;
            string file = Path.Combine(Path.GetTempPath(), "spread-task-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                File.WriteAllText(file, BuildXml(UserId, command, wanted, DelaySeconds), Encoding.Unicode);
                var (code, output) = RunSchtasks(new[] { "/create", "/tn", Name, "/xml", file, "/f" });
                if (code == 0) { Log.Write("autostart: logon task created or updated"); return true; }
                Log.Write("autostart: logon task not created (" + code + "); the Run entry still starts Spread. " + FirstLine(output));
                return false;
            }
            finally { try { File.Delete(file); } catch { } }
        }
        catch (Exception e) { Log.Write("autostart: logon task failed: " + e.GetType().Name); return false; }
    }

    public static void Remove()
    {
        try
        {
            var (code, output) = RunSchtasks(new[] { "/delete", "/tn", Name, "/f" });
            if (code != 0 && !output.Contains("cannot find", StringComparison.OrdinalIgnoreCase)) Log.Write("autostart: logon task not removed (" + code + "). " + FirstLine(output));
        }
        catch (Exception e) { Log.Write("autostart: removing the logon task failed: " + e.GetType().Name); }
    }

    static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();

    static (int Code, string Output) RunSchtasks(string[] arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in arguments) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch { } return (-1, "schtasks did not answer in time"); }
        return (p.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}

/// <summary>
/// `--autostart-task-test` (local only: it talks to the real Task Scheduler, so it is not part of the CI run): creates, updates and removes a
/// throw-away task named "Spread-selftest". The real "Spread" task is never touched.
/// </summary>
internal static class AutostartTaskTest
{
    static int _fail;
    static void Check(string name, bool ok, string detail = "") { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name}{(ok ? "" : "  " + detail)}"); if (!ok) _fail++; }

    public static int Run()
    {
        AutostartTask.Name = "Spread-selftest";
        Console.WriteLine("Logon task against the real Task Scheduler (task name Spread-selftest):");
        AutostartTask.Remove();
        try
        {
            Check("no such task at first", AutostartTask.ReadArguments() == null);
            Check("created", AutostartTask.Sync("cmd.exe /c exit 1"));
            Check("its command line is stored with the marker", AutostartTask.ReadArguments() == "/c exit 1 " + AutostartTask.FromTaskArg, AutostartTask.ReadArguments() ?? "null");
            Check("the same command again changes nothing", AutostartTask.Sync("cmd.exe /c exit 1") && AutostartTask.ReadArguments() == "/c exit 1 " + AutostartTask.FromTaskArg);
            Check("another build folder rewrites it", AutostartTask.Sync("cmd.exe /c exit 2") && AutostartTask.ReadArguments() == "/c exit 2 " + AutostartTask.FromTaskArg, AutostartTask.ReadArguments() ?? "null");
        }
        finally { AutostartTask.Remove(); }
        Check("removed", AutostartTask.ReadArguments() == null);
        Console.WriteLine(_fail == 0 ? "AUTOSTART TASK TEST PASSED" : $"AUTOSTART TASK TEST FAILED ({_fail})");
        return _fail == 0 ? 0 : 1;
    }
}
