using Renci.SshNet;
using Renci.SshNet.Common;

namespace AllInOneInstaller.Remote;

public sealed record ServerLogin(string Host, int Port, string User, string Password)
{
    /// <summary>Parses "host", "host:port" or "[ipv6]:port"; the port defaults to 22.</summary>
    public static (string Host, int Port) ParseHost(string text)
    {
        text = text.Trim();
        if (text.StartsWith('[') && text.IndexOf(']') is var close and > 0)
        {
            string host = text[1..close];
            string rest = text[(close + 1)..];
            return rest.StartsWith(':') && int.TryParse(rest[1..], out int p) ? (host, p) : (host, 22);
        }
        // A single colon is host:port; more colons is a bare IPv6 address.
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out int port))
            return (text[..colon], port);
        return (text, 22);
    }
}

/// <summary>From /etc/os-release.</summary>
public sealed record OsInfo(string Id, string VersionId, string PrettyName)
{
    public bool IsSupported => Id is "ubuntu" or "debian";
}

/// <summary>The stages of <see cref="ServerConnection.ConnectAsync"/>, in order.</summary>
public enum ConnectStage { Login, OperatingSystem, Root }

public sealed record CommandResult(int ExitCode, string Output, string Error)
{
    public bool Succeeded => ExitCode == 0;
}

public sealed class ServerConnectionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>SSH session to the target server. Root commands go through sudo when the login user is not root.</summary>
public sealed class ServerConnection : IAsyncDisposable
{
    private enum Elevation { None, Root, SudoNoPassword, SudoPassword }

    private readonly ServerLogin _login;
    private readonly Action<string> _log;
    private readonly Action<ConnectStage> _stage;
    private readonly SshClient _client;
    private Elevation _elevation = Elevation.None;

    public string LoginUser { get; private set; } = "";
    public string HostKeyFingerprint { get; private set; } = "";
    public OsInfo? Os { get; private set; }
    public bool IsRoot => _elevation != Elevation.None;
    public bool IsConnected => _client.IsConnected;
    public ServerLogin Login => _login;

    private ServerConnection(ServerLogin login, Action<string> log, Action<ConnectStage> stage)
    {
        _login = login;
        _log = log;
        _stage = stage;

        var keyboard = new KeyboardInteractiveAuthenticationMethod(login.User);
        // Ubuntu and Debian often only offer keyboard-interactive (PAM) for password logins.
        keyboard.AuthenticationPrompt += (_, e) =>
        {
            foreach (AuthenticationPrompt prompt in e.Prompts) prompt.Response = login.Password;
        };
        var info = new ConnectionInfo(login.Host, login.Port, login.User,
            new PasswordAuthenticationMethod(login.User, login.Password), keyboard)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        // Installs run for minutes; keep-alives stop routers and firewalls from dropping an idle-looking session.
        _client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        _client.HostKeyReceived += (_, e) =>
        {
            HostKeyFingerprint = $"{e.HostKeyName} SHA256:{e.FingerPrintSHA256}";
            e.CanTrust = true;
        };
    }

