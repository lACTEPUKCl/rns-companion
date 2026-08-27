using System.IO;
using System.IO.Pipes;
using System.Text;

namespace RnsCompanion.Services;

/// <summary>
/// Single-instance: именованный мьютекс + named pipe. Второй экземпляр
/// передаёт свой аргумент (protocol URI) первому и завершается.
/// </summary>
internal sealed class SingleInstanceService : IDisposable
{
    private readonly string _pipeName;
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _handlerSync = new();
    private readonly Queue<string> _pendingMessages = new();
    private Action<string>? _messageReceived;

    public bool IsPrimary { get; }
    public event Action<string> MessageReceived
    {
        add
        {
            List<string> pending;
            lock (_handlerSync)
            {
                _messageReceived += value;
                pending = _pendingMessages.ToList();
                _pendingMessages.Clear();
            }
            foreach (var message in pending) value(message);
        }
        remove
        {
            lock (_handlerSync) _messageReceived -= value;
        }
    }

    public SingleInstanceService(string name)
    {
        _pipeName = name;
        _mutex = new Mutex(true, name, out var created);
        IsPrimary = created;
        if (created)
            _ = ListenAsync();
    }

    /// <summary>Передать команду первому экземпляру. Сразу после пробуждения ПК
    /// (запуск из планировщика в 06:00) слушатель первого может быть ещё не готов —
    /// повторяем попытки до ~1 минуты, прежде чем сдаться.</summary>
    public void Forward(string message)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut);
                client.Connect(3000);
                using var writer = new StreamWriter(client, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
                writer.WriteLine(message);
                // Не завершаем экземпляр из планировщика, пока основной экземпляр
                // реально не обработал команду. Иначе после wake-таймера Windows
                // может снова уснуть, оставив /scheduled в очереди UI до утра.
                using var reader = new StreamReader(client, Encoding.UTF8, leaveOpen: true);
                client.ReadTimeout = 30000;
                if (!string.Equals(reader.ReadLine(), "OK", StringComparison.Ordinal))
                    throw new IOException("Основной экземпляр не подтвердил команду.");
                return;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                if (attempt >= 20) throw;
                Thread.Sleep(3000);
            }
        }
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                if (await reader.ReadLineAsync(_stop.Token) is { } message)
                {
                    Action<string>? handler;
                    lock (_handlerSync)
                    {
                        handler = _messageReceived;
                        if (handler is null) _pendingMessages.Enqueue(message);
                    }
                    handler?.Invoke(message);
                    await using var writer = new StreamWriter(server, Encoding.UTF8, leaveOpen: true)
                        { AutoFlush = true };
                    await writer.WriteLineAsync("OK");
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
            catch (IOException) { }
            catch (Exception ex)
            {
                // Слушатель не должен умирать молча от непредвиденного исключения —
                // иначе проброс /scheduled от планировщика теряется без следа.
                LogService.Warn($"SingleInstance: ошибка слушателя пайпа: {ex.GetType().Name}: {ex.Message}");
                try { await Task.Delay(1000, _stop.Token); }
                catch (OperationCanceledException) { }
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (IsPrimary)
        {
            try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
        }
        _mutex.Dispose();
        _stop.Dispose();
    }
}
