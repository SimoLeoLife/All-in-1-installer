using System.Globalization;

namespace AllInOneInstaller.Remote;

/// <summary>One thread of the emulator's JVM: its CPU ticks so far and its state (R running, S sleeping ...).</summary>
public sealed record JvmThread(int Id, string Name, char State, long Ticks);

/// <summary>A reading of the emulator process. Ticks are counters: compare two samples for a rate.</summary>
public sealed record EmulatorSample(
    DateTime At,
    int Pid,
    int TicksPerSecond,
    int Cores,
    long MachineTicks,
    long ProcessTicks,
    long RssBytes,
    long MaxHeapBytes,
    long StartedSecondsAgo,
    IReadOnlyList<JvmThread> Threads)
{
    public bool Running => Pid > 0;

    /// <summary>CPU of the whole process: percent of the server, and percent of one core.</summary>
    public (double Server, double Core) Cpu(EmulatorSample previous)
    {
        long machine = MachineTicks - previous.MachineTicks;
        long process = ProcessTicks - previous.ProcessTicks;
        double seconds = (At - previous.At).TotalSeconds;
        double server = machine > 0 ? Math.Clamp(process * 100.0 / machine, 0, 100) : 0;
        double core = seconds > 0 ? Math.Max(0, process * 100.0 / (seconds * TicksPerSecond)) : 0;
        return (server, core);
    }

    /// <summary>Percent of one core per thread id, since <paramref name="previous"/>.</summary>
    public Dictionary<int, double> ThreadCpu(EmulatorSample previous)
    {
        double seconds = (At - previous.At).TotalSeconds;
        var before = previous.Threads.ToDictionary(t => t.Id, t => t.Ticks);
        var cpu = new Dictionary<int, double>();
        foreach (JvmThread thread in Threads)
        {
            long delta = before.TryGetValue(thread.Id, out long old) ? thread.Ticks - old : 0;
            cpu[thread.Id] = seconds > 0 ? Math.Max(0, delta * 100.0 / (seconds * TicksPerSecond)) : 0;
        }
        return cpu;
    }
}

/// <summary>A room in the emulator's "Slow wired task" warnings.</summary>
public sealed record SlowWiredRoom(int RoomId, string? Name, int Count, int SlowestMs, int TotalMs, string LastSeen, string Shards);

/// <summary>What the emulator log says about wired since the emulator started.</summary>
public sealed record WiredLogSummary(IReadOnlyList<SlowWiredRoom> Rooms, IReadOnlyList<string> Recent, int SlowTasks, int DroppedWarnings);

/// <summary>
/// Insight into the emulator without changing it: per-thread CPU of its JVM from /proc (the wired engine runs on
/// WiredTickCoordinator, WiredTickShard-N and WiredTickHeavy-N), and the slow-wired warnings in its log.
/// </summary>
public static class EmulatorMonitor
{
    // Prefixes: names from /proc are cut to 15 characters (WiredTickCoordi) when jcmd gave no full names.
    public static string Group(string thread) => thread switch
    {
        _ when thread.StartsWith("WiredTick", StringComparison.Ordinal) => "Wired",
        _ when thread.StartsWith("HabExec", StringComparison.Ordinal) => "Game logic (HabExec)",
        _ when thread.StartsWith("Game", StringComparison.Ordinal) || thread.StartsWith("Ws", StringComparison.Ordinal) => "Game connections",
        _ when thread.StartsWith("Polaris-JDBC", StringComparison.Ordinal) || thread.Contains("Hikari", StringComparison.Ordinal) => "Database",
        _ when thread.StartsWith("GC ", StringComparison.Ordinal) || thread.StartsWith("G1 ", StringComparison.Ordinal) || thread == "VM Thread" => "Garbage collector",
        _ when thread.StartsWith("C1 ", StringComparison.Ordinal) || thread.StartsWith("C2 ", StringComparison.Ordinal) => "JIT compiler",
        _ when thread.StartsWith("AsyncAppender", StringComparison.Ordinal) => "Logging",
        _ when thread.StartsWith("RCON", StringComparison.Ordinal) => "RCON",
        _ => "Other"
    };

