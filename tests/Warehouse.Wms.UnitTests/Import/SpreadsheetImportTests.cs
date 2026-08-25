using System.IO.Compression;
using System.Globalization;
using System.Text;
using Warehouse.Wms.Application.Import;
using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Application.Inventory;
using Warehouse.Wms.Application.Outbound;
using Warehouse.Wms.Domain.Inventory;
using Warehouse.Wms.Domain.Inbound;
using Warehouse.Wms.Domain.Outbound;

namespace Warehouse.Wms.UnitTests.Import;

public sealed class SpreadsheetImportTests
{
    [Fact]
    public async Task Inbound_xlsx_import_creates_orders_and_pending_receipts_without_device_submission()
    {
        var inbound = new InboundOrderService();
        var outbound = new OutboundAllocationService(new InventoryService());
        var service = new SpreadsheetImportService(inbound, outbound);
        var material = Guid.NewGuid();
        var bytes = Xlsx("Inbound", [
            ["IB-XLSX-001", material.ToString("D"), "5", "LOT-1", "2027-03-31", "PALLET-X1", "12.5"]
        ]);

        var result = await service.ImportAsync(
            new MemoryStream(bytes),
            new SpreadsheetImportRequest(SpreadsheetImportType.Inbound, "hand-import-001"));

        Assert.Equal(SpreadsheetImportStatus.Imported, result.Status);
        Assert.Single(inbound.Orders);
        Assert.Equal(InboundState.Received, inbound.Orders.Single().State);
        Assert.Single(inbound.PendingInboundInventory);
        Assert.Null(inbound.PendingInboundInventory.Single().LocationId);
        Assert.Empty(outbound.Orders);
        Assert.Empty(result.ErrorReport.Errors);
    }

    [Fact]
    public async Task Outbound_xlsx_import_creates_draft_order_only_and_does_not_lock_inventory()
    {
        var inbound = new InboundOrderService();
        var inventory = new InventoryService();
        var outbound = new OutboundAllocationService(inventory);
        var service = new SpreadsheetImportService(inbound, outbound);
        var material = Guid.NewGuid();
        var bytes = Xlsx("Outbound", [
            ["OB-XLSX-001", material.ToString("D"), "2", "LOT-2", "", ""]
        ]);

        var result = await service.ImportAsync(
            new MemoryStream(bytes),
            new SpreadsheetImportRequest(SpreadsheetImportType.Outbound, "hand-import-002"));

        Assert.Equal(SpreadsheetImportStatus.Imported, result.Status);
        var order = Assert.Single(outbound.Orders);
        Assert.Equal(OutboundState.Draft, order.State);
        Assert.Equal(2m, order.Lines.Single().RequestedQuantity);
        Assert.Empty(inventory.GetTransactions());
    }

    [Fact]
    public async Task Wrong_template_version_returns_a_structured_error_without_creating_any_order()
    {
        var inbound = new InboundOrderService();
        var outbound = new OutboundAllocationService(new InventoryService());
        var service = new SpreadsheetImportService(inbound, outbound);
        var bytes = Xlsx("Inbound", [["IB-XLSX-003", Guid.NewGuid().ToString("D"), "1", "LOT-3", "", "PALLET-X3", "1"]], "9.9");

        var result = await service.ImportAsync(
            new MemoryStream(bytes),
            new SpreadsheetImportRequest(SpreadsheetImportType.Inbound, "wrong-version"));

        Assert.Equal(SpreadsheetImportStatus.Rejected, result.Status);
        var error = Assert.Single(result.ErrorReport.Errors);
        Assert.Equal("TEMPLATE_VERSION_UNSUPPORTED", error.Code);
        Assert.Equal(1, error.RowNumber);
        Assert.Equal("TemplateVersion", error.Column);
        Assert.Empty(inbound.Orders);
    }

    [Fact]
    public async Task Any_invalid_row_rejects_the_whole_workbook_before_creating_partial_orders()
    {
        var inbound = new InboundOrderService();
        var outbound = new OutboundAllocationService(new InventoryService());
        var service = new SpreadsheetImportService(inbound, outbound);
        var bytes = Xlsx("Inbound", [
            ["IB-XLSX-004", Guid.NewGuid().ToString("D"), "1", "LOT-4", "", "PALLET-X4", "1"],
            ["IB-XLSX-005", "not-a-guid", "1", "LOT-5", "", "PALLET-X5", "1"]
        ]);

        var result = await service.ImportAsync(
            new MemoryStream(bytes),
            new SpreadsheetImportRequest(SpreadsheetImportType.Inbound, "partial-invalid"));

        Assert.Equal(SpreadsheetImportStatus.Rejected, result.Status);
        Assert.Contains(result.ErrorReport.Errors, error =>
            error.RowNumber == 4 && error.Column == "MaterialId" && error.Code == "INVALID_GUID");
        Assert.Empty(inbound.Orders);
    }

