namespace PLCManagement.API.Models
{
    public class DocumentDownLoadRequest
    {
        public string DocumentNo { get; set; } = string.Empty;    // 单据编号
        public string LoadingPoint { get; set; } = string.Empty;  // 装载点信息
    }
    //DocumentUploadRequest
    public class DocumentUpLoadRequest
    {
        public string DocumentNo { get; set; } = string.Empty;    // 单据编号
        public string StorageLocation { get; set; } = string.Empty; // 储位信息
    }

    public class DocumentOperationResult
    {
        public int ResultFlag { get; set; }           // 结果标志：1-成功，0-异常
        public required string PLCID { get; set; } = "";          // PLC标识
        public int Shelf { get; set; }           // 货架信息
        public int StorageLocation { get; set; } // 储位信息
        public int LoadPoint { get; set; }           // 结果标志：0-外装载点，1-内装载点
        public string? DocumentNo { get; set; }      // 单据编号
        public string? DocumentType { get; set; }    // 单据类别
        public int ItemSequence { get; set; }    // 项次信息
        public string? ErrorMessage { get; set; }    // 错误信息（结果标志为0时）
        public double qty { get; set; }    // 出入库数量
        public string? rem { get; set; }    // 备注信息
    }
}