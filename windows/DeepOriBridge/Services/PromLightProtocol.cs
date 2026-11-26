using DeepOriBridge.Models;

namespace DeepOriBridge.Services;

internal static class PromLightProtocol
{
    internal const ushort VendorId = 0x07D7;
    internal const ushort ProductId = 0x6B01;
    internal const ushort UsagePage = 0xFF00;
    internal const ushort UsageId = 0x0001;
    internal const ushort OutputReportId = 0x0002;
    internal const int ReportSize = 64;
    internal const int SupportedReleaseNumber = 0x0013;

    internal static bool NeedsRefresh(PromLightActivitySnapshot activity, PromLightStatus desired, DateTimeOffset now)
        => activity.SubmittedAt is null || activity.Status != desired
            || now - activity.SubmittedAt.Value >= TimeSpan.FromSeconds(15);

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
}