    [Fact]
    public async Task Duplicate_pallet_and_quantity_limit_are_reported_with_repair_guidance()
    {
        var inbound = new InboundOrderService();
        var outbound = new OutboundAllocationService(new InventoryService());
        var service = new SpreadsheetImportService(inbound, outbound);
        var pallet = "PALLET-DUP";
        var bytes = Xlsx("Inbound", [
            ["IB-XLSX-006", Guid.NewGuid().ToString("D"), "1000001", "LOT-6", "", pallet, "1"],
            ["IB-XLSX-007", Guid.NewGuid().ToString("D"), "1", "LOT-7", "", pallet, "1"]
        ]);

        var result = await service.ImportAsync(
            new MemoryStream(bytes),
            new SpreadsheetImportRequest(SpreadsheetImportType.Inbound, "duplicate-pallet"));

        Assert.Equal(SpreadsheetImportStatus.Rejected, result.Status);
        Assert.Contains(result.ErrorReport.Errors, error => error.Code == "QUANTITY_EXCEEDS_LIMIT");
        Assert.Contains(result.ErrorReport.Errors, error => error.Code == "DUPLICATE_PALLET");
        Assert.All(result.ErrorReport.Errors, error => Assert.False(string.IsNullOrWhiteSpace(error.Suggestion)));
        Assert.Empty(inbound.Orders);
    }

    [Fact]
    public async Task Same_source_and_file_digest_is_idempotent_but_a_changed_file_is_rejected()
    {
        var inbound = new InboundOrderService();
        var outbound = new OutboundAllocationService(new InventoryService());
        var service = new SpreadsheetImportService(inbound, outbound);
        var material = Guid.NewGuid().ToString("D");
        var bytes = Xlsx("Outbound", [["OB-XLSX-007", material, "1", "", "", ""]]);
        var request = new SpreadsheetImportRequest(SpreadsheetImportType.Outbound, "same-source");

        var first = await service.ImportAsync(new MemoryStream(bytes), request);
        var replay = await service.ImportAsync(new MemoryStream(bytes), request);
        var changed = await service.ImportAsync(
            new MemoryStream(Xlsx("Outbound", [["OB-XLSX-008", material, "1", "", "", ""]])), request);

        Assert.Equal(SpreadsheetImportStatus.Imported, first.Status);
        Assert.Equal(SpreadsheetImportStatus.AlreadyImported, replay.Status);
        Assert.Equal(SpreadsheetImportStatus.Rejected, changed.Status);
        Assert.Contains(changed.ErrorReport.Errors, error => error.Code == "SOURCE_KEY_CONFLICT");
        Assert.Single(outbound.Orders);
    }

    [Fact]
    public async Task Rejected_workbook_can_be_corrected_and_retried_with_the_same_source_key()
    {
        var inbound = new InboundOrderService();
        var outbound = new OutboundAllocationService(new InventoryService());
        var service = new SpreadsheetImportService(inbound, outbound);
        var sourceKey = "correct-after-error";

        var rejected = await service.ImportAsync(
            new MemoryStream(Xlsx("Inbound", [["IB-XLSX-008", "not-guid", "1", "LOT-8", "", "PALLET-X8", "1"]])),
            new SpreadsheetImportRequest(SpreadsheetImportType.Inbound, sourceKey));
        var accepted = await service.ImportAsync(
            new MemoryStream(Xlsx("Inbound", [["IB-XLSX-008", Guid.NewGuid().ToString("D"), "1", "LOT-8", "", "PALLET-X8", "1"]])),
            new SpreadsheetImportRequest(SpreadsheetImportType.Inbound, sourceKey));

        Assert.Equal(SpreadsheetImportStatus.Rejected, rejected.Status);
        Assert.Equal(SpreadsheetImportStatus.Imported, accepted.Status);
        Assert.Single(inbound.Orders);
    }

    private static byte[] Xlsx(string kind, IReadOnlyList<IReadOnlyList<string>> rows, string version = "1.0")
    {
        var headers = kind == "Inbound"
            ? new[] { "OrderNumber", "MaterialId", "Quantity", "BatchNumber", "ExpirationDate", "PalletCode", "WeightKg" }
            : new[] { "OrderNumber", "MaterialId", "Quantity", "BatchNumber", "PalletId", "LocationId" };
        var sheetRows = new List<string[]>
        {
            new[] { "TemplateVersion", version },
            headers
        };
        sheetRows.AddRange(rows.Select(row => row.ToArray()));
        var shared = sheetRows.SelectMany(row => row).Distinct(StringComparer.Ordinal).ToArray();
        var sharedIndex = shared.Select((value, index) => (value, index)).ToDictionary(item => item.value, item => item.index);
        var sheet = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var rowIndex = 0; rowIndex < sheetRows.Count; rowIndex++)
        {
            sheet.Append(CultureInfo.InvariantCulture, $"<row r=\"{rowIndex + 1}\">");
            for (var columnIndex = 0; columnIndex < sheetRows[rowIndex].Length; columnIndex++)
            {
                var reference = $"{(char)('A' + columnIndex)}{rowIndex + 1}";
                sheet.Append(CultureInfo.InvariantCulture, $"<c r=\"{reference}\" t=\"s\"><v>{sharedIndex[sheetRows[rowIndex][columnIndex]]}</v></c>");
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");
        var sharedXml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"").Append(shared.Length).Append("\" uniqueCount=\"").Append(shared.Length).Append("\">");
        foreach (var value in shared)
        {
            sharedXml.Append("<si><t>").Append(System.Security.SecurityElement.Escape(value)).Append("</t></si>");
        }

        sharedXml.Append("</sst>");
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add(archive, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/></Types>");
            Add(archive, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            Add(archive, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Import\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Add(archive, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/></Relationships>");
            Add(archive, "xl/worksheets/sheet1.xml", sheet.ToString());
            Add(archive, "xl/sharedStrings.xml", sharedXml.ToString());
        }

        return output.ToArray();
    }

    private static void Add(ZipArchive archive, string name, string contents)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }
}
