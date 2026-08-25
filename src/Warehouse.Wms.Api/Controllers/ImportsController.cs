using Microsoft.AspNetCore.Mvc;
using Warehouse.Wms.Application.Import;

namespace Warehouse.Wms.Api.Controllers;

[ApiController]
[Route("api/imports")]
public sealed class ImportsController(SpreadsheetImportService service) : ControllerBase
{
    private readonly SpreadsheetImportService _service = service ?? throw new ArgumentNullException(nameof(service));

    [HttpPost("{type}")]
    [RequestSizeLimit(20 * 1024 * 1024)]
    public async Task<IActionResult> Import(
        string type,
        IFormFile? file,
        [FromHeader(Name = "X-Source-Key")] string? sourceKey,
        CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<SpreadsheetImportType>(type, true, out var importType))
            return BadRequest(new { code = "INVALID_IMPORT_TYPE", message = "导入类型必须为 inbound 或 outbound。" });
        if (file is null || file.Length == 0)
            return BadRequest(new { code = "FILE_REQUIRED", message = "必须上传非空 Excel 文件。" });
        if (string.IsNullOrWhiteSpace(sourceKey))
            return BadRequest(new { code = "SOURCE_KEY_REQUIRED", message = "必须提供 X-Source-Key。" });

        await using var stream = file.OpenReadStream();
        var result = await _service.ImportAsync(
            stream,
            new SpreadsheetImportRequest(importType, sourceKey),
            cancellationToken);
        if (result.Status == SpreadsheetImportStatus.Rejected)
        {
            return UnprocessableEntity(new
            {
                result.ImportId,
                result.Status,
                errors = result.ErrorReport.Errors,
                errorReportUrl = Url is null
                    ? $"/api/imports/{result.ImportId:D}/errors"
                    : Url.ActionLink(nameof(DownloadErrors), values: new { importId = result.ImportId })
            });
        }

        return Ok(result);
    }

    [HttpGet("{importId:guid}/errors")]
    public IActionResult DownloadErrors(Guid importId)
    {
        return _service.TryGetErrorReport(importId, out var report)
            ? File(report.ToCsvBytes(), "text/csv; charset=utf-8", $"import-errors-{importId:D}.csv")
            : NotFound(new { code = "IMPORT_ERROR_REPORT_NOT_FOUND" });
    }
}
