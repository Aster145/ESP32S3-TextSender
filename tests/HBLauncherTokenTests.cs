using System;
using System.IO;
using System.Text;

internal static class HBLauncherTokenTests
{
    private const string Good = "V=START(39380,192,168,1,18,281474976710655,4294967296,0,123456789012345)";
    private static readonly DateTime Stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static int passed;

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAILED: " + label);
        Console.WriteLine("PASS: " + label);
        passed++;
    }

    private static void Reject(Action action, string label, string messageFragment)
    {
        try { action(); }
        catch (InvalidOperationException error)
        {
            Check(error.Message.Contains(messageFragment), label);
            return;
        }
        throw new Exception("FAILED, accepted: " + label);
    }

    private static string WriteRun(string root, string name, string command, int minute)
    {
        string directory = Path.Combine(root, "runs", name);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "connect.txt");
        File.WriteAllText(path, command, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(path, Stamp.AddMinutes(minute));
        return path;
    }

    public static int Main(string[] args)
    {
        try
        {
            string fixture = Path.Combine(Environment.CurrentDirectory, "work", "hblauncher-token-tests-" + Guid.NewGuid().ToString("N"));
            string launcher = Path.Combine(fixture, "HBLauncher");
            string first = WriteRun(launcher, "launcher-older", Good.Replace("123456789012345", "9"), 1);
            string latest = WriteRun(launcher, "launcher-latest", Good + "\r\n", 2);
            StartTokenInfo info = StartTokenReader.ReadLatest(launcher);
            Check(info.FilePath == latest && info.Command == Good, "HBLauncher project root selects latest connect.txt");
            Check(info.Port == 39380 && info.HostAddress.ToString() == "192.168.1.18", "HBLauncher endpoint parsed");
            Check(StartTokenReader.ReadLatest(Path.Combine(launcher, "runs")).FilePath == latest, "HBLauncher runs directory accepted");
            Check(info.Command.Contains("281474976710655"), "HBLauncher 48-bit token retained exactly");

            string ignored = WriteRun(launcher, "host-check", Good.Replace("39380", "12"), 9);
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(ignored), "result.json"), "{}");
            Check(StartTokenReader.ReadLatest(launcher).FilePath == latest, "host-check result and token are ignored");
            WriteRun(launcher, "launcherish", Good.Replace("39380", "12"), 9);
            Check(StartTokenReader.ReadLatest(launcher).FilePath == latest, "non-session prefix is ignored");
            Directory.CreateDirectory(Path.Combine(launcher, "runs", "launcher-empty"));
            Check(StartTokenReader.ReadLatest(launcher).FilePath == latest, "missing connect.txt is not treated as a token file");

            File.WriteAllText(latest, "", Encoding.UTF8);
            File.SetLastWriteTimeUtc(latest, Stamp.AddMinutes(2));
            Reject(delegate { StartTokenReader.ReadLatest(launcher); }, "empty latest launcher never falls back", "为空");
            File.WriteAllText(latest, Good + "\nRUN", Encoding.UTF8);
            File.SetLastWriteTimeUtc(latest, Stamp.AddMinutes(2));
            Reject(delegate { StartTokenReader.ReadLatest(launcher); }, "invalid latest launcher never falls back", "格式无效");
            File.WriteAllText(latest, Good, Encoding.UTF8);
            File.SetLastWriteTimeUtc(latest, Stamp.AddMinutes(2));
            string paired = Path.Combine(Path.GetDirectoryName(latest), "connection.json");
            File.WriteAllText(paired, "{}");
            Reject(delegate { StartTokenReader.ReadLatest(launcher); }, "paired launcher never falls back", "已经配对");
            Reject(delegate { StartTokenReader.EnsureReady(info); }, "launcher paired between get and send rejected", "已经配对");
            File.Delete(paired);
            string finished = Path.Combine(Path.GetDirectoryName(latest), "result.json");
            File.WriteAllText(finished, "{}");
            Reject(delegate { StartTokenReader.ReadLatest(launcher); }, "completed launcher never falls back", "已经结束");
            Reject(delegate { StartTokenReader.EnsureReady(info); }, "launcher completed between get and send rejected", "已经结束");
            File.Delete(finished);

            string mixed = Path.Combine(fixture, "mixed");
            string ns2 = WriteRun(mixed, "switch2-older", Good, 1);
            string hb = WriteRun(mixed, "launcher-newer", Good.Replace("123456789012345", "7"), 2);
            Check(StartTokenReader.ReadLatest(mixed).FilePath == hb, "newest across both supported layouts wins");
            File.SetLastWriteTimeUtc(ns2, Stamp.AddMinutes(3));
            Check(StartTokenReader.ReadLatest(mixed).FilePath == ns2, "newer ns2jb wins over older launcher");
            File.WriteAllText(ns2, "invalid", Encoding.UTF8);
            File.SetLastWriteTimeUtc(ns2, Stamp.AddMinutes(3));
            Reject(delegate { StartTokenReader.ReadLatest(mixed); }, "invalid latest ns2jb does not fall back to launcher", "格式无效");
            File.WriteAllText(hb, "invalid", Encoding.UTF8);
            File.SetLastWriteTimeUtc(hb, Stamp.AddMinutes(4));
            Reject(delegate { StartTokenReader.ReadLatest(mixed); }, "invalid latest launcher does not fall back to ns2jb", "格式无效");

            string original = Path.Combine(fixture, "ns2jb");
            string originalPath = WriteRun(original, "switch2-retained", Good, 1);
            Check(StartTokenReader.ReadLatest(original).FilePath == originalPath, "original ns2jb project layout retained");
            Check(StartTokenReader.ReadLatest(Path.Combine(original, "runs")).FilePath == originalPath, "original ns2jb runs layout retained");
            string missing = Path.Combine(fixture, "no-output");
            Directory.CreateDirectory(Path.Combine(missing, "runs", "launcher-without-connect"));
            Reject(delegate { StartTokenReader.ReadLatest(missing); }, "unmodified launcher without connect.txt gets actionable error", "launcher-*");

            Console.WriteLine("All " + passed + " HBLauncher compatibility checks passed; no sockets, serial, or host startup used.");
            if (args.Length > 0)
            {
                try
                {
                    StartTokenInfo actual = StartTokenReader.ReadLatest(args[0]);
                    Console.WriteLine("LOCAL READ: success, run=" + Path.GetFileName(Path.GetDirectoryName(actual.FilePath)) +
                        ", timestamp=" + actual.LastWriteUtc.ToString("o") + ", command=[REDACTED]");
                }
                catch (InvalidOperationException error)
                {
                    Console.WriteLine("LOCAL READ: " + error.Message);
                }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString());
            return 1;
        }
    }
}
