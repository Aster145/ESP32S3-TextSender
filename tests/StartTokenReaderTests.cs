using System;
using System.IO;
using System.Net;
using System.Text;

internal static class StartTokenReaderTests
{
    private const string Good = "V=START(39380,192,168,1,20,0,4294967296,281474976710655,123456789012345)";
    private static int passed;

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Reject(Action action, string name)
    {
        try { action(); }
        catch (InvalidOperationException) { Check(true, name); return; }
        throw new Exception("FAILED, accepted: " + name);
    }

    private static string WriteRun(string runs, string name, string command, DateTime modified)
    {
        string directory = Path.Combine(runs, "switch2-" + name);
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "connect.txt");
        File.WriteAllText(file, command, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(file, modified);
        return file;
    }

    public static int Main()
    {
        try
        {
            Check(StartTokenReader.ValidateCommand(Good) == Good, "preserves 48-bit token digits");
            Check(StartTokenReader.ValidateCommand("\uFEFF \t" + Good + "\r\n") == Good, "BOM and trailing CRLF");
            Check(StartTokenReader.ValidateCommand(Good.Replace(",", ", \t")) == Good, "normalizes parameter whitespace");
            Check(StartTokenReader.ValidateCommand(Good.Replace("39380", "00039380")).StartsWith("V=START(00039380,"), "preserves leading token digits");
            Check(StartTokenReader.ValidateCommand(Good.Replace("39380", "1")).StartsWith("V=START(1,"), "minimum port");
            Check(StartTokenReader.ValidateCommand(Good.Replace("39380", "65535")).StartsWith("V=START(65535,"), "maximum port");
            Reject(delegate { StartTokenReader.ValidateCommand(null); }, "null command");
            Reject(delegate { StartTokenReader.ValidateCommand(" \r\n"); }, "empty command");
            Reject(delegate { StartTokenReader.ValidateCommand(Good.Replace("281474976710655", "281474976710656")); }, "48-bit overflow");
            Reject(delegate { StartTokenReader.ValidateCommand(Good.Replace("281474976710655", "18446744073709551616")); }, "UInt64 overflow");
            Reject(delegate { StartTokenReader.ValidateCommand(Good.Replace("39380", "0")); }, "zero port");
            Reject(delegate { StartTokenReader.ValidateCommand(Good.Replace("39380", "65536")); }, "port overflow");
            Reject(delegate { StartTokenReader.ValidateCommand(Good.Replace("192", "256")); }, "IPv4 octet overflow");
            Reject(delegate { StartTokenReader.ValidateCommand(Good.Replace(",0,", ",-1,")); }, "negative token");
            Reject(delegate { StartTokenReader.ValidateCommand(Good.Replace(",0,", ",+1,")); }, "signed token");
            Reject(delegate { StartTokenReader.ValidateCommand(Good.Replace("39380", "3.938e4")); }, "floating point notation");
            Reject(delegate { StartTokenReader.ValidateCommand(Good + ":RUN"); }, "same-line BASIC suffix");
            Reject(delegate { StartTokenReader.ValidateCommand(Good + "\r\nRUN"); }, "newline BASIC suffix");
            Reject(delegate { StartTokenReader.ValidateCommand(Good + "\n" + Good); }, "multiple statements");
            Reject(delegate { StartTokenReader.ValidateCommand(Good.Replace(",0,", ",\n0,")); }, "embedded newline");
            Reject(delegate { StartTokenReader.ValidateCommand(Good.Replace("39380", "３９３８０")); }, "non-ASCII digits");
            Reject(delegate { StartTokenReader.ValidateCommand(Good.Replace("START", "start")); }, "wrong command form");
            Reject(delegate { StartTokenReader.ValidateCommand(Good.Replace(",123456789012345", "")); }, "too few arguments");
            Reject(delegate { StartTokenReader.ValidateCommand(Good.Replace(")", ",1)")); }, "too many arguments");

            string root = Path.Combine(Environment.CurrentDirectory, "work", "start-token-reader-tests-" + Guid.NewGuid().ToString("N"));
            string runs = Path.Combine(root, "runs");
            Directory.CreateDirectory(runs);
            Reject(delegate { StartTokenReader.ReadLatest(root); }, "no connect files");
            Reject(delegate { StartTokenReader.ReadLatest(Path.Combine(root, "missing")); }, "missing directory");
            DateTime modified = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            string oldPath = WriteRun(runs, "a", Good.Replace(",123456789012345", ",2"), modified);
            string newPath = WriteRun(runs, "b", Good, modified.AddMinutes(1));
            StartTokenInfo info = StartTokenReader.ReadLatest(root);
            Check(info.FilePath == newPath && info.Command == Good, "project root selects latest connect timestamp");
            Check(info.Port == 39380 && info.HostAddress.Equals(IPAddress.Parse("192.168.1.20")), "extracts endpoint");
            Check(info.LastWriteUtc == modified.AddMinutes(1), "retains UTC timestamp");
            Check(StartTokenReader.ReadLatest(runs).FilePath == newPath, "accepts explicit runs directory");
            File.SetLastWriteTimeUtc(oldPath, modified.AddMinutes(2));
            Check(StartTokenReader.ReadLatest(root).FilePath == oldPath, "timestamp takes priority over run name");
            File.SetLastWriteTimeUtc(oldPath, modified);

            File.WriteAllText(newPath, "", Encoding.UTF8);
            File.SetLastWriteTimeUtc(newPath, modified.AddMinutes(1));
            Reject(delegate { StartTokenReader.ReadLatest(root); }, "empty newest never falls back");
            File.WriteAllText(newPath, "V=START(1,2)", Encoding.UTF8);
            File.SetLastWriteTimeUtc(newPath, modified.AddMinutes(1));
            Reject(delegate { StartTokenReader.ReadLatest(root); }, "malformed newest never falls back");
            File.WriteAllText(newPath, Good, Encoding.UTF8);
            File.SetLastWriteTimeUtc(newPath, modified.AddMinutes(1));
            string runDirectory = Path.GetDirectoryName(newPath);
            string paired = Path.Combine(runDirectory, "connection.json");
            File.WriteAllText(paired, "{}");
            Reject(delegate { StartTokenReader.ReadLatest(root); }, "paired newest never falls back");
            Reject(delegate { StartTokenReader.EnsureReady(info); }, "send-time rejects paired session");
            File.Delete(paired);
            string finished = Path.Combine(runDirectory, "result.json");
            File.WriteAllText(finished, "{}");
            Reject(delegate { StartTokenReader.ReadLatest(root); }, "completed newest never falls back");
            Reject(delegate { StartTokenReader.EnsureReady(info); }, "send-time rejects completed session");
            File.Delete(finished);
            File.SetLastWriteTimeUtc(newPath, modified.AddMinutes(3));
            Reject(delegate { StartTokenReader.EnsureReady(info); }, "send-time rejects changed file");

            Check(StartTokenReader.HasListener(info, new[] { new IPEndPoint(IPAddress.Parse("192.168.1.20"), 39380) }), "exact IPv4 listener");
            Check(StartTokenReader.HasListener(info, new[] { new IPEndPoint(IPAddress.Any, 39380) }), "IPv4 any listener");
            Check(!StartTokenReader.HasListener(info, new[] { new IPEndPoint(IPAddress.Loopback, 39380) }), "wrong address rejected");
            Check(!StartTokenReader.HasListener(info, new[] { new IPEndPoint(info.HostAddress, 39381) }), "wrong port rejected");
            Check(!StartTokenReader.HasListener(info, new[] { new IPEndPoint(IPAddress.IPv6Any, 39380) }), "IPv6 any does not imply IPv4 listener");
            Check(!StartTokenReader.HasListener(info, new IPEndPoint[0]), "missing listener rejected");
            Check(!StartTokenReader.HasListener(null, new IPEndPoint[0]), "missing token rejected");
            Console.WriteLine("All " + passed + " checks passed; no network sockets or hardware used.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.ToString());
            return 1;
        }
    }
}
