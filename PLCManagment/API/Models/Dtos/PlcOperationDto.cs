// PLCManagement.API/Models/Dtos/PlcOperationDto.cs
using System.ComponentModel.DataAnnotations;

namespace PLCManagement.API.Models.Dtos
{
    public class OutboundRequestDto
    {
        public required string PlcId { get; set; }
        public int Shelf { get; set; }          // outShelf
        public int Position { get; set; }       // selectedOutPosition
        public int LoadingPoint { get; set; }   // loadingPoint
    }

    public class InboundRequestDto
    {
        public required string PlcId { get; set; }
        public int Shelf { get; set; }          // inShelf
        public int Position { get; set; }       // selectedInPosition
        public int LoadingPoint { get; set; }   // loadingPoint
    }

    public class TransferRequestDto
    {
        public required string PlcId { get; set; }
        public int OutShelf { get; set; }
        public int OutPosition { get; set; }    // selectedOutPosition
        public int InShelf { get; set; }
        public int InPosition { get; set; }     // selectedInPosition
    }

    public class PlcParameterDto
    {
        public required string PlcId { get; set; }
        public int MainSpeed { get; set; }
        public int AuxSpeed { get; set; }
        public decimal ForkLength { get; set; }
    }

    public class PlcStatusDto
    {
        public bool Online { get; set; }
        public int Working { get; set; }
        public int WorkingType { get; set; }
        public int TaskStatus { get; set; }
        public bool HasGoods { get; set; }
        public int ShelfHasGoods { get; set; }
        public int LoadingPointStatus { get; set; }
        public int ErrorCode { get; set; }
        public string ErrorMSG { get; set; }
        public float XPosition { get; set; }
        public float YPosition { get; set; }
        public float ZPosition { get; set; }
        public int MainSpeed { get; set; }
        public int AuxSpeed { get; set; }
        public decimal ForkLength { get; set; }
        public bool InboundCompleted { get; set; }
        public bool OutboundCompleted { get; set; }
        public bool TransferCompleted { get; set; }
        public float WeightA { get; set; }
        public float WeightB { get; set; }

    }
    public class ImportBillDto
    {
        public required string Bil_ID { get; set; }

        public required string Bil_NO { get; set; }
    }

}