    /// <summary>Readable name of a wired thread.</summary>
    public static string WiredName(string thread) => thread switch
    {
        _ when thread.StartsWith("WiredTickCoordi", StringComparison.Ordinal) => "Coordinator",
        _ when thread.StartsWith("WiredTickShard-", StringComparison.Ordinal) => thread.Length > 15 ? $"Shard {thread[15..]}" : "Shard",
        _ when thread.StartsWith("WiredTickHeavy-", StringComparison.Ordinal) => thread.Length > 15 ? $"Heavy rooms {thread[15..]}" : "Heavy rooms",
        _ => thread
    };

    private const string SampleScript = """
        pid=$(pgrep -u emulator -x java | head -1)
        echo "clk $(getconf CLK_TCK)"
        echo "cores $(nproc)"
        echo "machine $(head -1 /proc/stat | awk '{ s = 0; for (i = 2; i <= 9; i++) s += $i; print s }')"
        if [ -n "$pid" ] && [ -d /proc/$pid ]; then
          echo "pid $pid"
          # comm can hold spaces and brackets: the fields after the last ')' are fixed.
          awk '{ sub(/.*\) /, ""); print "proc", $12 + $13, $20 }' /proc/$pid/stat
          echo "rss $(awk '/^VmRSS:/ {print $2 * 1024}' /proc/$pid/status)"
          echo "xmx $(tr '\0' '\n' < /proc/$pid/cmdline | sed -n 's/^-Xmx\([0-9]*\)\([mMgG]\)$/\1 \2/p')"
          echo "boot $(awk '{print int($1)}' /proc/uptime)"
          for t in /proc/$pid/task/*; do
            s=$(cat "$t/stat" 2>/dev/null) || continue
            rest=${s##*) }
            set -- $rest
            echo "t ${t##*/} $1 $(( ${12} + ${13} )) $(cat "$t/comm" 2>/dev/null)"
          done
        fi
        true
        """;

