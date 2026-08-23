namespace PLCManagement.API.Models.Dtos
{
    public class GetAcceptanceByMoRequestDto
    {
        public required string MO_NO { get; set; }
    }

    public class AcceptanceInfoDto
    {
        public string TY_ID { get; set; }
        public string TY_NO { get; set; }
        public int ITM { get; set; }
        public string PRD_NO { get; set; }
        public string NAME { get; set; }
        public string PRD_NAME { get; set; }
        public string SPC { get; set; }
        public string UT { get; set; }
        public decimal QTY_CHK { get; set; }
        public decimal QTY_OK { get; set; }
        public string BIL_NO { get; set; }
        public string TI_NO { get; set; }
        public string SPC_NO { get; set; }
        public string PRC_ID { get; set; }
        public decimal QTY_LOST { get; set; }
        public decimal? QTY_OK_RTN { get; set; }
        public string CLS_ID_OK { get; set; }
        public string CLS_ID_LOST { get; set; }
        public int CHK_KND { get; set; }
        public int STAT { get; set; }
    }

    public class SaveAcceptanceRequestDto
    {
        public required string TY_ID { get; set; }
        public required string TY_NO { get; set; }
        public int ITM { get; set; }
        public required string MO_NO { get; set; }
        public decimal QTY_OK { get; set; }
        public decimal QTY_LOST { get; set; }
        public string? SPC_NO { get; set; }
        public string? PRC_ID { get; set; }
        public string? REM { get; set; }
    }

    public class SaveAcceptanceResponseDto
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public int ResultCode { get; set; } // 0-正常, 1-已转单
        public List<AcceptanceInfoDto> Data { get; set; }
    }

    public class DefectReasonDto
    {
        public string SPC_NO { get; set; }
        public string NAME { get; set; }
        public string SPC_NO_UP { get; set; }
    }

    public class CreateDefectReasonDto
    {
        public required string SPC_NO { get; set; }
        public required string NAME { get; set; }
        public string? SPC_NO_UP { get; set; }
    }

    public class UpdateDefectReasonDto
    {
        public required string NAME { get; set; }
        public string? SPC_NO_UP { get; set; }
    }
}