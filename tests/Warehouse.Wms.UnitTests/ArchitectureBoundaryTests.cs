using System.Linq;
using Xunit;

namespace Warehouse.Wms.UnitTests;

public sealed class ArchitectureBoundaryTests
{
    [Fact]
    public void Domain_assembly_does_not_reference_infrastructure_or_transport_packages()
    {
        var references = typeof(Warehouse.Wms.Domain.AssemblyMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("Microsoft.EntityFrameworkCore", references);
        Assert.DoesNotContain("Microsoft.Data.SqlClient", references);
        Assert.DoesNotContain("System.Net.Http", references);
        Assert.DoesNotContain("Warehouse.Wms.Infrastructure", references);
    }

    [Fact]
    public void Application_assembly_references_domain_but_not_api_or_infrastructure()
    {
        var references = typeof(Warehouse.Wms.Application.AssemblyMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("Warehouse.Wms.Domain", references);
        Assert.DoesNotContain("Warehouse.Wms.Api", references);
        Assert.DoesNotContain("Warehouse.Wms.Infrastructure", references);
    }
}
