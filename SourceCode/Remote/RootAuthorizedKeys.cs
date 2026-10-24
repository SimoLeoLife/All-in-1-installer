using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace AllInOneInstaller.Remote;

public enum AuthorizedKeysMode
{
    Replace,
    Add
}

public static class RootAuthorizedKeys
{
    public const string FilePath = "/root/.ssh/authorized_keys";

    // Empty when the file does not exist.
    public static async Task<List<AuthorizedKey>> ReadAsync(ServerConnection connection, CancellationToken token = default)
    {
        CommandResult result = await connection.RunAsRootAsync($"if [ -f {FilePath} ]; then cat {FilePath}; fi", token);
        if (!result.Succeeded)
            throw new ServerConnectionException($"Could not read {FilePath}: {result.Error.Trim()}");
        return SshKeys.ParseAuthorizedKeys(result.Output);
    }

    /// <summary>Backs up the current file first so a replace can be undone. Returns the backup path, if any.</summary>
    public static async Task<string?> InstallAsync(ServerConnection connection, AuthorizedKey key, AuthorizedKeysMode mode,
        CancellationToken token = default)
    {
        string line = ServerConnection.ShellQuote(key.Line);
        string write = mode == AuthorizedKeysMode.Replace
            ? $"printf '%s\\n' {line} > \"$f\""
            // Append only if absent, adding a newline first if the file lacks a trailing one.
            : $"if ! grep -qF -- {ServerConnection.ShellQuote(key.Base64)} \"$f\" 2>/dev/null; then " +
              $"if [ -s \"$f\" ] && [ -n \"$(tail -c1 \"$f\")\" ]; then echo >> \"$f\"; fi; printf '%s\\n' {line} >> \"$f\"; fi";

        // Not AppendLine: on Windows that adds \r\n and bash sees "set -e\r".
        string script = string.Join('\n',
        [
            "set -e",
            "umask 077",
            $"f={FilePath}",
            "install -d -m 700 -o root -g root /root/.ssh",
            "backup=",
            "if [ -s \"$f\" ]; then backup=$(mktemp \"$f.bak-$(date +%Y%m%d-%H%M%S)-XXXX\"); cp -p \"$f\" \"$backup\"; fi",
            write,
            "chmod 600 \"$f\"",
            "chown root:root \"$f\"",
            // In case SELinux is enabled.
            "if command -v restorecon >/dev/null 2>&1; then restorecon -R /root/.ssh || true; fi",
            "echo \"$backup\""
        ]) + "\n";

        CommandResult result = await connection.RunAsRootAsync(script, token);
        if (!result.Succeeded)
            throw new ServerConnectionException($"Could not write {FilePath}: {FirstLine(result.Error)}");
        string backup = result.Output.Trim();
        return backup.Length > 0 ? backup : null;
    }

    /// <summary>The effective sshd PermitRootLogin value, or null if unknown.</summary>
    public static async Task<string?> PermitRootLoginAsync(ServerConnection connection, CancellationToken token = default)
    {
        CommandResult result = await connection.RunAsRootAsync("sshd -T 2>/dev/null | awk '$1==\"permitrootlogin\"{print $2}'", token);
        string value = result.Output.Trim();
        return value.Length > 0 ? value : null;
    }

    /// <summary>Logs in as root with the key in a second session. Returns null on success, else the reason.</summary>
    public static async Task<string?> TestKeyLoginAsync(ServerConnection connection, string privateKeyPem, string passphrase,
        CancellationToken token = default)
    {
        ServerLogin login = connection.Login;
        try
        {
            using var keyStream = new MemoryStream(Encoding.ASCII.GetBytes(privateKeyPem));
            using var keyFile = new PrivateKeyFile(keyStream, passphrase.Length > 0 ? passphrase : null);
            var info = new ConnectionInfo(login.Host, login.Port, "root", new PrivateKeyAuthenticationMethod("root", keyFile))
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            using var client = new SshClient(info);
            // Trust only the host key of the existing connection.
            client.HostKeyReceived += (_, e) => e.CanTrust = $"{e.HostKeyName} SHA256:{e.FingerPrintSHA256}" == connection.HostKeyFingerprint;
            await client.ConnectAsync(token);
            using SshCommand command = client.CreateCommand("id -un");
            await command.ExecuteAsync(token);
            string user = command.Result.Trim();
            client.Disconnect();
            return user == "root" ? null : $"logged in as '{user}' instead of root";
        }
        catch (SshAuthenticationException)
        {
            return "the server refused the key";
        }
        catch (Exception ex) when (ex is SshException or System.Net.Sockets.SocketException or TimeoutException)
        {
            return ex.Message;
        }
    }

    private static string FirstLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "unknown error";
}
