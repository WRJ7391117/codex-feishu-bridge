using System.Runtime.ExceptionServices;

namespace DeepOriBridge.Services;

internal static class FeishuRouteClaimProjection
{
    internal static T ClaimAndProject<T>(
        Func<T> claim,
        Func<FeishuRouteGateState> readState,
        Action<FeishuRouteGateState, Exception?> project)
    {
        T? result = default;
        Exception? claimFailure = null;
        try
        {
            result = claim();
        }
        catch (Exception exception)
        {
            claimFailure = exception;
        }

        FeishuRouteGateState state = readState();
        try
        {
            project(state, claimFailure);
        }
        catch when (claimFailure is not null)
        {
            // The coordinator has already failed closed. Never replace that reason
            // with a secondary UI projection failure.
        }

        if (claimFailure is not null)
        {
            ExceptionDispatchInfo.Capture(claimFailure).Throw();
        }

        return result!;
    }
}