    public static async Task<EmulatorSample> SampleAsync(ServerConnection connection, CancellationToken token = default)
    {
        CommandResult result = await connection.RunAsRootAsync(SampleScript, token);
        int pid = 0, clk = 100, cores = 1;
        long machine = 0, process = 0, rss = 0, maxHeap = 0, startTicks = 0, boot = 0;
        var threads = new List<JvmThread>();
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] f = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 2) continue;
            switch (f[0])
            {
                case "clk": clk = Math.Max(1, (int)Long(f[1])); break;
                case "cores": cores = Math.Max(1, (int)Long(f[1])); break;
                case "machine": machine = Long(f[1]); break;
                case "pid": pid = (int)Long(f[1]); break;
                case "proc" when f.Length > 2: process = Long(f[1]); startTicks = Long(f[2]); break;
                case "rss": rss = Long(f[1]); break;
                case "xmx" when f.Length > 2: maxHeap = Long(f[1]) * (f[2] is "g" or "G" ? 1L << 30 : 1L << 20); break;
                case "boot": boot = Long(f[1]); break;
                case "t" when f.Length > 4:
                    threads.Add(new JvmThread((int)Long(f[1]), string.Join(' ', f.Skip(4)), f[2][0], Long(f[3])));
                    break;
            }
        }
        long startedAgo = pid > 0 && boot > 0 ? Math.Max(0, boot - startTicks / clk) : 0;
        return new EmulatorSample(DateTime.UtcNow, pid, clk, cores, machine, process, rss, maxHeap, startedAgo, threads);
    }

    /// <summary>
    /// The JVM's own thread names by Linux thread id, from jcmd Thread.print (as the emulator account). Linux keeps
    /// only 15 characters, so "WiredTickShard-0" .. "-5" all read "WiredTickShard-" in /proc. Empty when jcmd is
    /// missing or refuses.
    /// </summary>
    public static async Task<Dictionary<int, string>> ThreadNamesAsync(ServerConnection connection, int pid, CancellationToken token = default)
    {
        string script = $$"""
            java=$({{EmulatorInstaller.FindJava}})
            [ -x "$java/bin/jcmd" ] || exit 0
            timeout 10 runuser -u {{EmulatorInstaller.Account}} -- "$java/bin/jcmd" {{pid}} Thread.print 2>/dev/null |
              sed -nE 's/^"([^"]+)".* nid=([0-9]+) .*/\2 \1/p'
            true
            """;
        CommandResult result = await connection.RunAsRootAsync(script, token);
        var names = new Dictionary<int, string>();
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int space = line.IndexOf(' ');
            if (space > 0 && int.TryParse(line[..space], out int tid)) names[tid] = line[(space + 1)..].Trim();
        }
        return names;
    }

    /// <summary>
    /// "Slow wired task: shard=, room=, took=ms" and "Room N has N wired tasks waiting" from the current
    /// emulator log, per room, with the room names from the database.
    /// </summary>
    public static async Task<WiredLogSummary> WiredLogAsync(ServerConnection connection, CancellationToken token = default)
    {
        string script = $$"""
            log={{EmulatorInstaller.LogDirectory}}/emulator.log
            [ -f "$log" ] || exit 0
            tmp=$(mktemp)
            grep -E 'Slow wired task|wired tasks waiting' "$log" 2>/dev/null | tail -n 5000 > "$tmp" || true
            awk '/Slow wired task/ {
                   match($0, /shard=[0-9]+/); shard = substr($0, RSTART + 6, RLENGTH - 6)
                   match($0, /room=[0-9]+/); room = substr($0, RSTART + 5, RLENGTH - 5)
                   match($0, /took=[0-9]+/); took = substr($0, RSTART + 5, RLENGTH - 5) + 0
                   n[room]++; sum[room] += took; if (took > max[room]) max[room] = took; last[room] = $1
                   if (index(shards[room], "," shard ",") == 0) shards[room] = shards[room] "," shard ","
                   total++ }
                 /wired tasks waiting/ { dropped++ }
                 END { for (r in n) { s = shards[r]; gsub(/,+/, ",", s); gsub(/^,|,$/, "", s); print "room", r, n[r], max[r], sum[r], last[r], s }
                       print "totals", total + 0, dropped + 0 }' "$tmp"
            tail -n 8 "$tmp" | sed 's/^/recent /'
            rm -f "$tmp"
            db=$(grep -sE '^db.database=' {{EmulatorInstaller.Directory}}/config.ini | cut -d= -f2 | tr -d '[:space:]')
            ids=$(grep -oE 'room=[0-9]+' "$log" 2>/dev/null | cut -d= -f2 | sort -un | tail -n 200 | paste -sd, -)
            [ -n "$db" ] && [ -n "$ids" ] && mariadb -N -B "$db" -e "SELECT id, name FROM rooms WHERE id IN ($ids)" 2>/dev/null | sed 's/^/name /'
            true
            """;
        CommandResult result = await connection.RunAsRootAsync(script, token);
        var names = new Dictionary<int, string>();
        var rows = new List<(int Id, int Count, int Max, int Sum, string Last, string Shards)>();
        var recent = new List<string>();
        int slow = 0, dropped = 0;
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("recent ", StringComparison.Ordinal)) { recent.Add(line[7..].Trim()); continue; }
            if (line.StartsWith("name ", StringComparison.Ordinal))
            {
                string[] parts = line[5..].Split('\t', 2);
                if (parts.Length == 2 && int.TryParse(parts[0], out int id)) names[id] = parts[1];
                continue;
            }
            string[] f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f[0] == "room" && f.Length >= 6)
                rows.Add(((int)Long(f[1]), (int)Long(f[2]), (int)Long(f[3]), (int)Long(f[4]), f[5], f.Length > 6 ? f[6] : ""));
            else if (f[0] == "totals" && f.Length >= 3) { slow = (int)Long(f[1]); dropped = (int)Long(f[2]); }
        }
        var rooms = rows
            .OrderByDescending(r => r.Sum)
            .Select(r => new SlowWiredRoom(r.Id, names.GetValueOrDefault(r.Id), r.Count, r.Max, r.Sum, r.Last, r.Shards))
            .ToList();
        return new WiredLogSummary(rooms, recent, slow, dropped);
    }

    private static long Long(string text) => long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : 0;
}
