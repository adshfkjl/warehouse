using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Api.Controllers;
using Warehouse.Wms.Application.Import;
using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Outbound;

namespace Warehouse.Wms.IntegrationTests.Import;

public sealed class SpreadsheetImportApiTests
{
    [Fact]
    public async Task Import_endpoint_returns_created_document_without_device_dispatch()
    {
        var inbound = new InboundOrderService();
        var outbound = new OutboundAllocationService(new InventoryService());
        var controller = new ImportsController(new SpreadsheetImportService(inbound, outbound));
        var material = Guid.NewGuid();
        var csv = string.Join('\n',
            "TemplateVersion,1.0",
            "OrderNumber,MaterialId,Quantity,BatchNumber,ExpirationDate,PalletCode,WeightKg",
            $"IB-API-XLSX,{material:D},2,LOT-API,2027-03-31,PALLET-API,10");
        var file = FormFile(csv, "inbound-import.xlsx");

        var action = await controller.Import("inbound", file, "api-source-001", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var result = Assert.IsType<SpreadsheetImportResult>(ok.Value);
        Assert.Equal(SpreadsheetImportStatus.Imported, result.Status);
        Assert.Single(inbound.Orders);
        Assert.Single(inbound.PendingInboundInventory);
        Assert.Empty(outbound.Orders);
    }

    [Fact]
    public async Task Invalid_import_returns_error_report_that_can_be_downloaded()
    {
        var controller = new ImportsController(new SpreadsheetImportService(
            new InboundOrderService(), new OutboundAllocationService(new InventoryService())));
        var file = FormFile("TemplateVersion,9.9\nwrong,headers\n", "broken.xlsx");

        var action = await controller.Import("inbound", file, "api-source-002", CancellationToken.None);

        var response = Assert.IsType<UnprocessableEntityObjectResult>(action);
        var importId = (Guid)response.Value!.GetType().GetProperty("ImportId")!.GetValue(response.Value)!;
        var download = controller.DownloadErrors(importId);
        var fileResult = Assert.IsType<FileContentResult>(download);
        Assert.Contains("TemplateVersion", Encoding.UTF8.GetString(fileResult.FileContents));
    }

    private static FormFile FormFile(string contents, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(contents);
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };
    }
}
