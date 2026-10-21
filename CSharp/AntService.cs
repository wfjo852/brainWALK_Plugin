using System.Linq.Expressions;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace BrainWalk;

internal readonly record struct AntDevice(int Id, int Type, int TransmissionType)
{
    public override string ToString() => $"ID={Id}  Type={Type}  Trans={TransmissionType}";
}

// The desktop ANT Managed Library is distributed as ANT_NET.dll in the PC SDK.
internal sealed class AntService : IDisposable
{
    private static readonly byte[] NetworkKey = [0xB9, 0xA5, 0x21, 0xFB, 0xBD, 0x72, 0xC3, 0x45];
    private CancellationTokenSource? _stop;
    private Task? _task;
    private object? _device;
    private object? _channel;
    private EventInfo? _responseEvent;
    private Delegate? _responseHandler;
    private bool _scan;
    private readonly HashSet<AntDevice> _found = [];
    private readonly object _bpmLock = new();
    private DateTime _lastDataAt = DateTime.MinValue;
    private DateTime _lastBpmSentAt = DateTime.MinValue;
    private bool _zeroPublished;
    private readonly UdpClient _udp = new();

    public event Action<AntDevice>? DeviceFound;
    public event Action<int>? BpmReceived;
    public event Action<string>? Status;
    public bool IsRunning => _task is { IsCompleted: false };
    public AntDevice SelectedDevice { get; set; } = new(0, 120, 0);
    public string UdpHost { get; set; } = "127.0.0.1";
    public int UdpPort { get; set; } = 5005;

    public void Start(bool scan = false, int scanSeconds = 10)
    {
        if (IsRunning) return;
        _scan = scan;
        _found.Clear();
        lock (_bpmLock)
        {
            _lastDataAt = DateTime.UtcNow;
            _lastBpmSentAt = DateTime.MinValue;
            _zeroPublished = false;
        }
        _stop = new CancellationTokenSource();
        _task = Task.Run(() => Run(scanSeconds, _stop.Token));
    }

