using System.Collections.Concurrent;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;

// Diagnostic/reconnection only: no unpairing, adapter reset, HID writes or GATT writes.
// The address must be explicitly supplied from the verified local PnP identity.
if (args.Length != 1 || args[0].Length != 12 || !ulong.TryParse(args[0],
    System.Globalization.NumberStyles.HexNumber, null, out ulong address))
{
    Console.Error.WriteLine("Usage: PromLightConnectionProbe <12-digit verified Bluetooth address>");
    return 2;
}
void Report(string stage, object details) => Console.WriteLine(JsonSerializer.Serialize(new
    { at = DateTimeOffset.Now, stage, details }));
using var lamp = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
if (lamp is null || lamp.Name != "PromLight" || !lamp.DeviceInformation.Pairing.IsPaired)
{
    Report("identity-rejected", new { reason = "Expected an already-paired PromLight. No connection requested." });
    return 3;
}
Report("before", new { connection = lamp.ConnectionStatus.ToString(), paired = true });
var packets = new ConcurrentQueue<object>();
int totalPackets = 0;
var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
watcher.Received += (_, packet) =>
{
    Interlocked.Increment(ref totalPackets);
    if (packet.BluetoothAddress == address)
        packets.Enqueue(new { rssi = packet.RawSignalStrengthInDBm,
            type = packet.AdvertisementType.ToString(), name = packet.Advertisement.LocalName });
};
watcher.Stopped += (_, stopped) => Report("scan-stopped", new { error = stopped.Error.ToString() });
using var session = await GattSession.FromDeviceIdAsync(lamp.BluetoothDeviceId);
try
{
    watcher.Start();
    if (session is not null && session.CanMaintainConnection) session.MaintainConnection = true;
    for (int tick = 1; tick <= 6; tick++)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        var devices = await DeviceInformation.FindAllAsync(
            HidDevice.GetDeviceSelector(0xFF00, 0x0001, 0x07D7, 0x6B01));
        int matching = devices.Count(device => device.Id.Contains(args[0], StringComparison.OrdinalIgnoreCase));
        Report("sample", new { elapsedSeconds = tick * 5,
            connection = lamp.ConnectionStatus.ToString(), session = session?.SessionStatus.ToString(),
            watcher = watcher.Status.ToString(), totalPackets, lampPackets = packets.Count,
            matchingHidInterfaces = matching });
        if (matching > 0 && lamp.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            Report("interface-recovered", new { lightingVerified = false,
                next = "Verify a fresh Bridge HID write and the physical light." });
            return 0;
        }
    }
    Report("not-recovered", new { lampPackets = packets.ToArray(),
        pairingPreserved = true, adapterUnchanged = true, lightingVerified = false });
    return 4;
}
finally
{
    watcher.Stop();
    if (session is not null) session.MaintainConnection = false;
}
