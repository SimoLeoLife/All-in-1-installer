using System.Globalization;

namespace AllInOneInstaller.Remote;

public sealed record DiskUsage(string Mount, string Device, long Total, long Used, long Available)
{
    public double Percent => Total > 0 ? Used * 100.0 / Total : 0;
}

public sealed record LoggedInUser(string User, string Terminal, string Since, string From);

/// <summary>One reading of the server. CPU and network are counters, so compare two samples for a rate.</summary>
public sealed record StatusSample(
    DateTime At,
    long CpuTotal,
    long CpuIdle,
    int Cores,
    long MemTotal,
    long MemAvailable,
    long SwapTotal,
    long SwapFree,
    long ReceivedBytes,
    long SentBytes,
    string Load,
    long UptimeSeconds,
    IReadOnlyList<DiskUsage> Disks,
    IReadOnlyList<LoggedInUser> Users,
    int? PlayersOnline,
    bool EmulatorRunning)
{
    public long MemUsed => MemTotal - MemAvailable;

    /// <summary>CPU busy percentage between <paramref name="previous"/> and this sample.</summary>
    public double CpuPercent(StatusSample previous)
    {
        long total = CpuTotal - previous.CpuTotal, idle = CpuIdle - previous.CpuIdle;
        return total > 0 ? Math.Clamp((total - idle) * 100.0 / total, 0, 100) : 0;
    }

    public (double In, double Out) BytesPerSecond(StatusSample previous)
    {
        double seconds = (At - previous.At).TotalSeconds;
        if (seconds <= 0) return (0, 0);
        return (Math.Max(0, (ReceivedBytes - previous.ReceivedBytes) / seconds), Math.Max(0, (SentBytes - previous.SentBytes) / seconds));
    }
}

/// <summary>The git state of one hotel repository.</summary>
public sealed record RepositoryVersion(string Name, string Directory, string? Branch, string? Commit, string? Date, string? Subject, string? Build);

/// <summary>Live figures for the Server status dashboard, read from /proc in one short command per sample.</summary>
public static class ServerStatus
{
    // Network totals cover the server's own interfaces, not loopback or Docker's bridges.
    private const string SampleScript = $$"""
        echo "cpu $(head -1 /proc/stat)"
        echo "cores $(nproc)"
        awk '/^(MemTotal|MemAvailable|SwapTotal|SwapFree):/ {print "mem", $1, $2}' /proc/meminfo
        awk 'NR > 2 { sub(/^ */, ""); split($0, a, /[: ]+/); if (a[1] != "lo" && a[1] !~ /^(docker|veth|br-)/) { rx += a[2]; tx += a[10] } } END { printf "net %.0f %.0f\n", rx, tx }' /proc/net/dev
        echo "load $(cut -d' ' -f1-3 /proc/loadavg)"
        echo "uptime $(cut -d' ' -f1 /proc/uptime)"
        df -P -B1 -x tmpfs -x devtmpfs -x overlay -x squashfs -x efivarfs 2>/dev/null | awk 'NR > 1 { print "disk", $6, $1, $2, $3, $4 }'
        who 2>/dev/null | awk '{ from = ""; if (match($0, /\(.*\)/)) from = substr($0, RSTART + 1, RLENGTH - 2); print "user", $1, $2, $3 "_" $4, from }'
        db=$(grep -sE '^db.database=' {{EmulatorInstaller.Directory}}/config.ini | cut -d= -f2 | tr -d '[:space:]')
        [ -n "$db" ] && echo "players $(mariadb -N "$db" -e "SELECT COUNT(*) FROM users WHERE online = '1'" 2>/dev/null)"
        if [ -d /run/systemd/system ]; then systemctl is-active --quiet emulator && echo "emulator yes"; else pgrep -f jar-with-dependencies >/dev/null && echo "emulator yes"; fi
        true
        """;

