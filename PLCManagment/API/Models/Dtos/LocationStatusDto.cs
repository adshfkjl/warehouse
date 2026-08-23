using System.ComponentModel.DataAnnotations;

namespace PLCManagement.API.Models.Dtos
{
    public class LoadingPointStatusDto
    {
        public required string PlcId { get; set; }
        public int LoadingPoint { get; set; }
        public bool IsEmpty { get; set; }
        public string CurrentPalletNumber { get; set; }
    }

    public class StorageLocationStatusDto
    {
        public required string PlcId { get; set; }
        public int Shelf { get; set; }
        public int Position { get; set; }
        public int Status { get; set; } // 0-空, 1-有货, 2-停用
        public string StatusDescription { get; set; }
    }

    public class LocationCheckRequestDto
    {
        public required string PlcId { get; set; }
        public int Shelf { get; set; }
        public int Position { get; set; }
        public int LoadingPoint { get; set; }
    }

    /// <summary>
    /// 预上架货框登记请求
    /// </summary>
    public class TrayPreUploadRequest
    {
        /// <summary>
        /// 单据编号
        /// </summary>
        [Required]
        public string DocumentNo { get; set; } = string.Empty;

        /// <summary>
        /// PLC编号
        /// </summary>
        [Required]
        public string PLCID { get; set; } = string.Empty;

        /// <summary>
        /// 装配载点 (0-外装载点, 1-内装载点)
        /// </summary>
        [Required]
        [Range(0, 1, ErrorMessage = "装配载点必须是0或1")]
        public int LoadingPoint { get; set; }

        /// <summary>
        /// 托盘编号（可选）
        /// </summary>
        public string? PalletCode { get; set; }

        /// <summary>
        /// 备注信息
        /// </summary>
        public string? Remark { get; set; }
    }

    /// <summary>
    /// 预上架货框登记响应
    /// </summary>
    public class TrayPreUploadResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int ResultFlag { get; set; }
        public string? ErrorMessage { get; set; }
        public string? PalletCode { get; set; }
        public DateTime? CreateTime { get; set; }
    }
}