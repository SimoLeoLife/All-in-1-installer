using System.Text;
using AllInOneInstaller.Remote;
using Renci.SshNet;

namespace AllInOneInstaller.Terminal;

/// <summary>An SSH shell on a <see cref="ServerConnection"/>, its output fed into a <see cref="TerminalEmulator"/>.</summary>
public sealed class TerminalSession : IDisposable
{
    private readonly ShellStream _shell;
    private readonly Thread _reader;
    private volatile bool _closed;

    public TerminalEmulator Emulator { get; }
    /// <summary>New output was processed (raised on the reader thread).</summary>
    public event Action? Output;
    /// <summary>The shell ended (exit, or the connection dropped); raised once, on the reader thread.</summary>
    public event Action? Closed;
    public bool IsClosed => _closed;

    public TerminalSession(ServerConnection connection, int columns, int rows)
    {
        Emulator = new TerminalEmulator(columns, rows);
        Emulator.Reply += Send;
        _shell = connection.OpenShell(columns, rows);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "SSH terminal" };
        _reader.Start();
    }

    private void ReadLoop()
    {
        var buffer = new byte[16384];
        try
        {
            while (true)
            {
                int read = _shell.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                lock (Emulator.Sync) Emulator.Feed(buffer.AsSpan(0, read));
                Output?.Invoke();
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or Renci.SshNet.Common.SshException or InvalidOperationException)
        {
        }
        _closed = true;
        Closed?.Invoke();
    }

    public void Send(string text) => Send(Encoding.UTF8.GetBytes(text));

    public void Send(byte[] data)
    {
        if (_closed) return;
        try
        {
            _shell.Write(data, 0, data.Length);
            _shell.Flush();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or Renci.SshNet.Common.SshException or InvalidOperationException)
        {
        }
    }

    public void Resize(int columns, int rows)
    {
        lock (Emulator.Sync) Emulator.Resize(columns, rows);
        if (_closed) return;
        try { _shell.ChangeWindowSize((uint)columns, (uint)rows, 0, 0); }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or Renci.SshNet.Common.SshException or InvalidOperationException) { }
    }

    public void Dispose()
    {
        _closed = true;
        try { _shell.Dispose(); } catch { }
    }
}