    public static async Task<StatusSample> SampleAsync(ServerConnection connection, CancellationToken token = default)
    {
        CommandResult result = await connection.RunAsRootAsync(SampleScript, token);
        long cpuTotal = 0, cpuIdle = 0, memTotal = 0, memAvailable = 0, swapTotal = 0, swapFree = 0, rx = 0, tx = 0, uptime = 0;
        int cores = 1;
        int? players = null;
        bool emulator = false;
        string load = "";
        var disks = new List<DiskUsage>();
        var users = new List<LoggedInUser>();
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (f[0])
            {
                // cpu cpu user nice system idle iowait irq softirq steal
                case "cpu" when f.Length >= 6:
                    long[] ticks = f.Skip(2).Take(8).Select(Long).ToArray();
                    cpuTotal = ticks.Sum();
                    cpuIdle = ticks[3] + (ticks.Length > 4 ? ticks[4] : 0);
                    break;
                case "cores" when f.Length > 1: cores = Math.Max(1, (int)Long(f[1])); break;
                case "mem" when f.Length > 2:
                    long kb = Long(f[2]) * 1024;
                    switch (f[1])
                    {
                        case "MemTotal:": memTotal = kb; break;
                        case "MemAvailable:": memAvailable = kb; break;
                        case "SwapTotal:": swapTotal = kb; break;
                        case "SwapFree:": swapFree = kb; break;
                    }
                    break;
                case "net" when f.Length > 2: rx = Long(f[1]); tx = Long(f[2]); break;
                case "load": load = string.Join(' ', f.Skip(1)); break;
                case "uptime" when f.Length > 1:
                    uptime = double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double up) ? (long)up : 0;
                    break;
                case "disk" when f.Length > 5: disks.Add(new DiskUsage(f[1], f[2], Long(f[3]), Long(f[4]), Long(f[5]))); break;
                case "user" when f.Length > 3: users.Add(new LoggedInUser(f[1], f[2], f[3].Replace('_', ' '), f.Length > 4 ? f[4] : "")); break;
                case "players" when f.Length > 1 && int.TryParse(f[1], out int online): players = online; break;
                case "emulator": emulator = true; break;
            }
        }
        return new StatusSample(DateTime.UtcNow, cpuTotal, cpuIdle, cores, memTotal, memAvailable, swapTotal, swapFree, rx, tx, load, uptime,
            disks, users, players, emulator);
    }

    /// <summary>Branch, commit and date of the emulator, Octane and Octane-Renderer clones, plus the emulator's JAR version.</summary>
    public static async Task<IReadOnlyList<RepositoryVersion>> VersionsAsync(ServerConnection connection, CancellationToken token = default)
    {
        (string Name, string Directory)[] repositories =
        [
            ("Emulator (Polaris)", EmulatorInstaller.Repository),
            ("Octane", OctaneInstaller.Octane),
            ("Octane-Renderer", OctaneInstaller.Renderer),
            ("AtomCMS", AtomCmsInstaller.Directory)
        ];
        string script = string.Join('\n', repositories.Select((r, i) => $$"""
            d={{r.Directory}}
            if [ -d "$d/.git" ]; then
              echo "repo{{i}} $(git -c safe.directory="$d" -C "$d" rev-parse --abbrev-ref HEAD 2>/dev/null)|$(git -c safe.directory="$d" -C "$d" log -1 --format='%h|%cd|%s' --date=format:'%Y-%m-%d %H:%M' 2>/dev/null)"
            fi
            """)) + $$"""

            jar=$(ls -t {{EmulatorInstaller.Directory}}/target/Polaris-*-jar-with-dependencies.jar {{EmulatorInstaller.Directory}}/target/Habbo-*-jar-with-dependencies.jar 2>/dev/null | head -1)
            [ -n "$jar" ] && echo "jar $(basename "$jar" -jar-with-dependencies.jar)"
            [ -f {{OctaneInstaller.Octane}}/package.json ] && echo "octane $(grep -m1 '"version"' {{OctaneInstaller.Octane}}/package.json | cut -d'"' -f4)"
            [ -f {{OctaneInstaller.Renderer}}/package.json ] && echo "renderer $(grep -m1 '"version"' {{OctaneInstaller.Renderer}}/package.json | cut -d'"' -f4)"
            true
            """;
        CommandResult result = await connection.RunAsRootAsync(script, token);
        var lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => (Key: l.Split(' ', 2)[0], Value: l.Contains(' ') ? l.Split(' ', 2)[1] : ""))
            .ToList();
        string? Value(string key) => lines.FirstOrDefault(l => l.Key == key).Value is { Length: > 0 } v ? v : null;
        string?[] builds = [Value("jar"), Value("octane") is { } o ? "v" + o : null, Value("renderer") is { } r ? "v" + r : null, null];

        var versions = new List<RepositoryVersion>();
        for (int i = 0; i < repositories.Length; i++)
        {
            if (Value($"repo{i}") is not { } info) continue;
            string[] parts = info.Split('|', 4);
            versions.Add(new RepositoryVersion(repositories[i].Name, repositories[i].Directory, Part(parts, 0), Part(parts, 1), Part(parts, 2), Part(parts, 3), builds[i]));
        }
        return versions;
    }

    private static string? Part(string[] parts, int i) => parts.Length > i && parts[i].Length > 0 ? parts[i] : null;

    private static long Long(string text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
}
