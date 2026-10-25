using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

public sealed class StartTokenInfo
{
    public string FilePath { get; set; }
    public string Command { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public int Port { get; set; }
    public IPAddress HostAddress { get; set; }
}

public static class StartTokenReader
{
    private const ulong MaximumToken = 281474976710655UL;

    public static StartTokenInfo ReadLatest(string projectOrRunsDirectory)
    {
        if (String.IsNullOrWhiteSpace(projectOrRunsDirectory))
            throw new InvalidOperationException("请先选择 ns2jb / HBLauncher 项目目录或 runs 目录。");

        try
        {
            string directory = Path.GetFullPath(projectOrRunsDirectory.Trim());
            if (!Directory.Exists(directory))
                throw new InvalidOperationException("选择的目录不存在，请重新选择 ns2jb / HBLauncher 项目目录。");
            string runsDirectory = Path.Combine(directory, "runs");
            if (!Directory.Exists(runsDirectory))
            {
                string leaf = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (String.Equals(leaf, "runs", StringComparison.OrdinalIgnoreCase))
                    runsDirectory = directory;
                else
                    throw new InvalidOperationException("此目录下没有 runs，请选择 ns2jb 或 HBLauncher 项目根目录，并先启动对应程序。");
            }

            string latestPath = null;
            DateTime latestTime = DateTime.MinValue;
            foreach (string runDirectory in Directory.GetDirectories(runsDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                string runName = Path.GetFileName(runDirectory);
                if (!runName.StartsWith("switch2-", StringComparison.OrdinalIgnoreCase) &&
                    !runName.StartsWith("launcher-", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string path in Directory.GetFiles(runDirectory, "connect.txt", SearchOption.TopDirectoryOnly))
                {
                    DateTime modified = File.GetLastWriteTimeUtc(path);
                    if (latestPath == null || modified > latestTime ||
                        (modified == latestTime && String.CompareOrdinal(path, latestPath) > 0))
                    {
                        latestPath = path;
                        latestTime = modified;
                    }
                }
            }
            if (latestPath == null)
                throw new InvalidOperationException("没有找到 runs\\switch2-* 或 runs\\launcher-* 下的 connect.txt。请先启动 ns2jb / HBLauncher，等到显示连接指令后再获取。");

            // Never fall back to an older run when the newest one is unusable.
            return ReadFile(latestPath);
        }
        catch (UnauthorizedAccessException error)
        {
            throw new InvalidOperationException("无法读取项目目录，请确认目录的读取权限。", error);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("读取最新连接指令失败，请确认项目目录仍然存在后重新获取。", error);
        }
        catch (ArgumentException error)
        {
            throw new InvalidOperationException("项目目录路径无效，请重新选择。", error);
        }
        catch (NotSupportedException error)
        {
            throw new InvalidOperationException("项目目录路径无效，请重新选择。", error);
        }
    }

    public static string ValidateCommand(string raw)
    {
        int port;
        IPAddress host;
        return ParseCommand(raw, out port, out host);
    }

    public static void EnsureReady(StartTokenInfo info)
    {
        if (info == null || String.IsNullOrEmpty(info.FilePath))
            throw new InvalidOperationException("请先获取最新 V=START 指令。");

        StartTokenInfo current;
        try { current = ReadFile(info.FilePath); }
        catch (UnauthorizedAccessException error)
        {
            throw new InvalidOperationException("无法读取连接指令，请检查读取权限后重新获取。", error);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("连接指令已不可用，请重新获取。", error);
        }
        if (current.Command != info.Command || current.LastWriteUtc != info.LastWriteUtc)
            throw new InvalidOperationException("连接指令已发生变化，请重新获取最新 V=START 后发送。");

        IPEndPoint[] listeners;
        try { listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(); }
        catch (NetworkInformationException error)
        {
            throw new InvalidOperationException("无法检查监听状态，请确认 ns2jb / HBLauncher 正在本机运行后重试。", error);
        }
        if (!HasListener(current, listeners))
            throw new InvalidOperationException("本机没有监听 " + current.HostAddress + ":" + current.Port.ToString(CultureInfo.InvariantCulture) +
                "。请先启动 ns2jb / HBLauncher，等到显示连接指令后重新获取并发送。");
    }

    internal static bool HasListener(StartTokenInfo info, IEnumerable<IPEndPoint> listeners)
    {
        if (info == null || info.HostAddress == null || listeners == null) return false;
        foreach (IPEndPoint endpoint in listeners)
        {
            if (endpoint != null && endpoint.Port == info.Port && endpoint.Address.AddressFamily == AddressFamily.InterNetwork &&
                (endpoint.Address.Equals(info.HostAddress) || endpoint.Address.Equals(IPAddress.Any)))
                return true;
        }
        return false;
    }

    private static StartTokenInfo ReadFile(string path)
    {
        EnsureRunWaiting(path);
        DateTime modified = File.GetLastWriteTimeUtc(path);
        string raw;
        try { raw = File.ReadAllText(path, new UTF8Encoding(false, true)); }
        catch (DecoderFallbackException error)
        {
            throw new InvalidOperationException("最新 connect.txt 不是有效文本，请重新启动 ns2jb / HBLauncher 生成连接指令。", error);
        }
        int port;
        IPAddress host;
        string command = ParseCommand(raw, out port, out host);
        EnsureRunWaiting(path);
        if (File.GetLastWriteTimeUtc(path) != modified)
            throw new InvalidOperationException("最新 connect.txt 正在更新，请稍后重新获取。");
        return new StartTokenInfo { FilePath = path, Command = command, LastWriteUtc = modified, Port = port, HostAddress = host };
    }

    private static void EnsureRunWaiting(string path)
    {
        string directory = Path.GetDirectoryName(path);
        if (File.Exists(Path.Combine(directory, "result.json")))
            throw new InvalidOperationException("最新会话已经结束，不能发送旧连接指令。请重新启动 ns2jb / HBLauncher 后再获取。");
        if (File.Exists(Path.Combine(directory, "connection.json")))
            throw new InvalidOperationException("最新会话已经配对，不能重复发送连接指令。需要重新连接时，请先重新启动 ns2jb / HBLauncher。");
    }

    private static string ParseCommand(string raw, out int port, out IPAddress host)
    {
        port = 0;
        host = null;
        if (raw == null) throw InvalidCommand();
        string command = raw.Trim(' ', '\t', '\r', '\n', '\uFEFF');
        if (command.Length == 0)
            throw new InvalidOperationException("最新 connect.txt 为空，请等 ns2jb / HBLauncher 生成指令后重新获取。");
        foreach (char character in command)
            if (character > 127 || character == '\r' || character == '\n') throw InvalidCommand();
        const string prefix = "V=START(";
        if (!command.StartsWith(prefix, StringComparison.Ordinal) || !command.EndsWith(")", StringComparison.Ordinal))
            throw InvalidCommand();
        string[] parts = command.Substring(prefix.Length, command.Length - prefix.Length - 1).Split(',');
        if (parts.Length != 9) throw InvalidCommand();
        ulong[] numbers = new ulong[parts.Length];
        for (int index = 0; index < parts.Length; index++)
        {
            parts[index] = parts[index].Trim(' ', '\t');
            if (parts[index].Length == 0) throw InvalidCommand();
            foreach (char character in parts[index])
                if (character < '0' || character > '9') throw InvalidCommand();
            if (!UInt64.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[index]))
                throw InvalidCommand();
        }
        if (numbers[0] < 1 || numbers[0] > 65535) throw InvalidCommand();
        byte[] octets = new byte[4];
        for (int index = 0; index < octets.Length; index++)
        {
            if (numbers[index + 1] > 255) throw InvalidCommand();
            octets[index] = (byte)numbers[index + 1];
        }
        for (int index = 5; index < numbers.Length; index++)
            if (numbers[index] > MaximumToken) throw InvalidCommand();
        port = (int)numbers[0];
        host = new IPAddress(octets);
        return prefix + String.Join(",", parts) + ")";
    }

    private static InvalidOperationException InvalidCommand()
    {
        return new InvalidOperationException("最新 connect.txt 的 V=START 格式无效：必须是一行，包含端口、4 个 IPv4 数字和 4 个 48 位令牌，不能附带其他语句。请重新启动 ns2jb / HBLauncher 生成指令。");
    }
}
