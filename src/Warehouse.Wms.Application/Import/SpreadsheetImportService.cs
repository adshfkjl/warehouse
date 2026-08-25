using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Warehouse.Wms.Application.Inbound;
using Warehouse.Wms.Application.Outbound;
using Warehouse.Wms.Domain.Outbound;

namespace Warehouse.Wms.Application.Import;

public enum SpreadsheetImportType
{
    Inbound,
    Outbound
}

public enum SpreadsheetImportStatus
{
    Imported,
    AlreadyImported,
    Rejected
}

public sealed record SpreadsheetImportRequest(SpreadsheetImportType Type, string SourceKey);

public sealed record SpreadsheetImportResult(
    Guid ImportId,
    SpreadsheetImportStatus Status,
    IReadOnlyList<string> OrderNumbers,
    ImportErrorReport ErrorReport)
{
    public bool Duplicate => Status == SpreadsheetImportStatus.AlreadyImported;
}

/// <summary>
/// Imports the versioned local workbook contract. Validation completes before
/// either domain service is called, so a bad row cannot create a partial order.
/// A successful import creates WMS documents only; PLC work starts in later tasks.
/// </summary>
public sealed class SpreadsheetImportService
{
    public const string TemplateVersion = "1.0";
    public const decimal MaximumQuantity = 1_000_000m;
    public const int QuantityScale = 3;

    private readonly object _gate = new();
    private readonly InboundOrderService _inboundOrders;
    private readonly OutboundAllocationService _outboundOrders;
    private readonly Dictionary<string, StoredImport> _importsBySource = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, ImportErrorReport> _errorReports = [];

    public SpreadsheetImportService(
        InboundOrderService inboundOrders,
        OutboundAllocationService outboundOrders)
    {
        _inboundOrders = inboundOrders ?? throw new ArgumentNullException(nameof(inboundOrders));
        _outboundOrders = outboundOrders ?? throw new ArgumentNullException(nameof(outboundOrders));
    }

