using System.Linq;
using Xunit;

namespace Warehouse.DeviceGateway.ContractTests;

public sealed class GatewayBoundaryTests
{
    [Fact]
    public void Device_gateway_assembly_exposes_no_legacy_database_or_plc_client_reference()
    {
        var references = typeof(Warehouse.Wms.DeviceGateway.AssemblyMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", references);
        Assert.DoesNotContain("Microsoft.Data.SqlClient", references);
        Assert.DoesNotContain("NModbus", references);
        Assert.DoesNotContain("Warehouse.Wms.Infrastructure", references);
    }
}