    private async Task Run(int scanSeconds, CancellationToken token)
    {
        bool failed = false;
        try
        {
            string? assemblyPath = new[] { "ANT_Managed_Library.dll", "ANT_NET.dll" }
                .Select(name => Path.Combine(AppContext.BaseDirectory, name))
                .FirstOrDefault(File.Exists);
            if (assemblyPath is null)
                throw new FileNotFoundException("ANT_Managed_Library.dll 또는 ANT_NET.dll을 실행 파일 폴더에 넣어주세요.");

            var assembly = Assembly.LoadFrom(assemblyPath);
            var deviceType = assembly.GetType("ANT_Managed_Library.ANT_Device", throwOnError: true)!;
            var referenceType = assembly.GetType("ANT_Managed_Library.ANT_ReferenceLibrary", throwOnError: true)!;
            var channelType = referenceType.GetNestedType("ChannelType")
                ?? throw new MissingMemberException("ANT ChannelType not found");

            _device = Activator.CreateInstance(deviceType)
                ?? throw new IOException("ANT USB device initialization failed");
            _channel = Call(_device, "getChannel", 0)
                ?? throw new IOException("ANT channel 0 unavailable");
            _responseEvent = _channel.GetType().GetEvent("channelResponse")
                ?? throw new MissingMemberException("ANT channelResponse event not found");
            _responseHandler = CreateResponseHandler(_responseEvent.EventHandlerType!);
            _responseEvent.AddEventHandler(_channel, _responseHandler);

            Require(Call(_device, "setNetworkKey", (byte)0, NetworkKey, (uint)500), "network key");
            var slave = Enum.ToObject(channelType, (byte)0);
            Require(Call(_channel, "assignChannel", slave, (byte)0, (uint)500), "channel assignment");
            var target = _scan ? new AntDevice(0, 0, 0) : SelectedDevice;
            Require(Call(_channel, "setChannelID", (ushort)target.Id, false,
                (byte)target.Type, (byte)target.TransmissionType, (uint)500), "channel ID");
            Require(Call(_channel, "setChannelPeriod", (ushort)8070, (uint)500), "channel period");
            Require(Call(_channel, "setChannelFreq", (byte)57, (uint)500), "RF frequency");
            if (_scan)
            {
                Require(Call(_device, "enableRxExtendedMessages", true, (uint)500), "extended messages");
                Require(Call(_device, "openRxScanMode", (uint)500), "scan mode");
            }
            else Require(Call(_channel, "openChannel", (uint)500), "channel open");

            Status?.Invoke(_scan ? "ANT 장비 검색 중" : "ANT 심박 수신 중");
            if (_scan) await Task.Delay(TimeSpan.FromSeconds(scanSeconds), token);
            else await WatchBpmTimeoutAsync(token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { failed = true; Status?.Invoke($"ANT 오류: {ex.GetBaseException().Message}"); }
        finally
        {
            if (_responseEvent is not null && _channel is not null && _responseHandler is not null)
                try { _responseEvent.RemoveEventHandler(_channel, _responseHandler); } catch { }
            if (_channel is not null)
            {
                try { Call(_channel, "closeChannel", (uint)500); } catch { }
                try { Call(_channel, "unassignChannel", (uint)500); } catch { }
            }
            if (_device is IDisposable disposable)
                try { disposable.Dispose(); } catch { }
            _device = null; _channel = null; _responseEvent = null; _responseHandler = null;
            if (!failed) Status?.Invoke(_scan ? "ANT 검색 완료" : "ANT 수신 중지");
        }
    }

    private Delegate CreateResponseHandler(Type handlerType)
    {
        var invoke = handlerType.GetMethod("Invoke")!;
        var response = Expression.Parameter(invoke.GetParameters()[0].ParameterType, "response");
        var callback = typeof(AntService).GetMethod(nameof(OnResponse), BindingFlags.Instance | BindingFlags.NonPublic)!;
        return Expression.Lambda(handlerType,
            Expression.Call(Expression.Constant(this), callback, Expression.Convert(response, typeof(object))),
            response).Compile();
    }

    private void OnResponse(object response)
    {
        try
        {
            var type = response.GetType();
            byte id = (byte)type.GetField("responseID")!.GetValue(response)!;
            if (id is not (0x4E or 0x4F or 0x50 or 0x5D or 0x5E or 0x5F)) return;
            if (_scan)
            {
                var ext = Call(response, "getDeviceIDfromExt");
                if (ext is null) return;
                var extType = ext.GetType();
                var found = new AntDevice(
                    Convert.ToInt32(extType.GetField("deviceNumber")!.GetValue(ext)),
                    Convert.ToInt32(extType.GetField("deviceTypeID")!.GetValue(ext)),
                    Convert.ToInt32(extType.GetField("transmissionTypeID")!.GetValue(ext)));
                lock (_found) if (_found.Add(found)) DeviceFound?.Invoke(found);
            }
            else
            {
                var payload = (byte[]?)Call(response, "getDataPayload");
                if (payload is not { Length: >= 8 }) return;
                int bpm = payload[7];
                bool publish;
                lock (_bpmLock)
                {
                    var now = DateTime.UtcNow;
                    _lastDataAt = now;
                    _zeroPublished = false;
                    publish = (now - _lastBpmSentAt).TotalSeconds >= 1;
                    if (publish) _lastBpmSentAt = now;
                }
                if (publish) PublishBpm(bpm);
            }
        }
        catch (Exception ex) { Status?.Invoke($"ANT 데이터 오류: {ex.GetBaseException().Message}"); }
    }

    private async Task WatchBpmTimeoutAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await Task.Delay(250, token);
            bool publishZero = false;
            lock (_bpmLock)
            {
                if (!_zeroPublished && (DateTime.UtcNow - _lastDataAt).TotalSeconds >= 5)
                {
                    _zeroPublished = true;
                    publishZero = true;
                }
            }
            if (publishZero) PublishBpm(0);
        }
    }

    private void PublishBpm(int bpm)
    {
        BpmReceived?.Invoke(bpm);
        var data = Encoding.UTF8.GetBytes($"{{\"bpm\":{bpm}}}");
        _udp.Send(data, data.Length, UdpHost, UdpPort);
    }

    private static object? Call(object target, string method, params object[] args)
    {
        var info = target.GetType().GetMethod(method, args.Select(x => x.GetType()).ToArray())
            ?? throw new MissingMethodException(target.GetType().FullName, method);
        return info.Invoke(target, args);
    }

    private static void Require(object? result, string operation)
    {
        if (result is bool success && !success) throw new IOException($"ANT {operation} failed");
    }

    public void Stop() => _stop?.Cancel();
    public void Dispose() { Stop(); _udp.Dispose(); }
}