    public async Task<SpreadsheetImportResult> ImportAsync(
        Stream workbook,
        SpreadsheetImportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workbook);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.SourceKey))
            throw new ArgumentException("A source key is required.", nameof(request));

        using var copy = new MemoryStream();
        await workbook.CopyToAsync(copy, cancellationToken);
        var bytes = copy.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes));
        var sourceKey = request.SourceKey.Trim();

        lock (_gate)
        {
            if (_importsBySource.TryGetValue(sourceKey, out var previous))
            {
                if (!string.Equals(previous.Digest, digest, StringComparison.Ordinal)
                    || previous.Type != request.Type)
                {
                    return RejectedAndRemember(
                        Guid.NewGuid(),
                        sourceKey,
                        digest,
                        [new ImportError(1, "SourceKey", "SOURCE_KEY_CONFLICT",
                            "来源键已关联另一份文件。", "更换来源键或提交原始文件。")]);
                }

                return new SpreadsheetImportResult(
                    previous.ImportId,
                    SpreadsheetImportStatus.AlreadyImported,
                    previous.OrderNumbers,
                    new ImportErrorReport());
            }
        }

        ParsedWorkbook parsed;
        try
        {
            parsed = Parse(bytes, request.Type);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Xml.XmlException)
        {
            return RejectedAndRemember(
                Guid.NewGuid(),
                sourceKey,
                digest,
                [new ImportError(1, "Workbook", "INVALID_WORKBOOK", exception.Message,
                    "使用项目提供的 xlsx 模板重新导出文件。")] );
        }
        if (parsed.Errors.Count > 0)
            return RejectedAndRemember(Guid.NewGuid(), sourceKey, digest, parsed.Errors);

        var importId = Guid.NewGuid();
        try
        {
            var orderNumbers = request.Type == SpreadsheetImportType.Inbound
                ? CreateInboundOrders(parsed.Rows, importId)
                : CreateOutboundOrders(parsed.Rows);
            var result = new SpreadsheetImportResult(
                importId,
                SpreadsheetImportStatus.Imported,
                orderNumbers,
                new ImportErrorReport());
            lock (_gate)
            {
                _importsBySource[sourceKey] = new StoredImport(digest, request.Type, importId, orderNumbers);
            }

            return result;
        }
        catch (InvalidOperationException exception)
        {
            return RejectedAndRemember(
                importId,
                sourceKey,
                digest,
                [new ImportError(1, "OrderNumber", "ORDER_CREATE_REJECTED", exception.Message,
                    "检查订单号是否已存在，并确认明细数量和状态符合规则。")] );
        }
    }

    public bool TryGetErrorReport(Guid importId, out ImportErrorReport report)
    {
        lock (_gate)
            return _errorReports.TryGetValue(importId, out report!);
    }

    private SpreadsheetImportResult RejectedAndRemember(
        Guid importId,
        string sourceKey,
        string digest,
        IReadOnlyList<ImportError> errors)
    {
        var result = Rejected(importId, errors);
        lock (_gate)
        {
            _errorReports[importId] = result.ErrorReport;
        }

        return result;
    }

    private static SpreadsheetImportResult Rejected(Guid importId, params ImportError[] errors)
        => new(importId, SpreadsheetImportStatus.Rejected, [], new ImportErrorReport(errors));

    private static SpreadsheetImportResult Rejected(Guid importId, IReadOnlyList<ImportError> errors)
        => new(importId, SpreadsheetImportStatus.Rejected, [], new ImportErrorReport(errors));

    private string[] CreateInboundOrders(IReadOnlyList<ImportRow> rows, Guid importId)
    {
        var orderNumbers = rows.Select(row => row.OrderNumber).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var orderNumber in orderNumbers)
        {
            if (_inboundOrders.Orders.Any(order => string.Equals(order.OrderNumber, orderNumber, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Inbound order '{orderNumber}' already exists.");
        }
        var existingPallets = _inboundOrders.PendingInboundInventory
            .Where(item => item.PalletCode is not null)
            .Select(item => item.PalletCode!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (rows.Any(row => row.PalletCode is not null && existingPallets.Contains(row.PalletCode)))
            throw new InvalidOperationException("An inbound pallet is already registered.");

        foreach (var group in rows.GroupBy(row => row.OrderNumber, StringComparer.Ordinal))
        {
            var order = _inboundOrders.Create(
                group.Key,
                group.Select(row => new InboundLineRequest(
                    row.MaterialId,
                    row.Quantity,
                    row.BatchNumber,
                    row.ExpirationDate)));
            var groupRows = group.ToArray();
            var lines = order.Lines.ToArray();
            for (var index = 0; index < groupRows.Length; index++)
            {
                var row = groupRows[index];
                var line = lines[index];
                _inboundOrders.Receive(
                    order.OrderNumber,
                    line.Id,
                    new InboundReceiptRequest(
                        $"spreadsheet:{importId:D}:row:{row.RowNumber}",
                        row.Quantity,
                        row.WeightKg,
                        row.BatchNumber,
                        row.ExpirationDate,
                        row.PalletCode));
            }
        }

        return orderNumbers;
    }

    private string[] CreateOutboundOrders(IReadOnlyList<ImportRow> rows)
    {
        var orderNumbers = rows.Select(row => row.OrderNumber).Distinct(StringComparer.Ordinal).ToArray();
        foreach (var orderNumber in orderNumbers)
        {
            if (_outboundOrders.Orders.Any(order => string.Equals(order.OrderNumber, orderNumber, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"Outbound order '{orderNumber}' already exists.");
        }

        foreach (var group in rows.GroupBy(row => row.OrderNumber, StringComparer.Ordinal))
        {
            _outboundOrders.Create(
                group.Key,
                group.Select(row => new OutboundLine(
                    row.MaterialId,
                    row.Quantity,
                    row.BatchNumber,
                    row.PalletId,
                    row.LocationId)));
        }

        return orderNumbers;
    }

    private static ParsedWorkbook Parse(byte[] bytes, SpreadsheetImportType type)
    {
        var rows = ReadRows(bytes);
        var errors = new List<ImportError>();
        if (rows.Count == 0)
        {
            errors.Add(new ImportError(1, "Workbook", "EMPTY_WORKBOOK", "工作簿没有可导入内容。", "使用项目模板填写至少一行明细。"));
            return new ParsedWorkbook([], errors);
        }

        var versionRow = rows[0];
        if (!string.Equals(Cell(versionRow, 0), "TemplateVersion", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Cell(versionRow, 1), TemplateVersion, StringComparison.Ordinal))
        {
            errors.Add(new ImportError(RowNumber(versionRow, 1), "TemplateVersion", "TEMPLATE_VERSION_UNSUPPORTED",
                $"模板版本必须为 {TemplateVersion}。", "下载当前版本模板后重新导入。"));
        }

        var expectedHeaders = type == SpreadsheetImportType.Inbound
            ? new[] { "OrderNumber", "MaterialId", "Quantity", "BatchNumber", "ExpirationDate", "PalletCode", "WeightKg" }
            : new[] { "OrderNumber", "MaterialId", "Quantity", "BatchNumber", "PalletId", "LocationId" };
        var headerRow = rows.Count > 1 ? rows[1] : new SpreadsheetRow(2, []);
        for (var i = 0; i < expectedHeaders.Length; i++)
        {
            if (!string.Equals(Cell(headerRow, i), expectedHeaders[i], StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ImportError(RowNumber(headerRow, 2), expectedHeaders[i], "HEADER_MISMATCH",
                    $"列 {expectedHeaders[i]} 缺失或顺序不正确。", "按模板列名和顺序填写后重新导入。"));
            }
        }

        if (errors.Count > 0 && errors.All(error => error.Code == "TEMPLATE_VERSION_UNSUPPORTED"))
        {
            // Continue parsing rows so callers receive all actionable errors when
            // a file contains both a version and data problem.
        }

        var parsed = new List<ImportRow>();
        var pallets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 2; index < rows.Count; index++)
        {
            var source = rows[index];
            var rowNumber = RowNumber(source, index + 1);
            if (source.Cells.All(string.IsNullOrWhiteSpace))
                continue;

            var orderNumber = Cell(source, 0).Trim();
            var materialText = Cell(source, 1).Trim();
            var quantityText = Cell(source, 2).Trim();
            if (string.IsNullOrWhiteSpace(orderNumber))
                errors.Add(Error(rowNumber, "OrderNumber", "REQUIRED", "订单号不能为空。", "填写唯一订单号。"));
            var materialValid = true;
            Guid materialId;
            if (string.IsNullOrWhiteSpace(materialText))
            {
                materialValid = false;
                materialId = Guid.Empty;
                errors.Add(Error(rowNumber, "MaterialId", "REQUIRED", "物料编号不能为空。", "填写系统中的物料 GUID。"));
            }
            else if (!Guid.TryParse(materialText, out materialId) || materialId == Guid.Empty)
            {
                materialValid = false;
                errors.Add(Error(rowNumber, "MaterialId", "INVALID_GUID", "物料编号不是有效 GUID。", "填写系统中的物料 GUID。"));
            }

            var quantityValid = true;
            decimal quantity;
            if (string.IsNullOrWhiteSpace(quantityText))
            {
                quantityValid = false;
                quantity = 0m;
                errors.Add(Error(rowNumber, "Quantity", "REQUIRED", "数量不能为空。", "填写正数数量。"));
            }
            else if (!TryQuantity(quantityText, out quantity, out var quantityCode))
            {
                quantityValid = false;
                errors.Add(Error(rowNumber, "Quantity", quantityCode!, quantityCode == "QUANTITY_EXCEEDS_LIMIT" ? "数量超过导入上限。" : "数量必须是正数且最多三位小数。", "修正数量后重新导入。"));
            }

            var batch = NullIfEmpty(Cell(source, 3));
            DateOnly? expiration = null;
            if (type == SpreadsheetImportType.Inbound && !string.IsNullOrWhiteSpace(Cell(source, 4)))
            {
                if (!DateOnly.TryParseExact(Cell(source, 4).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    errors.Add(Error(rowNumber, "ExpirationDate", "INVALID_DATE", "有效期不是有效日期。", "使用 yyyy-MM-dd 日期格式。"));
                else
                    expiration = date;
            }

            string? palletCode = null;
            Guid? palletId = null;
            Guid? locationId = null;
            decimal weight = 0m;
            if (type == SpreadsheetImportType.Inbound)
            {
                palletCode = NullIfEmpty(Cell(source, 5));
                if (palletCode is null)
                    errors.Add(Error(rowNumber, "PalletCode", "REQUIRED", "入库托盘号不能为空。", "填写唯一托盘号。"));
                else if (!pallets.Add(palletCode))
                    errors.Add(Error(rowNumber, "PalletCode", "DUPLICATE_PALLET", "同一文件中托盘号重复。", "每个托盘只保留一行或使用不同托盘号。"));
                var weightText = NullIfEmpty(Cell(source, 6));
                if (weightText is not null && (!decimal.TryParse(weightText, NumberStyles.Number, CultureInfo.InvariantCulture, out weight) || weight < 0m))
                    errors.Add(Error(rowNumber, "WeightKg", "INVALID_DECIMAL", "重量必须是非负数字。", "修正重量后重新导入。"));
            }
            else
            {
                var palletText = NullIfEmpty(Cell(source, 4));
                if (palletText is not null)
                {
                    if (!Guid.TryParse(palletText, out var parsedPallet) || parsedPallet == Guid.Empty)
                        errors.Add(Error(rowNumber, "PalletId", "INVALID_GUID", "托盘编号不是有效 GUID。", "填写有效托盘 GUID 或留空。"));
                    else
                        palletId = parsedPallet;
                }

                var locationText = NullIfEmpty(Cell(source, 5));
                if (locationText is not null)
                {
                    if (!Guid.TryParse(locationText, out var parsedLocation) || parsedLocation == Guid.Empty)
                        errors.Add(Error(rowNumber, "LocationId", "INVALID_GUID", "库位编号不是有效 GUID。", "填写有效库位 GUID 或留空。"));
                    else
                        locationId = parsedLocation;
                }
            }

            if (!string.IsNullOrWhiteSpace(orderNumber)
                && materialValid
                && quantityValid
                && materialId != Guid.Empty
                && quantity > 0m
                && quantity <= MaximumQuantity)
            {
                parsed.Add(new ImportRow(rowNumber, orderNumber, materialId, quantity, batch, expiration, palletCode, palletId, locationId, weight));
            }
        }

        return new ParsedWorkbook(parsed, errors);
    }

    private static ImportError Error(int row, string column, string code, string message, string suggestion)
        => new(row, column, code, message, suggestion);

    private static bool TryQuantity(string text, out decimal quantity, out string? errorCode)
    {
        quantity = 0m;
        errorCode = null;
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out quantity) || quantity <= 0m)
        {
            errorCode = "INVALID_DECIMAL";
            return false;
        }

        if (quantity > MaximumQuantity)
        {
            errorCode = "QUANTITY_EXCEEDS_LIMIT";
            return false;
        }

        if (decimal.Round(quantity, QuantityScale) != quantity)
        {
            errorCode = "QUANTITY_PRECISION";
            return false;
        }

        return true;
    }

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Cell(SpreadsheetRow row, int index)
        => index < row.Cells.Count ? row.Cells[index] : string.Empty;

    private static int RowNumber(SpreadsheetRow row, int fallback) => row.RowNumber > 0 ? row.RowNumber : fallback;

    private static IReadOnlyList<SpreadsheetRow> ReadRows(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4b)
            return ReadXlsx(bytes);

        var text = Encoding.UTF8.GetString(bytes);
        var rows = new List<SpreadsheetRow>();
        var rowNumber = 1;
        foreach (var line in text.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            rows.Add(new SpreadsheetRow(rowNumber++, line.Split(',').Select(value => value.Trim().Trim('"')).ToArray()));
        }

        return rows;
    }

    private static SpreadsheetRow[] ReadXlsx(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var shared = ReadSharedStrings(archive);
        var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")
            ?? throw new InvalidDataException("The workbook has no first worksheet.");
        var document = XDocument.Load(sheet.Open());
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var rows = new List<SpreadsheetRow>();
        foreach (var row in document.Descendants(ns + "row"))
        {
            var number = (int?)row.Attribute("r") ?? rows.Count + 1;
            var cells = new List<string>();
            foreach (var cell in row.Elements(ns + "c"))
            {
                var reference = (string?)cell.Attribute("r") ?? string.Empty;
                var column = ColumnIndex(reference);
                while (cells.Count <= column)
                    cells.Add(string.Empty);
                var value = cell.Element(ns + "v")?.Value ?? cell.Element(ns + "is")?.Element(ns + "t")?.Value ?? string.Empty;
                if (string.Equals((string?)cell.Attribute("t"), "s", StringComparison.Ordinal)
                    && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sharedIndex)
                    && sharedIndex >= 0 && sharedIndex < shared.Length)
                {
                    value = shared[sharedIndex];
                }

                cells[column] = value;
            }

            rows.Add(new SpreadsheetRow(number, cells));
        }

        return rows.OrderBy(row => row.RowNumber).ToArray();
    }

    private static string[] ReadSharedStrings(ZipArchive archive)
    {
        var entry = archive.GetEntry("xl/sharedStrings.xml");
        if (entry is null)
            return [];
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var document = XDocument.Load(entry.Open());
        return document.Descendants(ns + "si")
            .Select(item => string.Concat(item.Descendants(ns + "t").Select(text => text.Value)))
            .ToArray();
    }

    private static int ColumnIndex(string reference)
    {
        var result = 0;
        foreach (var character in reference.TakeWhile(char.IsLetter))
            result = result * 26 + (char.ToUpperInvariant(character) - 'A' + 1);
        return Math.Max(0, result - 1);
    }

    private sealed record SpreadsheetRow(int RowNumber, IReadOnlyList<string> Cells);

    private sealed record ImportRow(
        int RowNumber,
        string OrderNumber,
        Guid MaterialId,
        decimal Quantity,
        string? BatchNumber,
        DateOnly? ExpirationDate,
        string? PalletCode,
        Guid? PalletId,
        Guid? LocationId,
        decimal WeightKg);

    private sealed record ParsedWorkbook(IReadOnlyList<ImportRow> Rows, IReadOnlyList<ImportError> Errors);

    private sealed record StoredImport(string Digest, SpreadsheetImportType Type, Guid ImportId, IReadOnlyList<string> OrderNumbers);
}
