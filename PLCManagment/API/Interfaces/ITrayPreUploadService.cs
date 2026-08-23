using PLCManagement.API.Models.Dtos;

namespace PLCManagement.API.Interfaces
{
    /// <summary>
    /// 预上架货框登记服务接口
    /// </summary>
    public interface ITrayPreUploadService
    {
        /// <summary>
        /// 登记预上架货框
        /// </summary>
        /// <param name="request">预上架请求</param>
        /// <returns>处理结果</returns>
        Task<TrayPreUploadResponse> RegisterPreUploadTrayAsync(TrayPreUploadRequest request);

        /// <summary>
        /// 查询预上架记录
        /// </summary>
        /// <param name="documentNo">单据编号</param>
        /// <param name="plcId">PLC编号</param>
        /// <returns>预上架记录列表</returns>
        Task<List<TrayPreUploadRecordDto>> GetPreUploadRecordsAsync(string documentNo = null, string plcId = null);
    }

    /// <summary>
    /// 预上架记录DTO
    /// </summary>
    public class TrayPreUploadRecordDto
    {
        public int Id { get; set; }
        public string DocumentNo { get; set; } = string.Empty;
        public string PLCID { get; set; } = string.Empty;
        public int LoadingPoint { get; set; }
        public string PalletCode { get; set; } = string.Empty;
        public string Remark { get; set; } = string.Empty;
        public DateTime CreateTime { get; set; }
        public string Status { get; set; } = string.Empty;
    }
}