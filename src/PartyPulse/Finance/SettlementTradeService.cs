using System.Threading;
using System.Threading.Tasks;
using PartyPulse.Api;

namespace PartyPulse.Finance;

public sealed class SettlementTradeService
{
    public Task InitiateTradeAsync(
        CreateVipSettlementResponse settlement,
        CancellationToken cancellationToken)
    {
        // Intentionally empty. The third-party trade integration will be added here.
        // The settlement is already recorded as pending before this method is called.
        return Task.CompletedTask;
    }
}