    /// <summary>Logs in, checks the distribution and verifies root access.</summary>
    public static async Task<ServerConnection> ConnectAsync(ServerLogin login, Action<string> log, Action<ConnectStage> stage,
        CancellationToken token = default)
    {
        var connection = new ServerConnection(login, log, stage);
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task OpenAsync(CancellationToken token)
    {
        _stage(ConnectStage.Login);
        _log($"> Connecting to {_login.Host}:{_login.Port} as {_login.User} ...");
        try
        {
            await _client.ConnectAsync(token);
        }
        catch (SshAuthenticationException ex)
        {
            throw new ServerConnectionException($"Login refused for user '{_login.User}': wrong user name or password.", ex);
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or TimeoutException or SshOperationTimeoutException)
        {
            throw new ServerConnectionException($"Could not reach {_login.Host}:{_login.Port}: {ex.Message}", ex);
        }
        _log($"  Connected. Host key {HostKeyFingerprint}");

        LoginUser = (await RunAsync("id -un", token)).Output.Trim();
        string uid = (await RunAsync("id -u", token)).Output.Trim();
        _log($"  Logged in as '{LoginUser}' (uid {uid}).");

        _stage(ConnectStage.OperatingSystem);
        Os = ParseOsRelease((await RunAsync("cat /etc/os-release", token)).Output);
        _log($"  Operating system: {Os.PrettyName}");
        if (!Os.IsSupported)
            throw new ServerConnectionException($"'{Os.PrettyName}' is not supported. This installer only supports Ubuntu Server and Debian.");

        _stage(ConnectStage.Root);
        if (uid == "0")
        {
            _elevation = Elevation.Root;
            _log("  Already root, no sudo needed.");
        }
        else
        {
            await ElevateAsync(token);
        }

        CommandResult check = await RunAsRootAsync("id -un", token);
        if (!check.Succeeded || check.Output.Trim() != "root")
            throw new ServerConnectionException("Switching to root did not work: " + FirstLine(check.Error, check.Output));
        _log("  Root access confirmed.");
    }

    private async Task ElevateAsync(CancellationToken token)
    {
        _log($"> '{LoginUser}' is not root, switching to root with sudo ...");
        if (!(await RunAsync("command -v sudo", token)).Succeeded)
            throw new ServerConnectionException("sudo is not installed on the server. Log in as root, or install sudo and add the user to the 'sudo' group.");

        if ((await RunAsync("sudo -n true", token)).Succeeded)
        {
            _elevation = Elevation.SudoNoPassword;
            _log("  sudo works without a password.");
            return;
        }

        CommandResult result = await RunAsync("sudo -S -p '' -k id -u", token, _login.Password + "\n");
        if (!result.Succeeded || result.Output.Trim() != "0")
        {
            string reason = FirstLine(result.Error, result.Output);
            throw new ServerConnectionException(
                $"sudo refused '{LoginUser}'" + (reason.Length > 0 ? $": {reason}" : ".") +
                " Make sure the user is in the 'sudo' group (usermod -aG sudo " + LoginUser + ").");
        }
        _elevation = Elevation.SudoPassword;
        _log("  sudo accepted the password.");
    }

    public async Task<CommandResult> RunAsync(string command, CancellationToken token = default, string? input = null)
    {
        // A Windows line ending would end up in the command ("set -e\r").
        command = command.ReplaceLineEndings("\n");
        using SshCommand ssh = _client.CreateCommand(command);
        Task execution = ssh.ExecuteAsync(token);
        if (input is not null)
        {
            await using Stream stdin = ssh.CreateInputStream();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(input);
            await stdin.WriteAsync(bytes, token);
        }
        await execution;
        return new CommandResult(ssh.ExitStatus ?? -1, ssh.Result, ssh.Error);
    }

    public Task<CommandResult> RunAsRootAsync(string command, CancellationToken token = default)
    {
        (string commandLine, string? input) = RootCommandLine(command);
        return RunAsync(commandLine, token, input);
    }

    /// <summary>
    /// Streams stdout and stderr lines to <paramref name="onLine"/> and returns the exit code. Pass secrets in
    /// <paramref name="stdin"/>, never on the command line, where 'ps' shows them.
    /// </summary>
    public async Task<int> RunAsRootStreamingAsync(string script, Action<string> onLine, CancellationToken token = default,
        string? stdin = null)
    {
        (string commandLine, string? input) = RootCommandLine("exec 2>&1\n" + script);
        // sudo -S reads only the password line; everything after it reaches the script.
        if (stdin is not null) input = (input ?? "") + stdin;
        using SshCommand ssh = _client.CreateCommand(commandLine.ReplaceLineEndings("\n"));
        Task execution = ssh.ExecuteAsync(token);
        if (input is not null)
        {
            await using Stream inputStream = ssh.CreateInputStream();
            await inputStream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(input), token);
        }
        Task reading = Task.Run(async () =>
        {
            using var reader = new StreamReader(ssh.OutputStream, System.Text.Encoding.UTF8);
            while (await reader.ReadLineAsync(token) is { } line) onLine(line.TrimEnd('\r'));
        }, token);
        await execution;
        await reading;
        // Only sudo itself can still write to stderr; the script's stderr is redirected.
        foreach (string line in ssh.Error.Split('\n', StringSplitOptions.RemoveEmptyEntries)) onLine(line.TrimEnd('\r'));
        return ssh.ExitStatus ?? -1;
    }

    private (string CommandLine, string? Input) RootCommandLine(string command)
    {
        string quoted = ShellQuote(command);
        return _elevation switch
        {
            Elevation.Root => ($"bash -c {quoted}", null),
            Elevation.SudoNoPassword => ($"sudo -n -H -- bash -c {quoted}", null),
            // Password over stdin, so 'ps' never shows it.
            Elevation.SudoPassword => ($"sudo -S -p '' -H -- bash -c {quoted}", _login.Password + "\n"),
            _ => throw new InvalidOperationException("The connection has no root access yet.")
        };
    }

    public static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static OsInfo ParseOsRelease(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in text.Split('\n'))
        {
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            values[line[..equals].Trim()] = line[(equals + 1)..].Trim().Trim('"');
        }
        string id = values.GetValueOrDefault("ID", "unknown").ToLowerInvariant();
        return new OsInfo(id, values.GetValueOrDefault("VERSION_ID", ""), values.GetValueOrDefault("PRETTY_NAME", id));
    }

    private static string FirstLine(params string[] texts) =>
        texts.SelectMany(t => t.Split('\n')).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    public ValueTask DisposeAsync()
    {
        try { if (_client.IsConnected) _client.Disconnect(); } catch { }
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
