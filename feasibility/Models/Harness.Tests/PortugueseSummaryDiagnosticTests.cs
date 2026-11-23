using Xunit;

namespace Models.Harness.Tests;

[Trait("type", "IntegrationTests")]
[Trait("purpose", "OriginalPortugueseSummary")]
public sealed class PortugueseSummaryDiagnosticTests
{
    [Fact]
    public Task Summary_FrozenPortugueseMinute_ExecutesOriginalOperationOnly() =>
        new RealModelTests().Summary_ApprovedBilingualMinute_PersistsInitialUnverifiedClaimLedger("pt-BR");
}
