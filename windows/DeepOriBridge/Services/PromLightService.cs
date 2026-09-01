using System.Globalization;
using System.Text.RegularExpressions;
using DeepOriBridge.Models;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DeepOriBridge.Services;

internal sealed partial class PromLightService : IAsyncDisposable
{
    internal const ushort VendorId = 0x07D7;
    internal const ushort ProductId = 0x6B01;
    internal const ushort UsagePage = 0xFF00;
    internal const ushort UsageId = 0x0001;
    internal const ushort OutputReportId = 0x0002;
    internal const int ReportSize = 64;
    internal const int SupportedReleaseNumber = 0x0013;

    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private HidDevice? _device;

    public async Task<PromLightDeviceSnapshot> DiscoverAsync(CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            _device?.Dispose();
            _device = null;

            string selector = HidDevice.GetDeviceSelector(
                UsagePage,
                UsageId,
                VendorId,
                ProductId);
            string[] requestedProperties = ["System.Devices.HardwareIds"];
            DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(
                    selector,
                    requestedProperties)
                .AsTask(cancellationToken);
            if (devices.Count == 0)
            {
                return new PromLightDeviceSnapshot(
                    false,
                    false,
                    0,
                    string.Empty,
                    string.Empty,
                    0,
                    "未发现匹配 FF00:0001 的 PromLight HID 接口。",
                    DateTimeOffset.Now);
            }

            DeviceInformation selected = devices.FirstOrDefault(device => device.IsEnabled)
                ?? devices[0];
            int releaseNumber = ParseReleaseNumber(selected);
            _device = await HidDevice.FromIdAsync(selected.Id, FileAccessMode.ReadWrite)
                .AsTask(cancellationToken);

            if (_device is null)
            {
                return new PromLightDeviceSnapshot(
                    true,
                    false,
                    devices.Count,
                    "PromLight",
                    DeviceVersion(releaseNumber),
                    releaseNumber,
                    "已发现 PromLight，但 Windows 未授予 HID 读写句柄。",
                    DateTimeOffset.Now);
            }

            return new PromLightDeviceSnapshot(
                true,
                true,
                devices.Count,
                "PromLight",
                DeviceVersion(releaseNumber),
                releaseNumber,
                releaseNumber == SupportedReleaseNumber
                    ? null
                    : $"设备 release 0x{releaseNumber:X4} 尚未经过协议验证。",
                DateTimeOffset.Now);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new PromLightDeviceSnapshot(
                false,
                false,
                0,
                string.Empty,
                string.Empty,
                0,
                exception.Message,
                DateTimeOffset.Now);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task<PromLightWriteResult> SendAsync(
        PromLightStatus status,
        CancellationToken cancellationToken)
    {
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            if (_device is null)
            {
                throw new InvalidOperationException("PromLight 尚未打开。请先重新检测设备。");
            }

            byte[] frame = CreateFrame(status);
            HidOutputReport report = _device.CreateOutputReport(OutputReportId);
            int reportLength = checked((int)Math.Max(
                report.Data.Length,
                report.Data.Capacity));
            byte[] reportBytes;
            if (reportLength == frame.Length + 1)
            {
                reportBytes = new byte[reportLength];
                reportBytes[0] = checked((byte)report.Id);
                frame.CopyTo(reportBytes, 1);
            }
            else if (reportLength == frame.Length)
            {
                reportBytes = frame;
            }
            else
            {
                throw new InvalidOperationException(
                    $"PromLight HID 输出报告长度为 {reportLength}，预期为 64 或 65；已拒绝写入。");
            }

            using var writer = new DataWriter();
            writer.WriteBytes(reportBytes);
            report.Data = writer.DetachBuffer();
            uint bytesWritten = await _device.SendOutputReportAsync(report)
                .AsTask(cancellationToken);
            if (bytesWritten == 0)
            {
                throw new IOException("Windows HID 返回 0 字节写入。");
            }

            return new PromLightWriteResult(status, bytesWritten, DateTimeOffset.Now);
        }
        finally
        {
            _operationLock.Release();
        }
    }

    internal static byte[] CreateFrame(PromLightStatus status)
    {
        (byte channel, byte action) = status switch
        {
            PromLightStatus.Idle => ((byte)0x01, (byte)0x01),
            PromLightStatus.Running => ((byte)0x02, (byte)0x01),
            PromLightStatus.HumanGate => ((byte)0x02, (byte)0x02),
            PromLightStatus.Error => ((byte)0x04, (byte)0x02),
            _ => throw new ArgumentOutOfRangeException(nameof(status)),
        };

        var frame = new byte[ReportSize];
        frame[0] = 0x5E;
        frame[1] = 0x5E;
        frame[2] = 0x06;
        frame[3] = 0x04;
        frame[4] = channel;
        frame[5] = action;
        frame[6] = 0x01;
        frame[7] = 0xFF;
        for (int index = 2; index <= 7; index++)
        {
            frame[8] ^= frame[index];
        }

        return frame;
    }

    private static int ParseReleaseNumber(DeviceInformation device)
    {
        string source = device.Id;
        if (device.Properties.TryGetValue(
                "System.Devices.HardwareIds",
                out object? hardwareIdValue))
        {
            source += hardwareIdValue is IEnumerable<string> hardwareIds
                ? string.Join(';', hardwareIds)
                : hardwareIdValue?.ToString();
        }

        Match match = ReleaseNumberPattern().Match(source);
        return match.Success
            && int.TryParse(
                match.Groups[1].Value,
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out int release)
                    ? release
                    : 0;
    }

    private static string DeviceVersion(int releaseNumber) =>
        releaseNumber == SupportedReleaseNumber ? "0.1.3" : string.Empty;

    [GeneratedRegex(@"REV[&#_]([0-9A-Fa-f]{4})", RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseNumberPattern();

    public async ValueTask DisposeAsync()
    {
        await _operationLock.WaitAsync();
        try
        {
            _device?.Dispose();
            _device = null;
        }
        finally
        {
            _operationLock.Release();
            _operationLock.Dispose();
        }
    }
}
